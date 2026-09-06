# Mod configuration

F1 is reserved by Briefcase. It opens one movable ImGui.NET window owned by the
framework. Briefcase restores the window's last position, size, selected view,
and selected mod. A top switch selects **Client** or **Server**. Loaded client
mods are presented as vertical tabs on the left; the Server view is supplied by
the Briefcase Core administration built-in.

The first tab is always `Briefcase`. It lists every DLL installed in
`Briefcase/Mods` and exposes its persistent Enabled state plus the current
session's Load, Reload, and Unload actions. Disabling a mod unloads it
immediately and prevents automatic loading at the next game start. A manual
Unload keeps the Enabled preference, while a manual Load can run a disabled mod
for the current session.

Dependency state is part of this library. A dependency cannot be disabled,
unloaded, or reloaded while an enabled dependent uses it. The disabled control's
tooltip lists the mods that must be disabled first.

The same tab can open the Mods directory or refresh it manually. The directory
watcher discovers a newly copied DLL and hot reloads a replaced DLL when that
mod is enabled or currently loaded. A remote catalogue and download/update UI
can be added to this tab without changing the mod configuration API.

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

Briefcase currently provides automatic editors for `bool`, `int`, `float`,
`double`, `string`, and enum values. Numeric ranges are inclusive. A string can
set `secret: true` to use ImGui's password editor. Invalid persisted values fall
back to the default supplied by the mod.

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

## Rich configuration panels

Automatic controls cover ordinary preferences. A mod with a richer editor can
append custom ImGui content to its own tab:

```csharp
private IDisposable? _panel;

public override void Load(ModContext context)
{
    _panel = context.Configuration.RegisterPanel(panel =>
    {
        panel.ImGui.Text("This runs inside the mod's Briefcase tab.");
        // ImGuiNET.ImGui can also be called directly here.
    });
}

public override void Unload()
{
    _panel?.Dispose();
    _panel = null;
}
```

The callback runs on the managed rendering thread with the shared ImGui context
active. It must stay fast and must not retain the `ConfigurationPanelContext`.
Briefcase also removes all entries and panels for a mod generation during hot
reload, even if the mod forgets to dispose its panel.

F1 cannot be assigned as a mod hotkey. Escape and the window close button both
return input to the game. Overlays registered with `RenderWhenMenuHidden` keep
rendering independently of the configuration window.
