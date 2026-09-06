# Managed mod dependencies

Mods declare dependencies with stable `ModInfo.Id` values:

```csharp
public override ModInfo Info { get; } = new(
    Id: "example.inventory-ui",
    Name: "Inventory UI",
    Author: "Example",
    Version: "1.0.0",
    Description: "Adds an inventory panel.")
{
    Dependencies = ["example.inventory-core"]
};
```

Briefcase inventories metadata before calling any `Load` method. It then sorts
enabled mods topologically, which guarantees that every dependency is loaded
before its consumers. Enabling or loading a mod also enables its complete
dependency chain.

A missing dependency, duplicate ID, self-dependency, or cycle prevents only the
affected part of the graph from loading. The error is shown in the Briefcase
mod library and written to `Briefcase.log`.

An enabled or loaded dependent protects its dependency. The dependency's
disable, unload, and reload controls remain disabled until every listed
dependent has been disabled. Hover a disabled control to see which mods must be
stopped first. Briefcase uses the reverse order during framework shutdown.

Several mods can depend on one shared service mod. The service can perform an
expensive Unreal observation once and publish an immutable snapshot while each
consumer keeps its own settings and lifecycle.

`Briefcase/Core/BuiltIns` is separate from this graph. It contains indispensable
services shipped with Briefcase, such as client/server administration and the
vanilla community-balancing editor. Built-ins load automatically and never
appear in the user-mod lifecycle list.
