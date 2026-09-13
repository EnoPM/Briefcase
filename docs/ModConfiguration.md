# Mod configuration

F1 is reserved by Briefcase. It opens one framework-owned Avalonia configuration
window. Its stable navigation contains **Home**, **Servers**, and **Mods**.
Briefcase controls that navigation so mods cannot add top-level tabs.

The **Mods** page separates installed packages from the free **Marketplace**.
Each package lives below `Briefcase/Mods` in its own directory with a
`briefcase.mod.json` manifest. Briefcase migrates legacy DLLs that are still
placed directly in `Mods`.

The **Installed** view renders simple settings declared with
`Configuration.Bind` and exposes each mod's persistent Enabled state plus the
current session's Load, Reload, and Unload actions. Disabling a mod unloads it
immediately and prevents automatic loading at the next game start. A manual
Unload keeps the Enabled preference, while a manual Load can run a disabled mod
for the current session.

A configurable mod has a **Configure** action on its card. Its generated settings
and any registered complex panel open as a detail view inside **Mods**, with a
button to return to the installed list. Small mods therefore need no UI code.

Dependency state is part of this library. A dependency cannot be disabled,
unloaded, or reloaded while an enabled dependent uses it. The disabled control's
tooltip lists the mods that must be disabled first.

The **Marketplace** reads its curated list from the Briefcase GitHub repository
and installs packages from each mod's own GitHub releases. Missing catalogue
dependencies are installed first. See [Mod packages and marketplace](ModPackages.md)
for manifests, release assets, validation, and automatic updates.
## Typed settings

Call `ModContext.Configuration.Bind` during `Load`. The returned `ConfigEntry<T>`
is both the live value used by the mod and the value persisted by Briefcase:

```csharp
private ConfigEntry<bool> _enabled = null!;
private ConfigEntry<int> _radius = null!;

public override void Load(ModContext context)
{
    _enabled = context.Configuration.Bind(
        section: "General",
        key: "Enabled",
        defaultValue: true,
        description: "Enables this mod's runtime behavior.");

    _radius = context.Configuration.Bind(
        section: "General",
        key: "Radius",
        defaultValue: 25,
        description: "Maximum search radius in metres.",
        range: new ConfigurationRange<int>(1, 100));
}
```

Briefcase automatically places these settings in the mod's detail view on the
`Mods` page. It provides editors for `bool`,
`int`, `float`, `double`, `string`, and enum values. Numeric ranges are
inclusive. A string can set `secret: true` to use a password editor. Invalid
persisted values fall back to the default supplied by the mod.

Read `entry.Value` at the point where behavior is evaluated. Changes made in
the F1 menu are visible immediately. Assigning `entry.Value` from mod code uses
the same validation and saving path. `entry.ValueChanged` can be used when a
subsystem needs to rebuild derived state.

Settings, including the framework window placement, selected tab, and enabled
mod states, are written atomically to:

```text
DeceiveInc/Binaries/Win64/Briefcase/settings.json
```

Values are grouped by `ModInfo.Id`, then by the stable `section/key` pair. A mod
can change its displayed name without losing its saved values. Renaming its id,
section, or key intentionally creates a new setting.

## Rich client configuration panels

Client mods that need richer editors can register them with the toolkit-neutral
component API from `Briefcase.ClientModApi`. Registration adds the content to
that mod's detail view inside **Mods**; the same component tree is rendered by
the single Avalonia renderer:

```csharp
using Briefcase.ClientModApi;

private IDisposable? _panel;
private bool _enabled = true;
private float _strength = 0.75f;

public override void Load(ModContext context)
{
    _panel = context.Ui().RegisterPanel(
        Ui.Section("General",
            Ui.Toggle("Enabled", () => _enabled, value => _enabled = value),
            Ui.Number(
                "Strength",
                () => _strength,
                value => _strength = value,
                minimum: 0f,
                maximum: 1f,
                step: 0.05f)));
}

public override void Unload()
{
    _panel?.Dispose();
    _panel = null;
}
```

The component delegates read and update managed state. Use `Ui.Dynamic` for a
subtree whose content changes at runtime and supply a revision delegate when
possible, so retained backends only rebuild it after a meaningful change.

`Briefcase.ClientModApi` is a client-only contract. It is absent from the
dedicated-server package and server mods must use persistent `ConfigEntry<T>`
values, which Briefcase exposes through remote administration.

See [Client mod UI](ClientModUi.md) for the complete component list and design
guidelines.
F1 cannot be assigned as a mod hotkey. Escape and the window close button both
return input to the game. Overlays registered with `RenderWhenMenuHidden` keep
rendering independently of the configuration window.
