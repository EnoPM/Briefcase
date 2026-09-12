using Briefcase.ModApi.Interop;

namespace Briefcase.ModApi;

/// <summary>Public UObject flags useful when inspecting object ownership.</summary>
[Flags]
public enum UnrealObjectFlags : uint
{
    None = 0,
    Public = 0x00000001,
    Standalone = 0x00000002,
    MarkAsNative = 0x00000004,
    Transactional = 0x00000008,
    ClassDefaultObject = 0x00000010,
    ArchetypeObject = 0x00000020,
    Transient = 0x00000040
}

/// <summary>How Unreal should handle collisions while spawning an actor.</summary>
public enum UnrealSpawnCollisionHandling : byte
{
    Undefined = 0,
    AlwaysSpawn = 1,
    AdjustIfPossibleButAlwaysSpawn = 2,
    AdjustIfPossibleButDoNotSpawn = 3,
    DoNotSpawnIfColliding = 4
}

public readonly unsafe partial struct UnrealApi
{
    private const uint ObjectAccessApiVersion = 14;
    private const uint AssetLoadingApiVersion = 15;
    private const string ActorClassPath = "/Script/Engine.Actor";
    private const string GameplayStaticsPath = "/Script/Engine.GameplayStatics";
    private const string SubsystemLibraryPath = "/Script/Engine.SubsystemBlueprintLibrary";
    private const string TransformPath = "/Script/CoreUObject.Transform";

    private static readonly UnrealParameter SpawnObjectReturn =
        new("ReturnValue", typeof(UnrealObjectReference), 16, 8, IsOut: true);
    private static readonly UnrealFunction SpawnObjectFunction = new(
        GameplayStaticsPath,
        "SpawnObject",
        24,
        [
            new UnrealParameter("ObjectClass", typeof(UnrealObjectReference), 0, 8),
            new UnrealParameter("Outer", typeof(UnrealObjectReference), 8, 8),
            SpawnObjectReturn
        ],
        typeof(UnrealObjectReference))
    {
        ReturnParameter = SpawnObjectReturn
    };

    private static readonly UnrealFunction DestroyActorFunction = new(
        ActorClassPath, "K2_DestroyActor", 0, []);

    private static readonly UnrealParameter EngineSubsystemReturn =
        new("ReturnValue", typeof(UnrealObjectReference), 8, 8, IsOut: true);
    private static readonly UnrealFunction EngineSubsystemFunction = new(
        SubsystemLibraryPath,
        "GetEngineSubsystem",
        16,
        [
            new UnrealParameter("Class", typeof(UnrealObjectReference), 0, 8),
            EngineSubsystemReturn
        ],
        typeof(UnrealObjectReference))
    {
        ReturnParameter = EngineSubsystemReturn
    };

    private static readonly UnrealParameter ContextSubsystemReturn =
        new("ReturnValue", typeof(UnrealObjectReference), 16, 8, IsOut: true);

    /// <summary>Whether the host provides the API v14 object identity primitives.</summary>
    public bool IsObjectAccessAvailable =>
        IsAvailable && _api->ApiVersion >= ObjectAccessApiVersion &&
        _api->GetClassDefaultObject != null && _api->GetObjectOuter != null &&
        _api->GetObjectFlags != null;

    /// <summary>Whether the host can synchronously load and retain Unreal assets.</summary>
    public bool IsAssetLoadingAvailable =>
        IsObjectAccessAvailable && _api->ApiVersion >= AssetLoadingApiVersion &&
        _api->LoadObject != null && _api->AcquireObjectRoot != null &&
        _api->ReleaseObjectRoot != null;

    /// <summary>Finds a loaded object and verifies its generated Unreal type.</summary>
    public bool TryFindObject<T>(
        string path, UnrealClass<T> unrealClass, out T? result)
        where T : UnrealObject, IUnrealObject<T>
    {
        if (!TryFindObject(path, out var candidate) || !candidate!.IsA(unrealClass))
        {
            result = null;
            return false;
        }
        result = T.FromObject(this, candidate.Handle);
        return true;
    }

    /// <summary>Finds a loaded object and fails if its type differs from the requested class.</summary>
    public T FindObject<T>(string path, UnrealClass<T> unrealClass)
        where T : UnrealObject, IUnrealObject<T>
    {
        if (!TryFindObject(path, out var candidate))
            throw new UnrealApiException("FindObject", NativeUnrealResult.NotFound);
        if (!candidate!.IsA(unrealClass))
            throw new UnrealApiException("FindObject(type)", NativeUnrealResult.TypeMismatch);
        return T.FromObject(this, candidate.Handle);
    }

    /// <summary>
    /// Wraps a reference supplied by Briefcase and verifies that the live UObject
    /// still belongs to the requested generated class hierarchy.
    /// </summary>
    public T FromReference<T>(UnrealObjectReference reference, UnrealClass<T> unrealClass)
        where T : UnrealObject, IUnrealObject<T> =>
        FromCheckedReference(reference, unrealClass, "FromReference(type)");

    /// <summary>Returns the class object as an address-free Unreal reference.</summary>
    public UnrealObjectReference GetClassReference<T>(UnrealClass<T> unrealClass)
        where T : UnrealObject, IUnrealObject<T> =>
        new(FindMetadata(unrealClass.Path));

    /// <summary>Returns the validated class default object for a generated class.</summary>
    public T GetDefaultObject<T>(UnrealClass<T> unrealClass)
        where T : UnrealObject, IUnrealObject<T>
    {
        var handle = GetDefaultObjectHandle(unrealClass.Path);
        return T.FromObject(this, handle);
    }

    /// <summary>Returns an object's immediate Unreal Outer, or null at the package root.</summary>
    public UnrealObject? GetOuter(UnrealObject instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        EnsureObjectAccessAvailable();
        if (instance.IsNull) return null;
        UnrealObjectHandle outer;
        EnsureSuccess("GetObjectOuter", _api->GetObjectOuter(
            _api->Context, instance.Handle, &outer));
        return outer.IsNull ? null : new UnrealObject(this, outer);
    }

    /// <summary>Returns an object's reflected EObjectFlags.</summary>
    public UnrealObjectFlags GetFlags(UnrealObject instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        EnsureObjectAccessAvailable();
        if (instance.IsNull)
            throw new ArgumentException("The Unreal instance is null.", nameof(instance));
        uint flags;
        EnsureSuccess("GetObjectFlags", _api->GetObjectFlags(
            _api->Context, instance.Handle, &flags));
        return (UnrealObjectFlags)flags;
    }

    /// <summary>
    /// Resolves an engine subsystem through Unreal's reflected
    /// SubsystemBlueprintLibrary. Invoke this on the game thread.
    /// </summary>
    public T GetEngineSubsystem<T>(UnrealClass<T> subsystemClass)
        where T : UnrealObject, IUnrealObject<T> =>
        InvokeSubsystem<T>(EngineSubsystemFunction, subsystemClass,
            [GetClassReference(subsystemClass)]);

    /// <summary>Resolves a game-instance subsystem for a world-context object.</summary>
    public T GetGameInstanceSubsystem<T>(
        UnrealObject contextObject, UnrealClass<T> subsystemClass)
        where T : UnrealObject, IUnrealObject<T> =>
        InvokeContextSubsystem("GetGameInstanceSubsystem", contextObject, subsystemClass);

    /// <summary>Resolves a world subsystem for a world-context object.</summary>
    public T GetWorldSubsystem<T>(
        UnrealObject contextObject, UnrealClass<T> subsystemClass)
        where T : UnrealObject, IUnrealObject<T> =>
        InvokeContextSubsystem("GetWorldSubsystem", contextObject, subsystemClass);

    /// <summary>Resolves a local-player subsystem for a world-context object.</summary>
    public T GetLocalPlayerSubsystem<T>(
        UnrealObject contextObject, UnrealClass<T> subsystemClass)
        where T : UnrealObject, IUnrealObject<T> =>
        InvokeContextSubsystem("GetLocalPlayerSubsystem", contextObject, subsystemClass);

    /// <summary>
    /// Creates a non-actor UObject through the vanilla GameplayStatics SpawnObject
    /// UFunction. Unreal owns the result through the supplied Outer.
    /// </summary>
    public T CreateObject<T>(UnrealClass<T> unrealClass, UnrealObject outer)
        where T : UnrealObject, IUnrealObject<T>
    {
        ArgumentNullException.ThrowIfNull(outer);
        if (outer.IsNull)
            throw new ArgumentException("The Unreal Outer is null.", nameof(outer));
        var defaultObject = GetDefaultObject(unrealClass);
        if (defaultObject.IsA(new UnrealClass<UnrealObjectAdapter>(ActorClassPath)))
            throw new ArgumentException(
                "Actor classes must be created with SpawnActor.", nameof(unrealClass));

        var library = GetDefaultObjectHandle(GameplayStaticsPath);
        var reference = Invoke<UnrealObjectReference>(
            library,
            SpawnObjectFunction,
            [GetClassReference(unrealClass), outer.Reference]);
        return FromCheckedReference(reference, unrealClass, "SpawnObject");
    }

    /// <summary>
    /// Spawns and finishes an actor through GameplayStatics' deferred spawn path.
    /// TTransform must be the generated /Script/CoreUObject.Transform value type.
    /// </summary>
    public TActor SpawnActor<TActor, TTransform>(
        UnrealClass<TActor> actorClass,
        UnrealObject worldContext,
        TTransform transform,
        UnrealSpawnCollisionHandling collisionHandling = UnrealSpawnCollisionHandling.Undefined,
        UnrealObject? owner = null)
        where TActor : UnrealObject, IUnrealObject<TActor>
        where TTransform : unmanaged, IUnrealStructValue
    {
        ArgumentNullException.ThrowIfNull(worldContext);
        if (worldContext.IsNull)
            throw new ArgumentException("The Unreal world context is null.", nameof(worldContext));
        ValidateTransform(transform);
        var defaultObject = GetDefaultObject(actorClass);
        if (!defaultObject.IsA(new UnrealClass<UnrealObjectAdapter>(ActorClassPath)))
            throw new ArgumentException("The requested class is not an Actor class.", nameof(actorClass));

        var library = GetDefaultObjectHandle(GameplayStaticsPath);
        var nullReference = UnrealObjectReference.Null;
        var ownerReference = owner is null || owner.IsNull ? nullReference : owner.Reference;
        var deferred = Invoke<UnrealObjectReference>(
            library,
            SpawnActorDescriptors<TTransform>.Begin,
            [worldContext.Reference, GetClassReference(actorClass), transform,
             (byte)collisionHandling, ownerReference]);
        if (deferred.IsNull)
            throw new UnrealApiException("BeginDeferredActorSpawnFromClass", NativeUnrealResult.NotFound);

        try
        {
            var finished = Invoke<UnrealObjectReference>(
                library,
                SpawnActorDescriptors<TTransform>.Finish,
                [deferred, transform]);
            return FromCheckedReference(finished, actorClass, "FinishSpawningActor");
        }
        catch
        {
            TryDestroyActor(deferred);
            throw;
        }
    }

    /// <summary>Requests destruction through Actor.K2_DestroyActor on the game thread.</summary>
    public void DestroyActor(UnrealObject actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.IsNull)
            throw new ArgumentException("The Unreal actor is null.", nameof(actor));
        if (!actor.IsA(new UnrealClass<UnrealObjectAdapter>(ActorClassPath)))
            throw new ArgumentException("The Unreal object is not an Actor.", nameof(actor));
        InvokeVoid(actor.Handle, DestroyActorFunction, []);
    }

    private T InvokeContextSubsystem<T>(
        string functionName, UnrealObject contextObject, UnrealClass<T> subsystemClass)
        where T : UnrealObject, IUnrealObject<T>
    {
        ArgumentNullException.ThrowIfNull(contextObject);
        if (contextObject.IsNull)
            throw new ArgumentException("The Unreal context object is null.", nameof(contextObject));
        var function = ContextSubsystemFunctions.For(functionName);
        return InvokeSubsystem<T>(function, subsystemClass,
            [contextObject.Reference, GetClassReference(subsystemClass)]);
    }

    private T InvokeSubsystem<T>(
        UnrealFunction function,
        UnrealClass<T> subsystemClass,
        object?[] arguments)
        where T : UnrealObject, IUnrealObject<T>
    {
        var library = GetDefaultObjectHandle(SubsystemLibraryPath);
        var reference = Invoke<UnrealObjectReference>(library, function, arguments);
        return FromCheckedReference(reference, subsystemClass, function.Name);
    }

    private T FromCheckedReference<T>(
        UnrealObjectReference reference, UnrealClass<T> unrealClass, string operation)
        where T : UnrealObject, IUnrealObject<T>
    {
        if (reference.IsNull)
            throw new UnrealApiException(operation, NativeUnrealResult.NotFound);
        if (!IsA(reference.Handle, unrealClass.Path))
            throw new UnrealApiException(operation, NativeUnrealResult.TypeMismatch);
        return T.FromObject(this, reference.Handle);
    }

    private UnrealObjectHandle GetDefaultObjectHandle(string classPath)
    {
        EnsureObjectAccessAvailable();
        var classHandle = FindMetadata(classPath);
        UnrealObjectHandle result;
        var status = _api->GetClassDefaultObject(_api->Context, classHandle, &result);
        if (status == NativeUnrealResult.StaleHandle)
        {
            classHandle = RefreshMetadata(classPath);
            status = _api->GetClassDefaultObject(_api->Context, classHandle, &result);
        }
        EnsureSuccess("GetClassDefaultObject", status);
        return result;
    }

    internal UnrealObjectHandle LoadObjectHandle(string path, string expectedClassPath)
    {
        EnsureAssetLoadingAvailable();
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedClassPath);
        var classHandle = FindMetadata(expectedClassPath);
        var encodedPath = Encode(path);
        UnrealObjectHandle result;
        fixed (byte* pathPointer = encodedPath)
        {
            var status = _api->LoadObject(
                _api->Context, classHandle, pathPointer,
                checked((uint)encodedPath.Length), &result);
            if (status == NativeUnrealResult.StaleHandle)
            {
                classHandle = RefreshMetadata(expectedClassPath);
                status = _api->LoadObject(
                    _api->Context, classHandle, pathPointer,
                    checked((uint)encodedPath.Length), &result);
            }
            EnsureSuccess("LoadObject", status);
        }
        return result;
    }

    internal void AcquireObjectRoot(UnrealObjectHandle handle)
    {
        EnsureAssetLoadingAvailable();
        EnsureSuccess("AcquireObjectRoot",
            _api->AcquireObjectRoot(_api->Context, handle));
    }

    internal void ReleaseObjectRoot(UnrealObjectHandle handle)
    {
        EnsureAssetLoadingAvailable();
        EnsureSuccess("ReleaseObjectRoot",
            _api->ReleaseObjectRoot(_api->Context, handle));
    }

    private void EnsureObjectAccessAvailable()
    {
        EnsureAvailable();
        if (!IsObjectAccessAvailable)
            throw new NotSupportedException("The host does not expose Unreal object access API v14.");
    }

    private void EnsureAssetLoadingAvailable()
    {
        EnsureObjectAccessAvailable();
        if (!IsAssetLoadingAvailable)
            throw new NotSupportedException("The host does not expose Unreal asset loading API v15.");
    }

    private static void ValidateTransform<TTransform>(TTransform transform)
        where TTransform : unmanaged, IUnrealStructValue
    {
        var path = typeof(TTransform).GetField(
            "UnrealPath",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?
            .GetRawConstantValue() as string;
        if (!string.Equals(path, TransformPath, StringComparison.Ordinal) || transform.Size != 48)
            throw new ArgumentException(
                "The spawn transform must be the generated /Script/CoreUObject.Transform type.",
                nameof(transform));
    }

    private void TryDestroyActor(UnrealObjectReference actor)
    {
        if (actor.IsNull) return;
        try { InvokeVoid(actor.Handle, DestroyActorFunction, []); }
        catch { }
    }

    private static class ContextSubsystemFunctions
    {
        internal static UnrealFunction For(string name) => new(
            SubsystemLibraryPath,
            name,
            24,
            [
                new UnrealParameter("ContextObject", typeof(UnrealObjectReference), 0, 8),
                new UnrealParameter("Class", typeof(UnrealObjectReference), 8, 8),
                ContextSubsystemReturn
            ],
            typeof(UnrealObjectReference))
        {
            ReturnParameter = ContextSubsystemReturn
        };
    }

    private static class SpawnActorDescriptors<TTransform>
        where TTransform : unmanaged, IUnrealStructValue
    {
        private static readonly UnrealParameter BeginReturn =
            new("ReturnValue", typeof(UnrealObjectReference), 80, 8, IsOut: true);
        private static readonly UnrealParameter FinishReturn =
            new("ReturnValue", typeof(UnrealObjectReference), 64, 8, IsOut: true);

        internal static readonly UnrealFunction Begin = new(
            GameplayStaticsPath,
            "BeginDeferredActorSpawnFromClass",
            88,
            [
                new UnrealParameter("WorldContextObject", typeof(UnrealObjectReference), 0, 8),
                new UnrealParameter("ActorClass", typeof(UnrealObjectReference), 8, 8),
                new UnrealParameter("SpawnTransform", typeof(TTransform), 16, 48),
                new UnrealParameter("CollisionHandlingOverride", typeof(byte), 64, 1),
                new UnrealParameter("Owner", typeof(UnrealObjectReference), 72, 8),
                BeginReturn
            ],
            typeof(UnrealObjectReference))
        {
            ReturnParameter = BeginReturn
        };

        internal static readonly UnrealFunction Finish = new(
            GameplayStaticsPath,
            "FinishSpawningActor",
            72,
            [
                new UnrealParameter("Actor", typeof(UnrealObjectReference), 0, 8),
                new UnrealParameter("SpawnTransform", typeof(TTransform), 16, 48),
                FinishReturn
            ],
            typeof(UnrealObjectReference))
        {
            ReturnParameter = FinishReturn
        };
    }

    // A private adapter lets object-access internals validate well-known base
    // classes without creating a compile-time dependency on the generated SDK.
    private sealed class UnrealObjectAdapter : UnrealObject, IUnrealObject<UnrealObjectAdapter>
    {
        private UnrealObjectAdapter(UnrealApi api, UnrealObjectHandle handle) : base(api, handle) { }
        public static UnrealObjectAdapter FromObject(UnrealApi api, UnrealObjectHandle handle) =>
            new(api, handle);
    }
}
