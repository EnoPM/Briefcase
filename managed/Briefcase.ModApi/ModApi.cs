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
    /// Controls whether this component receives an entry in the client mod
    /// navigation. Built-in Core services can expose a dedicated server view
    /// while keeping the ordinary mod list focused on user-installed mods.
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

    public ModContext(NativeHostApi* api)
    {
        _api = api;
        _configuration = default;
        _mods = default;
    }

    private ModContext(
        NativeHostApi* api,
        ConfigurationApi configuration,
        ModManagementApi mods)
    {
        _api = api;
        _configuration = configuration;
        _mods = mods;
    }

    internal ModContext WithConfiguration(IModConfigurationScope configuration) =>
        new(_api, new ConfigurationApi(configuration), _mods);

    internal ModContext WithModManagement(IModManagementBackend backend) =>
        new(_api, _configuration, new ModManagementApi(backend));

    internal NativeRenderingApi* RenderingNative => IsValid ? _api->Rendering : null;
    internal NativePatchingApi* PatchingNative => IsValid ? _api->Patching : null;

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
    public RenderingApi Rendering => new(IsValid ? _api->Rendering : null, this);
    public InputApi Input => new();
    public ConfigurationApi Configuration => _configuration;
    public ModManagementApi Mods => _mods;

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
public readonly record struct UnrealProperty<T>(string OwnerPath, string Name, int Offset, int Size);
public readonly record struct UnrealParameter(
    string Name, Type ManagedType, int Offset, int Size, bool IsOut = false);
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

// TArray access will receive a dedicated bounded ABI. This placeholder carries
// no game address and cannot be used to dereference memory from managed code.
public readonly record struct UnrealArray(uint Reserved);
