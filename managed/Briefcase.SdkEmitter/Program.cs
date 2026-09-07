namespace Briefcase.SdkEmitter;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args is ["production", var productionSnapshot, var frameworkRoot])
            return RunProductionValidation(productionSnapshot, frameworkRoot);
        if (args is ["convert", var sourceSnapshot, var binarySnapshot])
            return ConvertSnapshot(sourceSnapshot, binarySnapshot);
        if (args.Length != 3)
        {
            Console.Error.WriteLine(
                "Usage: Briefcase.SdkEmitter <snapshot> <current-sdk.dll> <prototype.dll>\n" +
                "   or: Briefcase.SdkEmitter production <snapshot> <framework-root>\n" +
                "   or: Briefcase.SdkEmitter convert <snapshot.json> <snapshot.bsnap>");
            return 2;
        }

        try
        {
            var snapshotPath = Path.GetFullPath(args[0]);
            var referencePath = Path.GetFullPath(args[1]);
            var destination = Path.GetFullPath(args[2]);
            var snapshot = SnapshotReader.Read(snapshotPath);

            var result = SnapshotSdkEmitter.Emit(
                snapshot, destination, snapshot.SdkAssemblyName + ".EmitPrototype");
            var validation = FullSurfaceValidator.Validate(referencePath, destination, result.Plan);
            Console.WriteLine($"[OK] Persisted IL SDK prototype: {destination}");
            Console.WriteLine(
                $"[OK] Emitted {result.TypeCount} types, {result.PropertyCount} properties/fields, " +
                $"and {result.FunctionCount} functions.");
            Console.WriteLine(
                $"[OK] Described {result.Plan.DescribedPropertyCount} properties/fields and " +
                $"{result.Plan.DescribedFunctionCount} functions in reflection metadata.");
            Console.WriteLine(
                $"[INFO] Skipped {result.Plan.SkippedTypeCount} types, " +
                $"{result.Plan.SkippedPropertyCount} properties/fields, and " +
                $"{result.Plan.SkippedFunctionCount} functions not covered by the current ABI.");
            Console.WriteLine(
                $"[OK] Compared all {validation.TypeCount} types, " +
                $"{validation.PropertyCount} properties/fields, and " +
                $"{validation.FunctionCount} functions with the current SDK.");
            Console.WriteLine("[OK] PE identity validated with System.Reflection.Metadata.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ERROR] {exception}");
            return 1;
        }
    }

    private static int ConvertSnapshot(string sourcePath, string destinationPath)
    {
        try
        {
            sourcePath = Path.GetFullPath(sourcePath);
            destinationPath = Path.GetFullPath(destinationPath);
            if (!Path.GetExtension(destinationPath).Equals(
                    ".bsnap", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The converted snapshot must use the .bsnap extension.");
            var snapshot = SnapshotReader.Read(sourcePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            using var destination = File.Create(destinationPath);
            BriefcaseSnapshotSerializer.WriteBinary(snapshot, destination);
            Console.WriteLine($"[OK] Compact snapshot: {destinationPath}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ERROR] {exception}");
            return 1;
        }
    }

    private static int RunProductionValidation(string snapshotPath, string frameworkRoot)
    {
        try
        {
            var first = PersistedSdkGenerator.Generate(frameworkRoot, snapshotPath);
            if (!first.Generated)
                throw new InvalidDataException("The clean production validation did not emit an SDK.");
            var second = PersistedSdkGenerator.Generate(frameworkRoot, snapshotPath);
            if (second.Generated)
                throw new InvalidDataException("The unchanged production snapshot missed its cache.");
            var currentProps = Path.Combine(
                Path.GetFullPath(frameworkRoot), "Sdk", "Generated", first.Target, "Current.props");
            if (!File.Exists(currentProps))
                throw new FileNotFoundException("Current.props was not published.", currentProps);

            Console.WriteLine($"[OK] Production SDK generated: {first.AssemblyPath}");
            Console.WriteLine("[OK] Exact assembly identity, publication markers, and SHA-256 cache validated.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[ERROR] {exception}");
            return 1;
        }
    }
}
