using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace Briefcase.SdkGenerator;

internal static class Program
{
    public static int Main(string[] args)
    {
        string? root = null;
        string? snapshotPath = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--root" && ++index < args.Length) root = args[index];
            else if (args[index] == "--snapshot" && ++index < args.Length) snapshotPath = args[index];
            else return 2;
        }
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(snapshotPath))
            return 2;

        root = Path.GetFullPath(root);
        snapshotPath = Path.GetFullPath(snapshotPath);
        var logDirectory = Path.Combine(root, "Sdk");
        Directory.CreateDirectory(logDirectory);
        var logPath = Path.Combine(logDirectory, "Generator.log");
        try
        {
            using var snapshotStream = File.OpenRead(snapshotPath);
            var snapshot = JsonSerializer.Deserialize(
                snapshotStream, SnapshotJsonContext.Default.SdkSnapshot) ??
                throw new InvalidDataException("The SDK snapshot is empty.");
            if (snapshot.SchemaVersion is not (1 or 2))
                throw new InvalidDataException(
                    $"Unsupported SDK snapshot schema {snapshot.SchemaVersion}.");

            var modSdkPath = Path.Combine(root, "Briefcase.ModApi.dll");
            var modSdkIdentity = ReadAssemblyIdentity(modSdkPath, "Briefcase.ModApi");
            var result = SourceSdkGenerator.Generate(root, snapshot, modSdkPath);
            var sdkDll = Path.Combine(
                result.OutputDirectory, "bin", "Release", "net10.0",
                result.AssemblyName + ".dll");
            var readyPath = Path.Combine(result.OutputDirectory, ".ready");
            if ((result.SourcesChanged || !File.Exists(sdkDll) || !File.Exists(readyPath)) &&
                !BuildSdk(result.ProjectPath, result.OutputDirectory))
                throw new InvalidOperationException(
                    "The generated sources are valid, but dotnet build failed. See sdk-build.log.");
            var generatedIdentity = ReadAssemblyIdentity(sdkDll, result.AssemblyName);
            WriteCurrentReference(result, snapshot, sdkDll);
            WriteReadyMarker(readyPath, snapshot);
            File.AppendAllText(logPath,
                $"{DateTimeOffset.Now:O} Generated {result.TypeCount} types for " +
                $"{snapshot.Target} build {snapshot.GameBuild.PeTimestamp:X8}-" +
                $"{snapshot.GameBuild.ImageSize:X8}; Mod SDK={modSdkIdentity}; " +
                $"generated SDK={generatedIdentity}; " +
                $"output={result.OutputDirectory}{Environment.NewLine}");
            return 0;
        }
        catch (Exception exception)
        {
            File.AppendAllText(logPath,
                $"{DateTimeOffset.Now:O} ERROR {exception}{Environment.NewLine}");
            return 1;
        }
    }

    // System.Reflection.Metadata reads the trusted compile-time contract
    // without Assembly.Load and cannot execute code from the inspected file.
    private static string ReadAssemblyIdentity(string path, string expectedName)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new InvalidDataException($"{path} has no managed metadata.");
        var metadata = pe.GetMetadataReader();
        if (!metadata.IsAssembly)
            throw new InvalidDataException($"{path} is not an assembly.");
        var definition = metadata.GetAssemblyDefinition();
        var name = metadata.GetString(definition.Name);
        if (!string.Equals(name, expectedName, StringComparison.Ordinal))
            throw new InvalidDataException($"Expected {expectedName}; found {name}.");
        return $"{name}, Version={definition.Version}";
    }

    private static bool BuildSdk(string projectPath, string outputDirectory)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = outputDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(projectPath);
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("Release");
        start.ArgumentList.Add("--nologo");
        start.ArgumentList.Add("-v:minimal");
        start.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en-US";
        start.Environment["VSLANG"] = "1033";

        using var process = Process.Start(start) ??
            throw new InvalidOperationException("Could not start dotnet build.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        File.WriteAllText(
            Path.Combine(outputDirectory, "sdk-build.log"),
            standardOutput.GetAwaiter().GetResult() + standardError.GetAwaiter().GetResult());
        return process.ExitCode == 0;
    }

    private static void WriteCurrentReference(
        GenerationResult result,
        SdkSnapshot snapshot,
        string sdkDll)
    {
        var targetDirectory = Directory.GetParent(result.OutputDirectory)?.FullName ??
            throw new InvalidOperationException("The generated SDK target directory is invalid.");
        var destination = Path.Combine(targetDirectory, "Current.props");
        var temporary = destination + ".tmp";
        var buildId = $"{snapshot.GameBuild.PeTimestamp:X8}-{snapshot.GameBuild.ImageSize:X8}";
        var assemblyPath = EscapeXml(Path.GetFullPath(sdkDll));
        var contents =
            $"""
            <Project>
              <PropertyGroup>
                <BriefcaseGeneratedSdkTarget>{snapshot.Target}</BriefcaseGeneratedSdkTarget>
                <BriefcaseGeneratedSdkBuild>{buildId}</BriefcaseGeneratedSdkBuild>
                <BriefcaseGeneratedSdkAssembly>{result.AssemblyName}</BriefcaseGeneratedSdkAssembly>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="{result.AssemblyName}">
                  <HintPath>{assemblyPath}</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>
            </Project>

            """;
        File.WriteAllText(temporary, contents);
        File.Move(temporary, destination, overwrite: true);
    }

    private static void WriteReadyMarker(string destination, SdkSnapshot snapshot)
    {
        // The managed host watches this commit marker instead of the DLL itself.
        // A DLL can already exist from an older build while dotnet is replacing it;
        // publishing this file last makes the generated SDK visible atomically.
        var temporary = destination + ".tmp";
        File.WriteAllText(
            temporary,
            $"schema={snapshot.SchemaVersion}{Environment.NewLine}" +
            $"build={snapshot.GameBuild.PeTimestamp:X8}-{snapshot.GameBuild.ImageSize:X8}" +
            Environment.NewLine);
        File.Move(temporary, destination, overwrite: true);
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
