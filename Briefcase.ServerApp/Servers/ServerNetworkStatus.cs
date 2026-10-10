using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

public enum NetworkCheck { Unknown, Ready, Failed, Disabled }

public sealed record ServerNetworkStatus(string Address, NetworkCheck Firewall,
    NetworkCheck Upnp, NetworkCheck Eos)
{
    public static readonly ServerNetworkStatus Unknown = new("", NetworkCheck.Unknown,
        NetworkCheck.Unknown, NetworkCheck.Unknown);
}

/// <summary>Reads the dedicated server's own network checks from its current Unreal log.</summary>
public static partial class ServerNetworkStatusReader
{
    private const int MaximumLogBytes = 8 * 1024 * 1024;

    public static ServerNetworkStatus Read(ServerLocation location, DateTime processStartedUtc)
    {
        var logs = Path.Combine(location.InstallRoot, "DeceiveInc", "Saved", "Logs");
        var (gamePort, queryPort) = ServerProcess.ReadPorts(location.ConfigFile);
        return ReadFromDirectory(logs, processStartedUtc, gamePort, queryPort,
            location.IsInstance ? location.ConfigFile : null);
    }

    public static ServerNetworkStatus ReadFromDirectory(string logs, DateTime processStartedUtc,
        int gamePort, int queryPort, string? instanceConfig = null)
    {
        try
        {
            if (!Directory.Exists(logs)) return ServerNetworkStatus.Unknown;
            foreach (var file in new DirectoryInfo(logs).EnumerateFiles("DeceiveInc*.log")
                         .Where(file => file.LastWriteTimeUtc >= processStartedUtc.AddSeconds(-2))
                         .OrderByDescending(file => file.LastWriteTimeUtc).Take(24))
            {
                try
                {
                    if (file.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var log = ReadBounded(file.FullName, file.Length);
                    if (!BelongsToServer(log, gamePort, queryPort, instanceConfig)) continue;
                    return ParseLog(log);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Another process may rotate this log while it is being read.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException)
        {
            // A rotating or inaccessible log must never interrupt server management.
        }
        return ServerNetworkStatus.Unknown;
    }

    public static ServerNetworkStatus ParseLog(string log)
    {
        var firewall = NetworkCheck.Unknown;
        var upnp = NetworkCheck.Unknown;
        var eos = NetworkCheck.Unknown;
        var address = "";
        foreach (var line in log.Split('\n'))
        {
            if (line.Contains("[NetPosture] Windows Firewall (server UDP ports): ",
                    StringComparison.Ordinal))
            {
                var value = line[(line.LastIndexOf(": ", StringComparison.Ordinal) + 2)..].Trim();
                firewall = value.StartsWith("Allowed", StringComparison.OrdinalIgnoreCase)
                    ? NetworkCheck.Ready : value.StartsWith("Blocked", StringComparison.OrdinalIgnoreCase)
                        ? NetworkCheck.Failed : NetworkCheck.Unknown;
            }
            else if (line.Contains("[NetPosture] UPnP: ", StringComparison.Ordinal))
            {
                var value = line[(line.IndexOf("[NetPosture] UPnP: ", StringComparison.Ordinal) +
                                  "[NetPosture] UPnP: ".Length)..].Trim();
                upnp = value.StartsWith("Mapped", StringComparison.OrdinalIgnoreCase)
                    ? NetworkCheck.Ready : value.StartsWith("disabled", StringComparison.OrdinalIgnoreCase)
                        ? NetworkCheck.Disabled : value.StartsWith("Failed", StringComparison.OrdinalIgnoreCase)
                            ? NetworkCheck.Failed : NetworkCheck.Unknown;
            }
            if (line.Contains("OnCreateSessionComplete: SessionName:GameSession Success:",
                    StringComparison.Ordinal))
                eos = line.Contains("Success:1", StringComparison.Ordinal)
                    ? NetworkCheck.Ready : line.Contains("Success:0", StringComparison.Ordinal)
                        ? NetworkCheck.Failed : NetworkCheck.Unknown;
            else if (line.Contains("OnDestroySessionComplete: SessionName:GameSession Success:1",
                         StringComparison.Ordinal))
                eos = NetworkCheck.Unknown;

            if (line.Contains("[NetPosture] Public IP (ipify): ", StringComparison.Ordinal) ||
                line.Contains("[NetPosture] Advertising PublicAddress=", StringComparison.Ordinal))
            {
                var match = AddressPattern().Match(line);
                if (match.Success && IPAddress.TryParse(match.Groups["ip"].Value, out var ip) &&
                    ip.AddressFamily == AddressFamily.InterNetwork &&
                    int.TryParse(match.Groups["port"].Value, out var port) && port is > 0 and <= 65535)
                    address = $"{ip}:{port}";
            }
        }
        return new ServerNetworkStatus(address, firewall, upnp, eos);
    }

    private static bool BelongsToServer(string log, int gamePort, int queryPort, string? instanceConfig)
    {
        var command = log.Split('\n').FirstOrDefault(line =>
            line.StartsWith("LogInit: Command Line:", StringComparison.Ordinal));
        if (command is null ||
            !command.Contains($"-Port={gamePort} ", StringComparison.OrdinalIgnoreCase) ||
            !command.Contains($"-QueryPort={queryPort}", StringComparison.OrdinalIgnoreCase))
            return false;
        if (instanceConfig is null) return true;
        var match = InstanceConfigPattern().Match(command);
        return match.Success && string.Equals(
            match.Groups["quoted"].Success ? match.Groups["quoted"].Value : match.Groups["plain"].Value,
            instanceConfig, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadBounded(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (length <= MaximumLogBytes)
            using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                return reader.ReadToEnd();
        // Keep the command line at the start and recent state changes at the end.
        var head = new byte[128 * 1024];
        var headLength = stream.Read(head);
        stream.Seek(-2 * 1024 * 1024, SeekOrigin.End);
        using var tailReader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        return Encoding.UTF8.GetString(head, 0, headLength) + "\n" + tailReader.ReadToEnd();
    }

    [GeneratedRegex(@"(?<ip>(?:\d{1,3}\.){3}\d{1,3}):(?<port>\d{1,5})")]
    private static partial Regex AddressPattern();

    [GeneratedRegex("-TripwireServerIni=(?:\"(?<quoted>[^\"]+)\"|(?<plain>\\S+))",
        RegexOptions.IgnoreCase)]
    private static partial Regex InstanceConfigPattern();
}
