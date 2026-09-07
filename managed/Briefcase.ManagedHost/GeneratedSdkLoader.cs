using System.Reflection;
using System.Runtime.Loader;
using Briefcase.ModApi;
using Briefcase.SdkEmitter;

namespace Briefcase.ManagedHost;

internal static class GeneratedSdkLoader
{
    public static Assembly? Load(ModContext context, string coreDirectory)
    {
        var build = context.GameBuild;
        if (build.PeTimestamp == 0 || build.ImageSize == 0)
        {
            context.Warning("Generated SDK: the host did not report a game build identity.");
            return null;
        }

        var buildKey = $"{build.PeTimestamp:X8}-{build.ImageSize:X8}";
        var candidates = new[]
        {
            Candidate.For(coreDirectory, "Client", buildKey),
            Candidate.For(coreDirectory, "Server", buildKey)
        };

        var available = candidates.Where(candidate => File.Exists(candidate.SnapshotPath)).ToArray();
        if (available.Length != 1)
        {
            context.Warning(
                $"Generated SDK: expected one Client or Server snapshot for build {buildKey}; " +
                $"found {available.Length}. " +
                "Mods that do not use generated game types can still load.");
            return null;
        }

        var selected = available[0];
        PersistedSdkGenerationResult generation;
        try
        {
            generation = PersistedSdkGenerator.Generate(coreDirectory, selected.SnapshotPath);
        }
        catch (Exception exception)
        {
            context.Warning(
                $"Generated SDK: persisted IL generation failed: {exception.Message}. " +
                "Mods that do not use generated game types can still load.");
            return null;
        }

        if (generation.Target != selected.Target ||
            generation.AssemblyName != selected.ExpectedAssemblyName ||
            !Path.GetFullPath(generation.AssemblyPath).Equals(
                selected.AssemblyPath, StringComparison.OrdinalIgnoreCase) ||
            !selected.IsReady)
            throw new InvalidOperationException("Generated SDK publication does not match the host target.");

        var identity = AssemblyName.GetAssemblyName(generation.AssemblyPath);
        if (!string.Equals(identity.Name, selected.ExpectedAssemblyName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Generated SDK identity mismatch: expected {selected.ExpectedAssemblyName}, " +
                $"found {identity.Name ?? "<missing>"}.");

        // hostfxr may place this component in a dedicated context rather than
        // AssemblyLoadContext.Default. Loading beside Briefcase.ModApi is essential:
        // generated base types and patch attributes must resolve to the exact
        // same Briefcase.ModApi type identities used by the manager.
        var frameworkContext = AssemblyLoadContext.GetLoadContext(typeof(ModContext).Assembly) ??
            throw new InvalidOperationException("Could not identify the framework load context.");
        var existing = frameworkContext.Assemblies.FirstOrDefault(
            assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), identity));
        var loaded = existing ?? frameworkContext.LoadFromAssemblyPath(generation.AssemblyPath);
        context.Info(
            $"Shared generated SDK {(generation.Generated ? "generated" : "reused")}: " +
            $"{identity.Name} ({buildKey}); {generation.TypeCount} types, " +
            $"{generation.PropertyCount} properties/fields, {generation.FunctionCount} functions");
        context.Info(
            $"Generated SDK reflection metadata: {generation.DescribedPropertyCount} properties/fields, " +
            $"{generation.DescribedFunctionCount} functions described");
        if (generation.SkippedTypeCount + generation.SkippedPropertyCount +
            generation.SkippedFunctionCount > 0)
            context.Info(
                $"Generated SDK callable ABI report: {generation.SkippedTypeCount} types, " +
                $"{generation.SkippedPropertyCount} properties/fields, " +
                $"{generation.SkippedFunctionCount} functions skipped");
        return loaded;
    }

    private sealed record Candidate(
        string ExpectedAssemblyName,
        string Target,
        string SnapshotPath,
        string AssemblyPath,
        string ReadyPath)
    {
        public bool IsReady => File.Exists(ReadyPath) && File.Exists(AssemblyPath);

        public static Candidate For(string coreDirectory, string target, string buildKey)
        {
            var assemblyName = $"Briefcase.DeceiveInc.{target}.Sdk";
            var metadataDirectory = Path.GetFullPath(Path.Combine(
                coreDirectory,
                "Sdk",
                "Metadata"));
            var binarySnapshot = Path.Combine(
                metadataDirectory,
                $"DeceiveInc.{target}.{buildKey}.bsnap");
            var jsonSnapshot = Path.Combine(
                metadataDirectory,
                $"DeceiveInc.{target}.{buildKey}.json");
            var outputDirectory = Path.GetFullPath(Path.Combine(
                coreDirectory,
                "Sdk",
                "Generated",
                target,
                buildKey));
            var snapshotPath = new[] { binarySnapshot, jsonSnapshot }
                .Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ThenBy(path => Path.GetExtension(path).Equals(
                    ".bsnap", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault() ?? binarySnapshot;
            return new Candidate(
                assemblyName,
                target,
                snapshotPath,
                Path.GetFullPath(Path.Combine(
                    outputDirectory,
                    "bin",
                    "Release",
                    "net10.0",
                    $"{assemblyName}.dll")),
                Path.Combine(outputDirectory, ".ready"));
        }
    }
}
