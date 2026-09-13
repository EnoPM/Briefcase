using System.Text.RegularExpressions;

namespace Briefcase.ModPackages;

/// <summary>Small SemVer-compatible value used for release comparisons.</summary>
public readonly partial record struct PackageVersion : IComparable<PackageVersion>
{
    private PackageVersion(Version core, string? preRelease, string display)
    {
        Core = core;
        PreRelease = preRelease;
        Display = display;
    }

    public Version Core { get; }
    public string? PreRelease { get; }
    public string Display { get; }

    [GeneratedRegex("^v?(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:\\.(?<build>0|[1-9][0-9]*))?(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool TryParse(string? text, out PackageVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 80) return false;
        var match = VersionPattern().Match(text.Trim());
        if (!match.Success ||
            !int.TryParse(match.Groups["major"].Value, out var major) ||
            !int.TryParse(match.Groups["minor"].Value, out var minor) ||
            !int.TryParse(match.Groups["patch"].Value, out var patch)) return false;
        var build = match.Groups["build"].Success &&
                    int.TryParse(match.Groups["build"].Value, out var parsedBuild)
            ? parsedBuild
            : 0;
        try
        {
            var display = $"{major}.{minor}.{patch}" +
                          (match.Groups["build"].Success ? $".{build}" : "") +
                          (match.Groups["pre"].Success ? $"-{match.Groups["pre"].Value}" : "");
            version = new PackageVersion(
                new Version(major, minor, patch, build),
                match.Groups["pre"].Success ? match.Groups["pre"].Value : null,
                display);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    public int CompareTo(PackageVersion other)
    {
        var core = Core.CompareTo(other.Core);
        if (core != 0) return core;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string left, string right)
    {
        var leftParts = left.Split('.');
        var rightParts = right.Split('.');
        for (var index = 0; index < Math.Max(leftParts.Length, rightParts.Length); index++)
        {
            if (index >= leftParts.Length) return -1;
            if (index >= rightParts.Length) return 1;
            var leftNumeric = IsNumeric(leftParts[index]);
            var rightNumeric = IsNumeric(rightParts[index]);
            if (leftNumeric != rightNumeric) return leftNumeric ? -1 : 1;
            int comparison;
            if (leftNumeric)
            {
                var normalizedLeft = leftParts[index].TrimStart('0');
                var normalizedRight = rightParts[index].TrimStart('0');
                if (normalizedLeft.Length == 0) normalizedLeft = "0";
                if (normalizedRight.Length == 0) normalizedRight = "0";
                comparison = normalizedLeft.Length.CompareTo(normalizedRight.Length);
                if (comparison == 0)
                    comparison = StringComparer.Ordinal.Compare(
                        normalizedLeft, normalizedRight);
            }
            else
            {
                comparison = StringComparer.Ordinal.Compare(
                    leftParts[index], rightParts[index]);
            }
            if (comparison != 0) return comparison;
        }
        return 0;
    }

    private static bool IsNumeric(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9');

    public override string ToString() => Display;

    public static bool operator <(PackageVersion left, PackageVersion right) =>
        left.CompareTo(right) < 0;
    public static bool operator >(PackageVersion left, PackageVersion right) =>
        left.CompareTo(right) > 0;
    public static bool operator <=(PackageVersion left, PackageVersion right) =>
        left.CompareTo(right) <= 0;
    public static bool operator >=(PackageVersion left, PackageVersion right) =>
        left.CompareTo(right) >= 0;
}
