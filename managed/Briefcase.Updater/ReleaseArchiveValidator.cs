using System.IO.Compression;

namespace Briefcase.Updater;

internal static class ReleaseArchiveValidator
{
    private const int MaximumEntries = 25_000;
    private const long MaximumExpandedBytes = 1_500L * 1024 * 1024;

    public static void Validate(
        string archivePath,
        string expectedVersion,
        BriefcasePackageKind packageKind)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is 0 or > MaximumEntries)
            throw new InvalidDataException("The release archive has an invalid entry count.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = NormalizeEntryName(entry.FullName);
            if (!names.Add(normalized))
                throw new InvalidDataException($"The release archive contains duplicate path '{normalized}'.");

            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedBytes)
                throw new InvalidDataException("The expanded release archive is too large.");
        }

        RequireFile(names, "version.dll");
        RequireFile(names, "Briefcase/VERSION");
        RequireFile(names, "Briefcase/Core/Briefcase.ManagedHost.dll");
        RequireFile(names, "Briefcase/Core/Briefcase.Updater.dll");
        RequireFile(names, "Briefcase/Core/Native/Briefcase.UnrealRuntime.dll");
        RequireFile(names, "Briefcase/Core/Updater/Briefcase.UpdateInstaller.exe");
        if (packageKind == BriefcasePackageKind.Client)
            RequireFile(names, "Briefcase/Core/Ui/Avalonia/Briefcase.AvaloniaUi.dll");

        if (names.Any(name =>
                name.StartsWith("Briefcase/Mods/", StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith('/')))
            throw new InvalidDataException("A framework release must not contain user mods.");

        var versionEntry = archive.GetEntry("Briefcase/VERSION") ??
                           FindEntry(archive, "Briefcase/VERSION");
        if (versionEntry is null || versionEntry.Length is <= 0 or > 64)
            throw new InvalidDataException("The release VERSION file is invalid.");
        using var reader = new StreamReader(versionEntry.Open());
        var archiveVersion = reader.ReadToEnd().Trim();
        if (!string.Equals(archiveVersion, expectedVersion, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The release archive version '{archiveVersion}' does not match '{expectedVersion}'.");
    }

    public static string NormalizeEntryName(string entryName)
    {
        if (string.IsNullOrWhiteSpace(entryName))
            throw new InvalidDataException("The release archive contains an empty path.");

        var normalized = entryName.Replace('\\', '/');
        if (normalized.StartsWith('/') ||
            normalized.Contains(':') ||
            normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Any(component => component is "." or ".."))
            throw new InvalidDataException($"Unsafe release archive path '{entryName}'.");
        return normalized;
    }

    public static bool IsInstallablePath(string normalized, BriefcasePackageKind packageKind)
    {
        if (string.Equals(normalized, "version.dll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "Briefcase/VERSION", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("Briefcase/Core/", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "LICENSE", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, "README-Briefcase.txt", StringComparison.OrdinalIgnoreCase))
            return true;
        return packageKind == BriefcasePackageKind.Server &&
               string.Equals(normalized, "StartBriefcaseServer.bat",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static void RequireFile(HashSet<string> names, string name)
    {
        if (!names.Contains(name))
            throw new InvalidDataException($"The release archive is missing '{name}'.");
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name) =>
        archive.Entries.FirstOrDefault(entry =>
            string.Equals(
                NormalizeEntryName(entry.FullName),
                name,
                StringComparison.OrdinalIgnoreCase));
}
