using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Briefcase.ServerApp;

/// <summary>Configuration that can be completed before Shipping or an injected DLL runs.</summary>
public static class ServerPreparation
{
    public static bool Prepare(ServerLocation location)
    {
        BriefcasePackageInstaller.RecoverPending(location);
        DisableNativeUpdater(location);
        DisableEac(location);
        RemoveLegacyInjector(location);
        return DisableProxy(location);
    }

    private static void RemoveLegacyInjector(ServerLocation location)
    {
        foreach (var file in new[]
        {
            Path.Combine(location.BriefcaseRoot, "Core", "Tools", "Briefcase.ServerInjector.exe"),
            Path.Combine(location.BriefcaseRoot, "Core", "Briefcase.ServerBootstrap.dll"),
            Path.Combine(location.BriefcaseRoot, "Core", "Tools", "Briefcase.ServerUpdater.exe"),
            Path.Combine(location.BriefcaseRoot, "Core", "Tools", "Briefcase.ServerRestart.exe")
        })
        {
            EnsurePlainPath(location.Win64, file);
            if (File.Exists(file)) File.Delete(file);
        }
    }

    private static bool DisableProxy(ServerLocation location)
    {
        var proxy = Path.Combine(location.Win64, "version.dll");
        var disabled = proxy + ".disabled";
        EnsurePlainPath(location.Win64, proxy);
        EnsurePlainPath(location.Win64, disabled);
        if (!File.Exists(proxy)) return false;
        File.Move(proxy, disabled, overwrite: true);
        return true;
    }

    private static void DisableNativeUpdater(ServerLocation location)
    {
        var file = Path.Combine(location.BriefcaseRoot, "updater.json");
        EnsurePlainPath(location.Win64, file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        JsonDocument? previous = null;
        if (File.Exists(file))
        {
            if (new FileInfo(file).Length > 65536) throw new InvalidDataException("Native updater configuration is too large.");
            previous = JsonDocument.Parse(File.ReadAllBytes(file));
        }
        using (previous)
        {
            if (previous is not null && previous.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Native updater configuration must be a JSON object.");
            var temp = file + ".serverapp-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Create(temp))
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    if (previous is not null)
                        foreach (var property in previous.RootElement.EnumerateObject())
                            if (property.Name is not ("schemaVersion" or "enabled" or "updateMods"))
                            {
                                writer.WritePropertyName(property.Name);
                                property.Value.WriteTo(writer);
                            }
                    writer.WriteNumber("schemaVersion", 1);
                    writer.WriteBoolean("enabled", false);
                    writer.WriteBoolean("updateMods", false);
                    writer.WriteEndObject();
                }
                File.Move(temp, file, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    private static void DisableEac(ServerLocation location)
    {
        var gameRoot = Directory.GetParent(Directory.GetParent(location.Win64)!.FullName)!.FullName;
        var file = Path.Combine(gameRoot, "Saved", "Config", "WindowsServer", "Engine.ini");
        EnsurePlainPath(gameRoot, file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var text = File.Exists(file) ? File.ReadAllText(file) : "";
        if (text.Length > 2 * 1024 * 1024) throw new InvalidDataException("Engine.ini is too large.");
        var setting = new Regex(@"(?im)^[ \t]*sb\.DisableEAC[ \t]*=[^\r\n]*", RegexOptions.CultureInvariant);
        var updated = setting.IsMatch(text) ? setting.Replace(text, "sb.DisableEAC=1") :
            text.TrimEnd('\r', '\n') + "\n\n[ConsoleVariables]\nsb.DisableEAC=1\n";
        if (updated == text) return;
        var temp = file + ".serverapp-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, updated, new UTF8Encoding(false));
            File.Move(temp, file, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void EnsurePlainPath(string root, string file)
    {
        var normalized = Path.GetFullPath(root);
        var current = Path.GetDirectoryName(Path.GetFullPath(file));
        while (current is not null && current.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
        {
            if (Directory.Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException("Server preparation path contains a directory link.");
            if (string.Equals(current, normalized, StringComparison.OrdinalIgnoreCase)) break;
            current = Path.GetDirectoryName(current);
        }
        if (File.Exists(file) && File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Server preparation target is a file link.");
    }
}
