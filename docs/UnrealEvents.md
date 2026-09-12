# Unreal events

Briefcase 0.17 exposes generated Unreal dynamic multicast delegates as scoped
C# subscriptions. A mod supplies the live publisher object and the generated
property descriptor; Briefcase resolves and validates the delegate again before
it changes native state.

```csharp
private IDisposable? _subscription;

public void Load(ModContext context)
{
    context.GameThread.OnEngineReady(() =>
    {
        MyGeneratedActor actor = FindActor(context);
        _subscription = context.Events.Subscribe(
            actor,
            MyGeneratedActor.Properties.OnStateChanged,
            (publisher, arguments) =>
            {
                int state = arguments.Get<int>("NewState");
                context.Info($"{publisher.Path}: state={state}");
            });
    });
}

public void Unload() => _subscription?.Dispose();
```

`Properties.OnStateChanged` is an
`UnrealProperty<UnrealMulticastDelegate>` generated from snapshot schema 4. Its
descriptor includes the exact reflected `UFunction` signature. Properties from
older snapshots have no signature and fail with a clear exception instead of
guessing a parameter layout.

Registration is asynchronous: `Subscribe` queues the native binding for the
next game-thread pulse and immediately returns its `IDisposable`. Disposing it
before that pulse cancels the pending registration. Event handlers execute on
the Unreal game thread and should finish quickly; expensive work can copy the
managed values it needs and continue elsewhere.

Internally, the dynamic delegate points to an unused method on a stable class
default object whose reflected parameter layout exactly matches the event. The
global ProcessEvent hook intercepts only that object and method, substitutes the
actual publisher in the managed callback, and suppresses the sink method body.
Mods never select or invoke this implementation detail themselves.

`UnrealEventArguments` contains pointer-free copies made while Unreal's native
parameter buffer is valid. `Names`, `Count`, the string indexer, `Get<T>` and
`TryGet<T>` expose input, output and return parameters by their generated Unreal
names. Scalar values, strings, text, generated structs, object wrappers and the
supported recursive containers use the same conversion rules as attributed
ProcessEvent patches.

Every subscription belongs to the mod scope. Briefcase unregisters native
callbacks before unloading the collectible assembly, including failed loads and
hot reload. Explicit disposal remains useful when an individual object or UI
screen is no longer relevant.

The first implementation supports `MulticastInlineDelegateProperty`. Unreal's
sparse multicast delegates use different engine-managed storage and are
rejected. Briefcase also rejects invalid or stale objects, mismatched owner
classes, changed property layouts, unsupported signatures and pre-existing
bindings that would have to be overwritten.

`samples/Briefcase.EventSample` contains a complete client example. It observes
the vanilla server-browser subsystem, reads a typed `TArray<FDIServerInfo>` and
disposes its subscription from inside the event callback.
