using System.Runtime.InteropServices;
using System.Text;
using Briefcase.ModApi.Interop;

namespace Briefcase.ModApi;

public sealed record ModInfo(
    string Id,
    string Name,
    string Author,
    string Version,
    string Description,
    ulong RequiredCapabilities = BriefcaseAbi.CoreCapability)
{
    /// <summary>
    /// IDs of mods that must be enabled and loaded before this mod. IDs refer
    /// to <see cref="Id"/>, never to DLL file names, so a package may be renamed
    /// without changing its dependency graph.
    /// </summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>
    /// Legacy compatibility hint retained for mods built against earlier
    /// Briefcase versions. The framework-owned menu now renders all simple and
    /// complex client configuration inside the central Mods page and does not
    /// create a top-level navigation entry per mod.
    /// </summary>
    public bool ShowConfigurationTab { get; init; } = true;
}

public abstract class BriefcaseMod
{
    public abstract ModInfo Info { get; }
    public abstract void Load(ModContext context);
    public virtual void Unload() { }
}

public readonly unsafe struct ModContext
{
    private readonly NativeHostApi* _api;
    private readonly ConfigurationApi _configuration;
    private readonly ModManagementApi _mods;
    private readonly GameThreadApi _gameThread;
    private readonly UnrealAssetApi _assets;
    private readonly UnrealEventApi _events;
    private readonly object? _extension;

    public ModContext(NativeHostApi* api)
    {
        _api = api;
        _configuration = default;
        _mods = default;
        _gameThread = default;
        _assets = default;
        _events = default;
        _extension = null;
    }

    private ModContext(
        NativeHostApi* api,
        ConfigurationApi configuration,
        ModManagementApi mods,
        GameThreadApi gameThread,
        UnrealAssetApi assets,
        UnrealEventApi events,
        object? extension)
    {
        _api = api;
        _configuration = configuration;
        _mods = mods;
        _gameThread = gameThread;
        _assets = assets;
        _events = events;
        _extension = extension;
    }

    internal ModContext WithConfiguration(IModConfigurationScope configuration) =>
        new(_api, new ConfigurationApi(configuration), _mods, _gameThread, _assets, _events, _extension);

    internal ModContext WithModManagement(IModManagementBackend backend) =>
        new(_api, _configuration, new ModManagementApi(backend), _gameThread, _assets, _events, _extension);

    internal ModContext WithGameThread(IGameThreadScope scope) =>
        new(_api, _configuration, _mods, new GameThreadApi(scope), _assets, _events, _extension);

    internal ModContext WithAssets(IUnrealAssetScope scope) =>
        new(_api, _configuration, _mods, _gameThread, new UnrealAssetApi(scope), _events, _extension);

    internal ModContext WithEvents(IUnrealEventScope scope) =>
        new(_api, _configuration, _mods, _gameThread, _assets, new UnrealEventApi(scope), _extension);
    internal ModContext WithExtension(object extension) =>
        new(_api, _configuration, _mods, _gameThread, _assets, _events,
            extension ?? throw new ArgumentNullException(nameof(extension)));

    internal T? GetExtension<T>() where T : class => _extension as T;

    internal NativeUnrealApi* UnrealNative => IsValid ? _api->Unreal : null;
    internal NativePatchingApi* PatchingNative => IsValid ? _api->Patching : null;
    internal NativeGameThreadApi* GameThreadNative => IsValid ? _api->GameThread : null;
    internal NativeRenderingApi* RenderingNative => IsValid ? _api->Rendering : null;

    public bool IsValid => _api != null && _api->ApiVersion == BriefcaseAbi.HostApiVersion &&
                           _api->Core != null && _api->Core->Log != null;
    public ulong Capabilities => IsValid
        ? _api->Capabilities |
          BriefcaseAbi.InputCapability |
          (ManagedRenderingBridge.Current?.IsAvailable == true
              ? BriefcaseAbi.RenderingCapability
              : 0) |
          (_mods.IsAvailable ? BriefcaseAbi.ModManagementCapability : 0)
        : 0;
    public UnrealApi Unreal => new(IsValid ? _api->Unreal : null);
    public RenderingApi Rendering => new();
    public InputApi Input => new();
    public ConfigurationApi Configuration => _configuration;
    public ModManagementApi Mods => _mods;
    public GameThreadApi GameThread => _gameThread;
    public UnrealAssetApi Assets => _assets;
    public UnrealEventApi Events => _events;

    public Version FrameworkVersion
    {
        get
        {
            if (!IsValid || _api->Core->GetFrameworkVersion == null) return new Version();
            var value = _api->Core->GetFrameworkVersion(_api->Core->Context);
            return new Version(value.Major, value.Minor, value.Patch);
        }
    }

    public GameBuild GameBuild
    {
        get
        {
            if (!IsValid || _api->Core->GetGameBuild == null) return default;
            var value = _api->Core->GetGameBuild(_api->Core->Context);
            return new GameBuild(value.PeTimestamp, value.ImageSize);
        }
    }

    public void Log(LogLevel level, string message)
    {
        if (!IsValid) return;
        var byteCount = Encoding.UTF8.GetByteCount(message);
        var bytes = byteCount <= 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(message, bytes);
        fixed (byte* pointer = bytes)
            _api->Core->Log(_api->Core->Context, (uint)level, pointer, (uint)bytes.Length);
    }

    public void Info(string message) => Log(LogLevel.Info, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);
}

