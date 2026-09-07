using System.Diagnostics;

namespace Briefcase.Core.Tests;

public sealed class VersionScriptTests
{
    [Theory]
    [InlineData("1.2.3", "build", "1.2.4")]
    [InlineData("1.2.3", "minor", "1.3.0")]
    [InlineData("1.2.3", "major", "2.0.0")]
    public void GetNextVersion_increments_the_selected_component(
        string current, string increment, string expected)
    {
        var result = Run(current, increment);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected, result.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("1.2", "build")]
    [InlineData("1.2.3.4", "build")]
    [InlineData("65536.0.0", "major")]
    [InlineData("1.2.65535", "build")]
    public void GetNextVersion_rejects_invalid_or_overflowing_versions(
        string current, string increment)
    {
        var result = Run(current, increment);

        Assert.NotEqual(0, result.ExitCode);
    }

    private static ProcessResult Run(string current, string increment)
    {
        var script = Path.Combine(
            RepositoryPaths.Root, "scripts", "release", "GetNextVersion.ps1");
        var start = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-ExecutionPolicy");
        start.ArgumentList.Add("Bypass");
        start.ArgumentList.Add("-File");
        start.ArgumentList.Add(script);
        start.ArgumentList.Add("-CurrentVersion");
        start.ArgumentList.Add(current);
        start.ArgumentList.Add("-Increment");
        start.ArgumentList.Add(increment);

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("PowerShell could not be started.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed record ProcessResult(
        int ExitCode, string StandardOutput, string StandardError);
}
