# Client mod UI

`Briefcase.ClientModApi` is the client-only contract for complex configuration
panels. Registering a panel gives the mod its own navigation entry and lets it
describe controls once while Briefcase chooses the Avalonia renderer
from `Briefcase/loader.json`. Mods that only need standard persistent settings
can use `context.Configuration.Bind(...)`; Briefcase renders those settings in
the central `Mods` page without any UI code.

Add this project reference during development:

```xml
<ProjectReference Include="..\..\managed\Briefcase.ClientModApi\Briefcase.ClientModApi.csproj"
                  Private="false" />
```

The assembly is supplied by the client loader. Do not copy it into a mod
package. It is deliberately absent from the dedicated-server distribution, so
server mods use `context.Configuration.Bind(...)` and remote administration.

## Register a panel

```csharp
using Briefcase.ClientModApi;
using Briefcase.ModApi;

public sealed class ExampleMod : BriefcaseMod
{
    private IDisposable? _panel;
    private bool _enabled = true;
    private int _radius = 25;
    private string _label = "Example";

    public override void Load(ModContext context)
    {
        _panel = context.Ui().RegisterPanel(
            Ui.Column(
                Ui.Text("The values below are live managed state."),
                Ui.Section("General",
                    Ui.Toggle("Enabled", () => _enabled, value => _enabled = value),
                    Ui.Number(
                        "Radius",
                        () => _radius,
                        value => _radius = value,
                        minimum: 0,
                        maximum: 100,
                        step: 1),
                    Ui.TextField(
                        "Label",
                        () => _label,
                        value => _label = value,
                        commitMode: UiCommitMode.OnCommit))));
    }

    public override void Unload()
    {
        _panel?.Dispose();
        _panel = null;
    }
}
```

Disposing the registration releases the component tree and its delegates.
Briefcase also scopes registrations to the current mod generation so hot reload
does not keep an old mod assembly alive.

## Reactive ViewModels

For event-driven UI, derive a client ViewModel from `BriefcaseViewModel` and
create strongly typed bindings from direct properties:

```csharp
private sealed class SettingsViewModel : BriefcaseViewModel
{
    private bool _enabled = true;
    private string _status = "Ready";

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }

    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }
}

var viewModel = new SettingsViewModel();
_panel = context.Ui().RegisterPanel(
    Ui.Card("General",
        Ui.Toggle("Enabled", Ui.Bind(viewModel, model => model.Enabled)),
        Ui.Status(
            Ui.Observe(viewModel, model => model.Status),
            UiStatusTone.Success)));
```

`Ui.Bind` creates a two-way binding to a public writable property. `Ui.Observe`
creates a read-only binding for text or status output. Both listen to
`INotifyPropertyChanged`; notifications raised from game, network, or worker
threads are coalesced and dispatched to Avalonia's UI thread. Bindings target a
direct property such as `model => model.Enabled`, which keeps notification and
unsubscription behavior explicit.

A persistent setting can be bound without a ViewModel adapter:

```csharp
var enabled = context.Configuration.Bind(
    "General", "Enabled", true, "Enables the feature.");
var toggle = Ui.Toggle("Enabled", Ui.Bind(enabled));
```

Legacy `Func<T>` and `Action<T>` overloads remain supported. Avalonia refreshes
those delegates periodically; new ViewModel bindings update immediately.

### ViewModel commands

Use `UiCommand` for an action whose availability depends on ViewModel state:

```csharp
private readonly UiCommand _connectCommand;

public SettingsViewModel()
{
    _connectCommand = new UiCommand(Connect, () => !IsConnected);
}

public UiCommand ConnectCommand => _connectCommand;

private void RefreshCommands() =>
    _connectCommand.NotifyCanExecuteChanged();
```

The command can be passed directly to a button. Avalonia reacts to
`CanExecuteChanged` and updates the enabled state without rebuilding the component:

```csharp
Ui.Button("Connect", viewModel.ConnectCommand, UiIcon.Plug)
```

## Styles, vector icons, and reusable components

Every component accepts semantic classes before registration:

```csharp
Ui.Button("Apply", Apply)
    .WithClass(UiClasses.Primary);

Ui.Button("Delete", Delete)
    .WithClasses(UiClasses.Danger, UiClasses.Compact);
```