public enum LogLevel : uint { Trace, Info, Warning, Error }
public readonly record struct GameBuild(uint PeTimestamp, uint ImageSize);

public static unsafe class NativeModEntrypoints<TMod> where TMod : BriefcaseMod, new()
{
    private static TMod? _instance;
    private static TMod Instance => _instance ??= new TMod();

    public static uint Query(uint hostApiVersion, NativeModInfo* destination)
    {
        try
        {
            if (destination == null || destination->StructSize < (uint)sizeof(NativeModInfo) ||
                hostApiVersion < BriefcaseAbi.HostApiVersion) return 0;
            var info = Instance.Info;
            *destination = default;
            destination->StructSize = (uint)sizeof(NativeModInfo);
            destination->MinimumHostApiVersion = BriefcaseAbi.HostApiVersion;
            destination->MaximumHostApiVersion = BriefcaseAbi.HostApiVersion;
            destination->RequiredCapabilities = info.RequiredCapabilities;
            WriteUtf8(destination->Id, 64, info.Id);
            WriteUtf8(destination->Name, 96, info.Name);
            WriteUtf8(destination->Author, 64, info.Author);
            WriteUtf8(destination->Version, 32, info.Version);
            WriteUtf8(destination->Description, 192, info.Description);
            return 1;
        }
        catch { return 0; }
    }

    public static uint Load(NativeHostApi* api)
    {
        try
        {
            var context = new ModContext(api);
            if (!context.IsValid) return 0;
            Instance.Load(context);
            return 1;
        }
        catch { return 0; }
    }

    public static void Unload()
    {
        try { Instance.Unload(); }
        catch { }
    }

    private static void WriteUtf8(byte* destination, int capacity, string value)
    {
        var buffer = new Span<byte>(destination, capacity);
        buffer.Clear();
        var source = value.AsSpan();
        while (!source.IsEmpty && Encoding.UTF8.GetByteCount(source) > capacity - 1)
            source = source[..^1];
        Encoding.UTF8.GetBytes(source, buffer[..^1]);
    }
}

public interface IUnrealObject<TSelf> where TSelf : UnrealObject, IUnrealObject<TSelf>
{
    static abstract TSelf FromObject(UnrealApi api, UnrealObjectHandle handle);
}

public readonly record struct UnrealClass<T>(string Path)
    where T : UnrealObject, IUnrealObject<T>;
public readonly record struct UnrealProperty<T>(string OwnerPath, string Name, int Offset, int Size)
{
    /// <summary>
    /// Reflected signature of this property when it is a delegate. It is null
    /// for ordinary properties and for delegates from pre-schema-4 SDKs.
    /// </summary>
    public UnrealFunction? DelegateSignature { get; init; }

    public UnrealProperty(
        string ownerPath, string name, int offset, int size,
        UnrealFunction? delegateSignature)
        : this(ownerPath, name, offset, size) =>
        DelegateSignature = delegateSignature;
}
public readonly record struct UnrealParameter(
    string Name, Type ManagedType, int Offset, int Size, bool IsOut = false)
{
    /// <summary>
    /// True for a writable Unreal reference parameter. A reference parameter
    /// contributes an input value and receives its final value after
    /// ProcessEvent returns. A pure <c>out</c> parameter has
    /// <see cref="IsOut"/> set and <see cref="IsReference"/> unset.
    /// </summary>
    public bool IsReference { get; init; }
}
public sealed record UnrealFunction(
    string OwnerPath,
    string Name,
    int ParameterBufferSize,
    IReadOnlyList<UnrealParameter> Parameters,
    Type? ReturnType = null)
{
    // Kept outside the positional constructor so SDK assemblies generated by
    // an older framework remain binary-compatible with this runtime.
    public UnrealParameter? ReturnParameter { get; init; }
}
public readonly record struct UnrealObjectReference(UnrealObjectHandle Handle)
{
    public static UnrealObjectReference Null { get; } =
        new(new UnrealObjectHandle(uint.MaxValue, 0));
    public bool IsNull => Handle.IsNull;
}

/// <summary>
/// Implemented by generated, blittable Unreal value structs. The instance
/// method keeps struct marshalling deterministic and independent from runtime
/// reflection-based marshalling.
/// </summary>
public interface IUnrealStructValue
{
    int Size { get; }
    void WriteTo(Span<byte> destination);
}

