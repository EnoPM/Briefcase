using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Briefcase.SdkEmitter;

/// <summary>
/// PersistedAssemblyBuilder binds its well-known types to the implementation
/// assembly supplied by the current runtime. A generated reference library must
/// instead bind them to the public System.Runtime facade so Roslyn can consume
/// the DLL against the net10.0 reference assemblies.
/// </summary>
internal static class CoreReferenceNormalizer
{
    private const string ImplementationName = "System.Private.CoreLib";
    private const string ReferenceName = "System.Runtime";
    private static readonly byte[] ImplementationKey =
        typeof(object).Assembly.GetName().GetPublicKey()
        ?? throw new InvalidOperationException("System.Private.CoreLib has no public key.");
    private static readonly byte[] ReferenceKey =
        System.Reflection.Assembly.Load(ReferenceName).GetName().GetPublicKey()
        ?? throw new InvalidOperationException("System.Runtime has no public key.");

    public static void Normalize(string path)
    {
        var before = ReadAssemblyReferences(path);
        var implementation = before.SingleOrDefault(reference => reference.Name == ImplementationName);
        if (implementation is null || !implementation.PublicKey.SequenceEqual(ImplementationKey))
            throw new InvalidDataException(
                $"Expected one known {ImplementationName} assembly reference before normalization. " +
                string.Join(", ", before.Select(reference =>
                    $"{reference.Name}={Convert.ToHexString(reference.PublicKey)}")));

        var bytes = File.ReadAllBytes(path);
        var needle = Encoding.UTF8.GetBytes(ImplementationName + '\0');
        var replacement = Encoding.UTF8.GetBytes(ReferenceName + '\0');
        var offset = FindUnique(bytes, needle);
        Array.Clear(bytes, offset, needle.Length);
        replacement.CopyTo(bytes, offset);
        ReferenceKey.CopyTo(bytes, FindUnique(bytes, ImplementationKey));
        File.WriteAllBytes(path, bytes);

        var after = ReadAssemblyReferences(path);
        if (after.Any(reference => reference.Name == ImplementationName) ||
            !after.Any(reference => reference.Name == ReferenceName &&
                                  reference.PublicKey.SequenceEqual(ReferenceKey)))
            throw new InvalidDataException("The persisted core reference was not normalized.");
    }

    private static IReadOnlyList<AssemblyReferenceIdentity> ReadAssemblyReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        return metadata.AssemblyReferences
            .Select(handle => metadata.GetAssemblyReference(handle))
            .Select(reference => new AssemblyReferenceIdentity(
                metadata.GetString(reference.Name),
                metadata.GetBlobBytes(reference.PublicKeyOrToken)))
            .ToArray();
    }

    private static int FindUnique(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> needle)
    {
        var first = bytes.IndexOf(needle);
        if (first < 0 || bytes[(first + needle.Length)..].IndexOf(needle) >= 0)
            throw new InvalidDataException(
                $"Expected one {ImplementationName} metadata string in the persisted PE.");
        return first;
    }

    private sealed record AssemblyReferenceIdentity(string Name, byte[] PublicKey);
}
