using Briefcase.ClientModApi;
using Briefcase.ManagedHost;
using Briefcase.ModApi;

namespace Briefcase.Core.Tests;

public sealed class ClientUiApiTests
{
    [Fact]
    public void Component_bindings_are_live_and_toolkit_neutral()
    {
        var enabled = false;
        var count = 3;
        var mode = "Solo";
        var panel = Ui.Column(
            Ui.Text(() => $"Count: {count}"),
            Ui.Toggle("Enabled", () => enabled, value => enabled = value),
            Ui.Number("Count", () => count, value => count = value, 1, 12),
            Ui.Choice("Mode", () => mode, value => mode = value,
                new[] { "Solo", "Duo", "Trio" }));

        var group = Assert.IsType<UiGroup>(panel);
        Assert.Equal("Count: 3", Assert.IsType<UiText>(group.Children[0]).Value());
        var toggle = Assert.IsType<UiToggle>(group.Children[1]);
        toggle.Changed(true);
        Assert.True(enabled);
        var number = Assert.IsType<UiNumberField>(group.Children[2]);
        number.Changed(9);
        Assert.Equal(9, count);
        var choice = Assert.IsType<UiChoice>(group.Children[3]);
        choice.Items.Single(item => item.Label == "Trio").Select();
        Assert.Equal("Trio", mode);
    }

    [Fact]
    public void ViewModel_binding_is_typed_two_way_and_observable()
    {
        var viewModel = new SampleViewModel();
        var binding = Ui.Bind(viewModel, model => model.Enabled);
        var invalidations = 0;

        using (binding.Subscribe(() => invalidations++))
        {
            binding.SetValue(true);
            Assert.True(viewModel.Enabled);
            Assert.Equal(1, invalidations);

            viewModel.Enabled = false;
            Assert.False(binding.Value);
            Assert.Equal(2, invalidations);
        }

        viewModel.Enabled = true;
        Assert.Equal(2, invalidations);
    }

    [Fact]
    public void Read_only_binding_rejects_editable_controls()
    {
        var viewModel = new SampleViewModel { Status = "Ready" };
        var binding = Ui.Observe(viewModel, model => model.Status);

        Assert.Equal("Ready", binding.Value);
        Assert.False(binding.CanWrite);
        Assert.Throws<ArgumentException>(() => Ui.TextField("Status", binding));
    }

    [Fact]
    public void ConfigEntry_binding_observes_changes_from_mod_and_ui()
    {
        using var directory = new TemporaryDirectory();
        using var registry = new ConfigurationRegistry(
            Path.Combine(directory.Path, "settings.json"),
            _ => { }, _ => { }, _ => { });
        using var scope = registry.RegisterMod(new ModInfo(
            "client-ui.binding", "Binding", "Briefcase", "1.0.0", ""));
        var entry = scope.Bind("General", "Count", 2, "", null, false);
        var binding = Ui.Bind(entry);
        var invalidations = 0;

        using var subscription = binding.Subscribe(() => invalidations++);
        entry.Value = 4;
        binding.SetValue(7);

        Assert.Equal(7, entry.Value);
        Assert.Equal(2, invalidations);
    }

    [Fact]
    public void Reusable_components_expose_semantic_structure_and_styles()
    {
        var status = Ui.Status("Connected", UiStatusTone.Success);
        var card = Ui.Card(
            "Connection",
            status,
            Ui.Expander("Details", true, Ui.Text("127.0.0.1")));
        card.WithClass(UiClasses.Compact);
        status.WithDescription("Current connection state");

        Assert.Equal("Connection", card.Title);
        Assert.Contains(UiClasses.Card, card.StyleClasses);
        Assert.Contains(UiClasses.Compact, card.StyleClasses);
        Assert.Contains(UiClasses.StatusSuccess, status.StyleClasses);
        Assert.Equal("Connected", status.Value());
        Assert.Equal("Current connection state", status.Description);
        Assert.Throws<ArgumentException>(() => status.WithDescription(" "));
        var expander = Assert.IsType<UiExpander>(card.Children[1]);
        Assert.True(expander.InitiallyExpanded);
        Assert.Throws<ArgumentException>(() => card.WithClass("invalid class"));
    }