/// <summary>
/// Marks a generated USTRUCT whose fields include owning Unreal values such as
/// FString, FText or containers. These structs are ordinary managed snapshots:
/// they never contain native pointers and are intentionally not blittable.
/// </summary>
public interface IUnrealManagedStructValue { }

/// <summary>
/// Identifies one reflected field in a managed USTRUCT snapshot. The decoder
/// matches both Unreal's original name and native offset, so renamed C# members
/// and inherited fields remain unambiguous.
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class UnrealStructFieldAttribute(string name, int offset) : Attribute
{
    public string Name { get; } = name;
    public int Offset { get; } = offset;
}

/// <summary>
/// An address-free snapshot of a native <c>TArray&lt;T&gt;</c>. The runtime copies
/// every element while the containing UObject is validated; no Unreal pointer
/// is retained by managed code.
/// </summary>
public sealed class UnrealArray<T> : IReadOnlyList<T>
{
    private readonly T[] _items;

    /// <summary>Creates a value that can be assigned to a generated TArray property.</summary>
    public UnrealArray(params T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = (T[])items.Clone();
    }
    public int Count => _items.Length;
    public T this[int index] => _items[index];
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _items.GetEnumerator();
    public T[] ToArray() => (T[])_items.Clone();
}

public readonly record struct UnrealInterfaceReference(UnrealObjectReference Object);
public readonly record struct UnrealGuid(uint A, uint B, uint C, uint D);
public readonly record struct UnrealLazyObjectReference(
    UnrealObjectReference Object, UnrealGuid Id);
public sealed record UnrealSoftObjectReference(
    UnrealObjectReference Object, string AssetPath, string SubPath);
public sealed record UnrealSoftClassReference(
    UnrealObjectReference Object, string AssetPath, string SubPath);
public readonly record struct UnrealDelegate(
    UnrealObjectReference Target, UnrealName FunctionName);

public sealed class UnrealMulticastDelegate : IReadOnlyList<UnrealDelegate>
{
    private readonly UnrealDelegate[] _bindings;
    internal UnrealMulticastDelegate(UnrealDelegate[] bindings) => _bindings = bindings;
    public int Count => _bindings.Length;
    public UnrealDelegate this[int index] => _bindings[index];
    public IEnumerator<UnrealDelegate> GetEnumerator() =>
        ((IEnumerable<UnrealDelegate>)_bindings).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _bindings.GetEnumerator();
}

public sealed class UnrealFieldPath : IReadOnlyList<UnrealName>
{
    private readonly UnrealName[] _segments;
    internal UnrealFieldPath(UnrealName[] segments) => _segments = segments;
    public int Count => _segments.Length;
    public UnrealName this[int index] => _segments[index];
    public IEnumerator<UnrealName> GetEnumerator() =>
        ((IEnumerable<UnrealName>)_segments).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _segments.GetEnumerator();
}

/// <summary>An address-free snapshot of a native <c>TSet&lt;T&gt;</c>.</summary>
public sealed class UnrealSet<T> : IReadOnlyCollection<T>
{
    private readonly T[] _items;

    /// <summary>
    /// Creates a value that can be assigned to a generated TSet property. Unreal's
    /// reflected hash and equality rules are applied by the runtime at write time.
    /// </summary>
    public UnrealSet(params T[] items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = (T[])items.Clone();
    }
    public int Count => _items.Length;
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _items.GetEnumerator();
    public T[] ToArray() => (T[])_items.Clone();
}

public readonly record struct UnrealMapEntry<TKey, TValue>(TKey Key, TValue Value);

/// <summary>
/// An address-free snapshot of a native <c>TMap&lt;TKey,TValue&gt;</c>. A list of
/// entries is used rather than Dictionary so malformed or custom Unreal key
/// equality cannot silently discard data while crossing the ABI.
/// </summary>
public sealed class UnrealMap<TKey, TValue> : IReadOnlyList<UnrealMapEntry<TKey, TValue>>
{
    private readonly UnrealMapEntry<TKey, TValue>[] _entries;

    /// <summary>
    /// Creates a value that can be assigned to a generated TMap property. Duplicate
    /// keys are rejected by the runtime according to Unreal's reflected equality.
    /// </summary>
    public UnrealMap(params UnrealMapEntry<TKey, TValue>[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = (UnrealMapEntry<TKey, TValue>[])entries.Clone();
    }
    public int Count => _entries.Length;
    public UnrealMapEntry<TKey, TValue> this[int index] => _entries[index];
    public IEnumerator<UnrealMapEntry<TKey, TValue>> GetEnumerator() =>
        ((IEnumerable<UnrealMapEntry<TKey, TValue>>)_entries).GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _entries.GetEnumerator();
    public UnrealMapEntry<TKey, TValue>[] ToArray() =>
        (UnrealMapEntry<TKey, TValue>[])_entries.Clone();
}
