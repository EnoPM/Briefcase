using System.Diagnostics;
using System.Globalization;
using System.Text;
using ServerAdminControl.Protocol;

namespace ServerAdminControl.Server;

/// <summary>
/// Provides a typed, bounded view over TripwireServer.ini. Unknown sections,
/// keys, and comments are preserved when known values are updated.
/// </summary>
internal sealed class ServerConfigurationService
{
    private const string Section = "/Script/DeceiveInc.TripwireServerSettings";
    private static readonly string[] DefaultMapRotation =
        ["DI_Hardsell", "DI_SR", "DI_DS", "DI_FS", "DI_SE", "DI_FSN", "DI_HSD"];
    private static readonly HashSet<string> Regions = new(
        ["", "us-east", "us-central", "us-west", "eu", "oce", "br", "asia", "me"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> GameModes = new(
        ["Solo", "Duo", "Trio"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> BotDifficulties = new(
        ["Easy", "Normal", "Difficult"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> MapCodes = new(
        DefaultMapRotation, StringComparer.OrdinalIgnoreCase);
    private readonly string _executablePath;
    private readonly string _workingDirectory;
    private readonly string _frameworkDirectory;
    private readonly string _configurationPath;
    private int _restartScheduled;

    public ServerConfigurationService() : this(GetDefaultPaths()) { }

    private ServerConfigurationService(DefaultPaths paths) : this(
        paths.ExecutablePath,
        paths.ConfigurationPath,
        paths.FrameworkDirectory) { }

    internal ServerConfigurationService(
        string executablePath,
        string configurationPath,
        string frameworkDirectory)
    {
        _executablePath = Path.GetFullPath(executablePath);
        _workingDirectory = Path.GetDirectoryName(_executablePath) ??
            throw new InvalidOperationException("The server working directory is unavailable.");
        _frameworkDirectory = Path.GetFullPath(frameworkDirectory);
        _configurationPath = Path.GetFullPath(configurationPath);
    }

    public ServerConfigurationEnvelope Snapshot()
    {
        var section = ReadSection();
        var values = section.Values;
        return new ServerConfigurationEnvelope(
            ServerName: ReadString(values, "ServerName", "Deceive Inc. Server"),
            Region: ReadString(values, "ServerRegion", "eu"),
            GameMode: ReadString(values, "GameMode", "Solo"),
            MapRotation: section.MapRotation.Count == 0
                ? DefaultMapRotation
                : section.MapRotation,
            Password: ReadString(values, "Password", ""),
            AdminPassword: ReadString(values, "AdminPassword", ""),
            Crossplay: ReadBool(values, "bCrossplay", true),
            IsPublic: ReadBool(values, "bIsPublic", true),
            GamePort: ReadInt(values, "GamePort", 50000),
            QueryPort: ReadInt(values, "QueryPort", 50001),
            EnableUpnp: ReadBool(values, "bEnableUPnP", true),
            AutoShutdownEmptyMinutes: ReadFloat(values, "AutoShutdownEmptyMinutes", 0),
            SandboxMode: ReadBool(values, "bSandboxMode", false),
            FillWithBots: ReadBool(values, "bFillWithBots", true),
            BotsDifficulty: ReadString(values, "BotsDifficulty", "Normal"),
            BotsAmount: ReadInt(values, "BotsAmount", 0),
            MaxPlayers: ReadInt(values, "MaxPlayers", 8),
            RandomizeMap: ReadBool(values, "bRandomizeMap", false),
            CivilianHeatPercent: ReadInt(values, "HeatPercentDamagingCivilian", 34),
            StaffHeatPercent: ReadInt(values, "HeatPercentDamagingStaff", 34),
            GuardHeatPercent: ReadInt(values, "HeatPercentDamagingGuard", 17),
            TechnicianHeatPercent: ReadInt(values, "HeatPercentDamagingTechnician", 34),
            VipHeatPercent: ReadInt(values, "HeatPercentDamagingVIP", 100),
            ScoldHeatPerSecond: ReadFloat(values, "ScoldHeatPerSecond", 1.5f),
            SpyHitHeatDelaySeconds: ReadFloat(values, "HeatDelayForSpyHit", 2.5f),
            PassiveHeatGainDelaySeconds: ReadFloat(values, "HeatDelayPassiveGain", 5),
            AggroAfterCoverHeatDelaySeconds: ReadFloat(
                values, "HeatDelayAggroPostCover", 5),
            HeatDecayDelaySeconds: ReadFloat(values, "HeatDelayToDecay", 1),
            HeatDecayRate: ReadFloat(values, "HeatDecayRate", 1.35f),
            ProcessId: Environment.ProcessId,
            ExecutablePath: _executablePath,
            ConfigurationPath: _configurationPath);
    }

    public ServerConfigurationEnvelope Update(ServerConfigurationEnvelope requested)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var current = Snapshot();
        var serverName = ValidateText(requested.ServerName, "Server name", 64, allowEmpty: false);
        var region = ValidateText(requested.Region, "Region", 24, allowEmpty: true);
        var gameMode = ValidateText(requested.GameMode, "Game mode", 32, allowEmpty: false);
        var password = ValidateText(requested.Password, "Password", 128, allowEmpty: true);
        // AdminPassword is never accepted through the unencrypted TCP
        // protocol. Rotate it locally with the provisioning script so a new
        // credential never appears in an administration frame.
        var adminPassword = current.AdminPassword;
        var difficulty = ValidateText(
            requested.BotsDifficulty, "Bot difficulty", 32, allowEmpty: false);
        ValidateChoice(region, Regions, "Region");
        ValidateChoice(gameMode, GameModes, "Game mode");
        ValidateChoice(difficulty, BotDifficulties, "Bot difficulty");
        ValidateRange(requested.GamePort, 1024, 65535, "Game port");
        ValidateRange(requested.QueryPort, 1024, 65535, "Query port");
        if (requested.GamePort == requested.QueryPort)
            throw new InvalidOperationException("Game port and query port must differ.");
        ValidateRange(requested.BotsAmount, 0, 8, "Bot amount");
        ValidateRange(
            requested.MaxPlayers, 1, ServerAdminProtocol.MaximumPlayerCount,
            "Maximum players");
        ValidateRange(requested.AutoShutdownEmptyMinutes, 0, 1440,
            "Auto-shutdown delay");
        ValidateRange(requested.CivilianHeatPercent, -1, 100, "Civilian heat percentage");
        ValidateRange(requested.StaffHeatPercent, -1, 100, "Staff heat percentage");
        ValidateRange(requested.GuardHeatPercent, -1, 100, "Guard heat percentage");
        ValidateRange(requested.TechnicianHeatPercent, -1, 100,
            "Technician heat percentage");
        ValidateRange(requested.VipHeatPercent, -1, 100, "VIP heat percentage");
        ValidateRange(requested.ScoldHeatPerSecond, 0, 10, "Scold heat per second");
        ValidateRange(requested.SpyHitHeatDelaySeconds, 0, 30, "Spy-hit heat delay");
        ValidateRange(requested.PassiveHeatGainDelaySeconds, 0, 30,
            "Passive heat-gain delay");
        ValidateRange(requested.AggroAfterCoverHeatDelaySeconds, 0, 30,
            "Post-cover aggro heat delay");
        ValidateRange(requested.HeatDecayDelaySeconds, 0, 30, "Heat decay delay");
        ValidateRange(requested.HeatDecayRate, 0, 10, "Heat decay rate");

        var mapRotation = requested.MapRotation?
            .Select(code => ValidateText(code, "Map code", 32, allowEmpty: false))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
        if (mapRotation.Length == 0)
            throw new InvalidOperationException("Map rotation must contain at least one map.");
        foreach (var mapCode in mapRotation)
            ValidateChoice(mapCode, MapCodes, "Map code");

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ServerName"] = serverName,
            ["ServerRegion"] = region,
            ["GameMode"] = gameMode,
            ["Password"] = password,
            ["AdminPassword"] = adminPassword,
            ["bCrossplay"] = FormatBool(requested.Crossplay),
            ["bIsPublic"] = FormatBool(requested.IsPublic),
            ["GamePort"] = requested.GamePort.ToString(CultureInfo.InvariantCulture),
            ["QueryPort"] = requested.QueryPort.ToString(CultureInfo.InvariantCulture),
            ["bEnableUPnP"] = FormatBool(requested.EnableUpnp),
            ["AutoShutdownEmptyMinutes"] = FormatFloat(requested.AutoShutdownEmptyMinutes),
            ["bSandboxMode"] = FormatBool(requested.SandboxMode),
            ["bFillWithBots"] = FormatBool(requested.FillWithBots),
            ["BotsDifficulty"] = difficulty,
            ["BotsAmount"] = requested.BotsAmount.ToString(CultureInfo.InvariantCulture),
            ["MaxPlayers"] = requested.MaxPlayers.ToString(CultureInfo.InvariantCulture),
            ["bRandomizeMap"] = FormatBool(requested.RandomizeMap),
            ["HeatPercentDamagingCivilian"] = requested.CivilianHeatPercent.ToString(
                CultureInfo.InvariantCulture),
            ["HeatPercentDamagingStaff"] = requested.StaffHeatPercent.ToString(
                CultureInfo.InvariantCulture),
            ["HeatPercentDamagingGuard"] = requested.GuardHeatPercent.ToString(
                CultureInfo.InvariantCulture),
            ["HeatPercentDamagingTechnician"] = requested.TechnicianHeatPercent.ToString(
                CultureInfo.InvariantCulture),
            ["HeatPercentDamagingVIP"] = requested.VipHeatPercent.ToString(
                CultureInfo.InvariantCulture),
            ["ScoldHeatPerSecond"] = FormatFloat(requested.ScoldHeatPerSecond),
            ["HeatDelayForSpyHit"] = FormatFloat(requested.SpyHitHeatDelaySeconds),
            ["HeatDelayPassiveGain"] = FormatFloat(requested.PassiveHeatGainDelaySeconds),
            ["HeatDelayAggroPostCover"] = FormatFloat(
                requested.AggroAfterCoverHeatDelaySeconds),
            ["HeatDelayToDecay"] = FormatFloat(requested.HeatDecayDelaySeconds),
            ["HeatDecayRate"] = FormatFloat(requested.HeatDecayRate)
        };
        WriteSection(replacements, mapRotation);
        return Snapshot();
    }

    public void ScheduleRestart(Action<string> info)
    {
        if (Interlocked.Exchange(ref _restartScheduled, 1) != 0)
            throw new InvalidOperationException("A server restart is already scheduled.");

        var snapshot = Snapshot();
        Directory.CreateDirectory(_frameworkDirectory);
        var script = Path.Combine(
            _frameworkDirectory, $"restart-server-{Environment.ProcessId}.bat");
        var scriptText = CreateRestartScript(
            Environment.ProcessId, _workingDirectory, _executablePath, snapshot.GamePort);
        File.WriteAllText(script, scriptText, new UTF8Encoding(false));

        var launcher = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _workingDirectory
        };
        launcher.ArgumentList.Add("/d");
        launcher.ArgumentList.Add("/c");
        launcher.ArgumentList.Add(script);
        using var restartHelper = Process.Start(launcher) ??
            throw new InvalidOperationException("The server restart helper could not start.");
        info($"Server restart scheduled with game port {snapshot.GamePort}.");

        _ = Task.Run(async () =>
        {
            await Task.Delay(1_000);
            Environment.Exit(0);
        });
    }

    private ParsedSection ReadSection()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mapRotation = new List<string>();
        if (!File.Exists(_configurationPath)) return new ParsedSection(values, mapRotation);
        var inSection = false;
        foreach (var rawLine in File.ReadAllLines(_configurationPath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                inSection = line[1..^1].Equals(Section, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection || line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            var bareKey = key.TrimStart('+', '-', '.', '!');
            if (bareKey.Equals("MapRotation", StringComparison.OrdinalIgnoreCase))
            {
                if (key.StartsWith('!')) mapRotation.Clear();
                else if (value.Length > 0)
                    mapRotation.AddRange(value.Split(',', StringSplitOptions.TrimEntries |
                                                               StringSplitOptions.RemoveEmptyEntries));
                continue;
            }
            values[bareKey] = value;
        }
        return new ParsedSection(values, mapRotation);
    }

    private void WriteSection(
        IReadOnlyDictionary<string, string> replacements,
        IReadOnlyList<string> mapRotation)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_configurationPath)!);
        var lines = File.Exists(_configurationPath)
            ? File.ReadAllLines(_configurationPath).ToList()
            : [];
        var header = $"[{Section}]";
        var sectionStart = lines.FindIndex(line =>
            line.Trim().Equals(header, StringComparison.OrdinalIgnoreCase));
        if (sectionStart < 0)
        {
            if (lines.Count > 0 && lines[^1].Length != 0) lines.Add("");
            lines.Add(header);
            sectionStart = lines.Count - 1;
        }
        var sectionEnd = lines.FindIndex(sectionStart + 1, line =>
        {
            var trimmed = line.Trim();
            return trimmed.StartsWith('[') && trimmed.EndsWith(']');
        });
        if (sectionEnd < 0) sectionEnd = lines.Count;

        var remaining = new Dictionary<string, string>(
            replacements, StringComparer.OrdinalIgnoreCase);
        for (var index = sectionStart + 1; index < sectionEnd; index++)
        {
            var line = lines[index].Trim();
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var key = line[..separator].Trim();
            var bareKey = key.TrimStart('+', '-', '.', '!');
            if (bareKey.Equals("MapRotation", StringComparison.OrdinalIgnoreCase))
            {
                lines.RemoveAt(index--);
                sectionEnd--;
                continue;
            }
            if (!remaining.Remove(bareKey, out var value)) continue;
            lines[index] = $"{key}={value}";
        }
        foreach (var pair in remaining)
            lines.Insert(sectionEnd++, $"{pair.Key}={pair.Value}");
        // Unreal's array operators make this an explicit replacement of any
        // defaults inherited from lower-priority config files.
        lines.Insert(sectionEnd++, "!MapRotation=ClearArray");
        foreach (var mapCode in mapRotation)
            lines.Insert(sectionEnd++, $"+MapRotation={mapCode}");

        var temporary = _configurationPath + ".briefcase.tmp";
        var backup = _configurationPath + ".briefcase.bak";
        File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
        if (File.Exists(_configurationPath))
            File.Copy(_configurationPath, backup, true);
        File.Move(temporary, _configurationPath, true);
    }

    private static string ReadString(
        IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) ? value : fallback;

    private static int ReadInt(
        IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var value) &&
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

    private static float ReadFloat(
        IReadOnlyDictionary<string, string> values, string key, float fallback) =>
        values.TryGetValue(key, out var value) &&
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
        float.IsFinite(parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(
        IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed)
            ? parsed
            : fallback;

    private static string FormatBool(bool value) => value ? "True" : "False";
    private static string FormatFloat(float value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string ValidateText(
        string? value, string name, int maximumLength, bool allowEmpty)
    {
        value = value?.Trim() ?? "";
        if (!allowEmpty && value.Length == 0)
            throw new InvalidOperationException($"{name} cannot be empty.");
        if (value.Length > maximumLength)
            throw new InvalidOperationException(
                $"{name} cannot exceed {maximumLength} characters.");
        if (value.IndexOfAny(['\r', '\n']) >= 0)
            throw new InvalidOperationException($"{name} cannot contain a line break.");
        return value;
    }

    private static void ValidateRange(int value, int minimum, int maximum, string name)
    {
        if (value < minimum || value > maximum)
            throw new InvalidOperationException(
                $"{name} must be between {minimum} and {maximum}.");
    }

    private static void ValidateRange(
        float value, float minimum, float maximum, string name)
    {
        if (!float.IsFinite(value) || value < minimum || value > maximum)
            throw new InvalidOperationException(
                $"{name} must be between {minimum} and {maximum}.");
    }

    private static void ValidateChoice(
        string value, IReadOnlySet<string> choices, string name)
    {
        if (!choices.Contains(value))
            throw new InvalidOperationException(
                $"{name} has an unsupported value: '{value}'.");
    }

    internal static string CreateRestartScript(
        int processId,
        string workingDirectory,
        string executablePath,
        int gamePort) => $"""
        @echo off
        setlocal
        :wait_for_exit
        tasklist /FI "PID eq {processId}" /FO CSV /NH 2>NUL | findstr /C:"{processId}" >NUL
        if not errorlevel 1 (
          timeout /t 1 /nobreak >NUL
          goto wait_for_exit
        )
        start "" /D "{workingDirectory}" "{executablePath}" -Port={gamePort}
        del "%~f0"
        """;

    private static DefaultPaths GetDefaultPaths()
    {
        var executable = Environment.ProcessPath ??
            throw new InvalidOperationException("The server executable path is unavailable.");
        var workingDirectory = Path.GetDirectoryName(executable) ??
            throw new InvalidOperationException("The server working directory is unavailable.");
        var gameDirectory = Directory.GetParent(
                                Directory.GetParent(workingDirectory)?.FullName ?? "")?.FullName ??
                            throw new InvalidOperationException(
                                "Could not locate the DeceiveInc server directory.");
        return new DefaultPaths(
            executable,
            Path.Combine(
                gameDirectory, "Saved", "Config", "WindowsServer", "TripwireServer.ini"),
            Path.Combine(workingDirectory, "Briefcase"));
    }

    private sealed record DefaultPaths(
        string ExecutablePath,
        string ConfigurationPath,
        string FrameworkDirectory);

    private sealed record ParsedSection(
        Dictionary<string, string> Values,
        List<string> MapRotation);
}
