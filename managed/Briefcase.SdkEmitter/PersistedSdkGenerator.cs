using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace Briefcase.SdkEmitter;

public sealed record PersistedSdkGenerationResult(
    string Target,
    string BuildKey,
    string AssemblyName,
    string AssemblyPath,
    bool Generated,
    int TypeCount,
    int PropertyCount,
    int FunctionCount,
    int DescribedPropertyCount,
    int DescribedFunctionCount,
    int SkippedTypeCount,
    int SkippedPropertyCount,
    int SkippedFunctionCount);

/// <summary>
/// Runtime entry point used by Briefcase.ManagedHost. It turns one committed
/// address-free Unreal snapshot into a cached IL SDK and publishes the ready
/// marker only after the PE identity and developer reference are valid.
/// </summary>
public static class PersistedSdkGenerator
{
    public const string CurrentEmitterSchema = "8";

    public static PersistedSdkGenerationResult Generate(
        string coreDirectory, string snapshotPath)
    {
        coreDirectory = Path.GetFullPath(coreDirectory);
        snapshotPath = Path.GetFullPath(snapshotPath);
        var snapshot = SnapshotReader.Read(snapshotPath);
        var buildKey = $"{snapshot.GameBuild.PeTimestamp:X8}-{snapshot.GameBuild.ImageSize:X8}";
        var output = Path.Combine(
            coreDirectory, "Sdk", "Generated", snapshot.Target, buildKey);
        var assemblyPath = Path.Combine(
            output, "bin", "Release", "net10.0", snapshot.SdkAssemblyName + ".dll");
        var readyPath = Path.Combine(output, ".ready");
        var completionPath = Path.Combine(output, ".complete");
        var snapshotHash = SnapshotHash(snapshotPath);
        var fingerprint = $"emitter={CurrentEmitterSchema}{Environment.NewLine}" +
                          $"snapshot={snapshotHash}{Environment.NewLine}";

        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        if (File.Exists(readyPath)) File.Delete(readyPath);

        EmissionResult emission;
        var generated = !File.Exists(assemblyPath) ||
                        !File.Exists(completionPath) ||
                        File.ReadAllText(completionPath) != fingerprint;
        if (generated)
        {
            emission = SnapshotSdkEmitter.Emit(snapshot, assemblyPath);
            WriteAtomically(completionPath, fingerprint);
        }
        else
        {
            var plan = SdkEmissionPlan.Create(snapshot);
            emission = new EmissionResult(
                plan,
                assemblyPath,
                plan.Types.Count,
                plan.Types.Sum(type => type.Properties.Count),
                plan.Types.Sum(type => type.Functions.Count));
        }

        ValidateAssemblyIdentity(assemblyPath, snapshot.SdkAssemblyName);
        WriteAtomically(Path.Combine(output, "snapshot.txt"), snapshotPath);
        WriteCurrentReference(coreDirectory, snapshot, buildKey, assemblyPath);
        WriteAtomically(
            readyPath,
            $"schema={snapshot.SchemaVersion}{Environment.NewLine}" +
            $"emitter={CurrentEmitterSchema}{Environment.NewLine}" +
            $"build={buildKey}{Environment.NewLine}" +
            $"snapshot={snapshotHash}{Environment.NewLine}");

        return new PersistedSdkGenerationResult(
            snapshot.Target,
            buildKey,
            snapshot.SdkAssemblyName,
            assemblyPath,
            generated,
            emission.TypeCount,
            emission.PropertyCount,
            emission.FunctionCount,
            emission.Plan.DescribedPropertyCount,
            emission.Plan.DescribedFunctionCount,
            emission.Plan.SkippedTypeCount,
            emission.Plan.SkippedPropertyCount,
            emission.Plan.SkippedFunctionCount);
    }

    private static string SnapshotHash(string snapshotPath)
    {
        using var stream = File.OpenRead(snapshotPath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void ValidateAssemblyIdentity(string path, string expectedName)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
            throw new InvalidDataException($"Generated SDK {path} has no CLR metadata.");
        var metadata = pe.GetMetadataReader();
        var name = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        if (name != expectedName)
            throw new InvalidDataException(
                $"Expected generated assembly {expectedName}; found {name}.");
        if (metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .Contains("System.Private.CoreLib", StringComparer.Ordinal))
            throw new InvalidDataException("Generated SDK exposes System.Private.CoreLib.");
    }

    private static void WriteCurrentReference(
        string coreDirectory,
        SdkSnapshot snapshot,
        string buildKey,
        string assemblyPath)
    {
        var path = Path.Combine(
            coreDirectory, "Sdk", "Generated", snapshot.Target, "Current.props");
        var escapedAssembly = EscapeXml(Path.GetFullPath(assemblyPath));
        WriteAtomically(
            path,
            $"""
            <Project>
              <PropertyGroup>
                <BriefcaseGeneratedSdkTarget>{snapshot.Target}</BriefcaseGeneratedSdkTarget>
                <BriefcaseGeneratedSdkBuild>{buildKey}</BriefcaseGeneratedSdkBuild>
                <BriefcaseGeneratedSdkAssembly>{snapshot.SdkAssemblyName}</BriefcaseGeneratedSdkAssembly>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="{snapshot.SdkAssemblyName}">
                  <HintPath>{escapedAssembly}</HintPath>
                  <Private>false</Private>
                </Reference>
              </ItemGroup>
            </Project>

            """);
    }

    private static void WriteAtomically(string destination, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".tmp";
        File.WriteAllText(temporary, contents);
        File.Move(temporary, destination, overwrite: true);
    }

    private static string EscapeXml(string value) => value
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("\"", "&quot;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);
}
