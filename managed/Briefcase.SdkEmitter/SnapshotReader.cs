using System.Text.Json;

namespace Briefcase.SdkEmitter;

internal static class SnapshotReader
{
    public static SdkSnapshot Read(string path)
    {
        using var stream = File.OpenRead(Path.GetFullPath(path));
        var snapshot = JsonSerializer.Deserialize(stream, SnapshotJsonContext.Default.SdkSnapshot)
            ?? throw new InvalidDataException("The SDK snapshot is empty.");
        if (snapshot.SchemaVersion is not (2 or 3))
            throw new InvalidDataException(
                $"Persisted SDK generation requires snapshot schema 2 or 3; found {snapshot.SchemaVersion}.");
        if (snapshot.Target is not ("Client" or "Server"))
            throw new InvalidDataException($"Unsupported SDK target {snapshot.Target}.");
        var expectedName = $"Briefcase.DeceiveInc.{snapshot.Target}.Sdk";
        if (snapshot.SdkAssemblyName != expectedName)
            throw new InvalidDataException(
                $"Expected SDK assembly {expectedName}; found {snapshot.SdkAssemblyName}.");
        return snapshot;
    }
}