Briefcase currently defines `Primary`, `Secondary`, `Danger`, `Compact`,
`Card`, and status classes. Avalonia applies them through the framework XAML
theme, while Avalonia maps semantic variants to theme resources.

Buttons and standalone content can use semantic vector icons without an
Avalonia reference or image asset:

```csharp
Ui.Row(
    Ui.Icon(UiIcon.Server, size: 24, UiTextTone.Accent),
    Ui.Button("Refresh", UiIcon.Refresh, Refresh),
    Ui.Button("Open folder", UiIcon.Folder, OpenFolder));
```

Avalonia draws the built-in 24x24 vector geometries at the requested size, so no
bitmap asset or toolkit-specific object crosses the public mod API.

Reusable toolkit-neutral containers are available for common layouts:

```csharp
Ui.Card("Connection",
    Ui.Status("Connected", UiStatusTone.Success),
    Ui.Expander("Details", initiallyExpanded: false,
        Ui.Text("Endpoint: 127.0.0.1:7777")));
```

`Ui.Card`, `Ui.Expander`, and `Ui.Status` can be composed into helpers owned by
a mod without referencing Avalonia. Custom class names are accepted as a future
extension point, but only the classes documented by Briefcase receive built-in
styling.

Controls can provide a short plain-language explanation. Briefcase places it
below the label and keeps the editor aligned on the right:

```csharp
Ui.Number("Scan radius", () => radius, value => radius = value, 1, 100, 1)
    .WithDescription("Maximum distance, in metres, used to find nearby targets.");
```

Descriptions should explain the effect of a setting rather than repeat its
name. This keeps generated pages useful to players who do not know the mod's
implementation details.

## Components

- `Ui.Column` and `Ui.Row` arrange child components.
- `Ui.Section` groups related controls under a title.
- `Ui.Card` creates a themed container for related content.
- `Ui.Expander` creates a collapsible group.
- `Ui.Status` displays a semantic neutral, information, success, warning, or error state.
- `Ui.Text` displays live or static text.
- `Ui.Icon` displays a scalable filled vector icon in Avalonia.
- `Ui.Button` invokes an action or command and can include an icon.
- `Ui.Toggle` binds a Boolean value.
- `Ui.TextField` binds text, with optional password masking and commit mode.
- `Ui.Number` binds `int`, `float`, or `double` with explicit limits and step.
- `Ui.Choice` binds one value from a list of choices.
- `Ui.Dynamic` rebuilds a subtree when its revision changes.
- `Ui.Separator` and `Ui.Spacer` control visual grouping.

Briefcase groups automatic configuration by section in responsive cards. A
page uses two columns when space permits and one column in a narrower game
window. Changes stay in a session draft while the player moves between pages
or closes the menu. **Apply changes** writes only the current page; **Cancel**
restores that page's last applied values. A violet dot beside the mod name
marks a pending draft.

Custom panels keep the commit behavior defined by their components. Use
`UiCommitMode.OnCommit` when an expensive value should wait for Enter or focus
loss, or expose explicit Apply and Cancel commands for a complex workflow.

Most components accept visibility, enabled-state, or tooltip delegates. Keep
these delegates fast because the active panel evaluates them during UI refresh.
Use `UiCommitMode.OnCommit` for values that should only change after Enter or
focus loss. Use `UiCommitMode.Immediate` for live filtering and toggles.

For changing collections or layouts, prefer a revision counter:

```csharp
Ui.Dynamic(
    () => Ui.Column(_players.Select(player => Ui.Text(player.Name)).ToArray()),
    revision: () => _playersRevision)
```

Increment the revision only after replacing the data. Avalonia can then retain
the existing controls between changes instead of rebuilding them on every
refresh.

## Configuration and overlays

Use `context.Configuration.Bind(...)` by itself when Briefcase's generated
editors are sufficient. Register a client panel only for layouts or interactions
that need a dedicated page, and bind its components to the corresponding
`ConfigEntry<T>.Value`. ESP markers, crosshairs, and other per-frame game
drawings continue to use the rendering API because they belong on the
low-latency overlay path.
