using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Briefcase.ModApi;
using Briefcase.SdkEmitter;

namespace Briefcase.ManagedHost;

internal static class GeneratedSdkLoader
{
    public static Assembly? Load(
        ModContext context,
        string coreDirectory,
        Action<double, string>? progress = null)
    {
        progress?.Invoke(0.05, "Reading the game build identity...");
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
        progress?.Invoke(0.2, "Validating the generated SDK cache...");
        if (TryValidateCachedPublication(selected, buildKey, out var cachedIdentity))
        {
            progress?.Invoke(0.85, "Loading the validated generated SDK...");
            var cached = LoadIntoFrameworkContext(cachedIdentity!, selected.AssemblyPath);
            context.Info(
                $"Shared generated SDK loaded from validated cache: " +
                $"{cachedIdentity!.Name} ({buildKey}).");
            progress?.Invoke(1, "Generated SDK cache is ready.");
            return cached;
        }

        progress?.Invoke(0.35, "Generating the typed SDK from the metadata snapshot...");
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

        progress?.Invoke(0.82, "Validating the generated SDK publication...");
        var identity = AssemblyName.GetAssemblyName(generation.AssemblyPath);
        if (!string.Equals(identity.Name, selected.ExpectedAssemblyName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Generated SDK identity mismatch: expected {selected.ExpectedAssemblyName}, " +
                $"found {identity.Name ?? "<missing>"}.");

        progress?.Invoke(0.92, "Loading the generated SDK assembly...");
        var loaded = LoadIntoFrameworkContext(identity, generation.AssemblyPath);
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
        progress?.Invoke(1, "Generated SDK is ready.");
        return loaded;
    }

    private static bool TryValidateCachedPublication(
        Candidate candidate,
        string buildKey,
        out AssemblyName? identity)
    {
        identity = null;
        if (!candidate.IsReady || !File.Exists(candidate.SnapshotPath)) return false;
        try
        {
            var marker = File.ReadAllLines(candidate.ReadyPath)
                .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            if (!marker.TryGetValue("emitter", out var emitter) ||
                emitter != PersistedSdkGenerator.CurrentEmitterSchema ||
                !marker.TryGetValue("build", out var readyBuild) || readyBuild != buildKey ||
                !marker.TryGetValue("snapshot", out var expectedHash))
                return false;

            using var snapshot = File.OpenRead(candidate.SnapshotPath);
            var actualHash = Convert.ToHexString(SHA256.HashData(snapshot));
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase)) return false;

            identity = AssemblyName.GetAssemblyName(candidate.AssemblyPath);
            return string.Equals(
                identity.Name, candidate.ExpectedAssemblyName, StringComparison.Ordinal);
        }
        catch
        {
            identity = null;
            return false;
        }
    }

    private static Assembly LoadIntoFrameworkContext(AssemblyName identity, string assemblyPath)
    {
        // hostfxr may place this component in a dedicated context rather than
        // AssemblyLoadContext.Default. Loading beside Briefcase.ModApi keeps
        // generated base types and patch attributes on the framework identity.
        var frameworkContext = AssemblyLoadContext.GetLoadContext(typeof(ModContext).Assembly) ??
            throw new InvalidOperationException("Could not identify the framework load context.");
        var existing = frameworkContext.Assemblies.FirstOrDefault(
            assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), identity));
        return existing ?? frameworkContext.LoadFromAssemblyPath(assemblyPath);
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