    [Fact]
    public void Vector_icons_and_commands_remain_toolkit_neutral()
    {
        var allowed = false;
        var executions = 0;
        var invalidations = 0;
        var command = new UiCommand(() => executions++, () => allowed);
        var button = Ui.Button("Run", command, UiIcon.Play);
        var icon = Ui.Icon(UiIcon.Server, 24, UiTextTone.Accent);

        using var subscription = button.SubscribeInvalidated(() => invalidations++);
        Assert.Equal(UiIcon.Play, button.Icon);
        Assert.False(button.CanExecute);
        button.Execute();
        Assert.Equal(0, executions);

        allowed = true;
        command.NotifyCanExecuteChanged();
        Assert.Equal(1, invalidations);
        Assert.True(button.CanExecute);
        button.Execute();
        Assert.Equal(1, executions);
        Assert.Equal(24, icon.Size);
        Assert.Equal(UiIcon.Server, icon.Icon);
    }

    [Fact]
    public void Client_panel_registration_is_scoped_and_disposable()
    {
        using var directory = new TemporaryDirectory();
        using var registry = new ConfigurationRegistry(
            Path.Combine(directory.Path, "settings.json"),
            _ => { }, _ => { }, _ => { });
        using var scope = registry.RegisterMod(new ModInfo(
            "client-ui.test", "Client UI", "Briefcase", "1.0.0", ""));
        var context = scope.AttachTo(default);
        var api = context.Ui();
        var revision = registry.UiRevision;

        Assert.True(api.IsAvailable);

        using (api.RegisterPanel(Ui.Text("Hello")))
        {
            Assert.Single(scope.SnapshotUiPanels());
            Assert.True(registry.UiRevision > revision);
        }

        Assert.Empty(scope.SnapshotUiPanels());
    }

    [Fact]
    public void Explicit_complex_panel_updates_the_legacy_visibility_signal()
    {
        using var directory = new TemporaryDirectory();
        using var registry = new ConfigurationRegistry(
            Path.Combine(directory.Path, "settings.json"),
            _ => { }, _ => { }, _ => { });
        using var scope = registry.RegisterMod(new ModInfo(
            "client-ui.simple", "Simple UI", "Briefcase", "1.0.0", ""));

        scope.Bind("General", "Enabled", true, "", null, false);
        Assert.False(scope.ShouldShowClientTab);

        var context = scope.AttachTo(default);
        using (context.Ui().RegisterPanel(Ui.Text("Advanced")))
            Assert.True(scope.ShouldShowClientTab);

        Assert.False(scope.ShouldShowClientTab);
    }

    [Fact]
    public void Server_workspace_shares_selection_and_administration_navigation()
    {
        ServerWorkspace.ClearSelection();
        var observed = new List<ServerWorkspaceEvent>();
        using var subscription = ServerWorkspace.Subscribe(observed.Add);
        var profile = new ServerWorkspaceProfile(
            "test-server",
            "Test server",
            "127.0.0.1:50000",
            "game-secret",
            "127.0.0.1:47000",
            "admin-secret");

        try
        {
            ServerWorkspace.Select(profile);
            Assert.Single(observed);
            Assert.Same(profile, observed[0].Profile);
            Assert.False(observed[0].OpenAdministration);

            ServerWorkspaceEvent? replayed = null;
            using (ServerWorkspace.Subscribe(value => replayed = value, replaySelection: true))
            {
                Assert.NotNull(replayed);
                Assert.Same(profile, replayed.Profile);
                Assert.False(replayed.OpenAdministration);
            }

            ServerWorkspace.OpenAdministration(profile);
            Assert.Equal(2, observed.Count);
            Assert.True(observed[1].OpenAdministration);
            Assert.Equal("admin-secret", observed[1].Profile!.AdministrationPassword);
        }
        finally
        {
            ServerWorkspace.ClearSelection();
        }
    }

    [Fact]
    public void Missing_client_capability_fails_before_registration()
    {
        var api = new ClientUiApi(null);
        Assert.False(api.IsAvailable);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            api.RegisterPanel(Ui.Text("Unavailable")));
        Assert.Contains("only to client mods", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
    private sealed class SampleViewModel : BriefcaseViewModel
    {
        private bool _enabled;
        private string _status = "";

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
}
