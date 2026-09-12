using System.Text.Json;
using Briefcase.ManagedHost;
using Briefcase.ModApi;

namespace Briefcase.Core.Tests;

public sealed class ConfigurationRegistryTests
{
    [Fact]
    public void Bind_clamps_values_notifies_once_and_persists_them()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        var messages = new List<string>();

        using (var registry = CreateRegistry(settingsPath, messages))
        using (var scope = registry.RegisterMod(CreateModInfo()))
        {
            var entry = scope.Bind(
                " Gameplay ", " Maximum Players ", 20, "Player limit",
                new ConfigurationRange<int>(1, 12), secret: false);
            Assert.Equal(12, entry.Value);
            Assert.Equal("Gameplay", entry.Section);
            Assert.Equal("Maximum Players", entry.Key);

            var changes = new List<int>();
            entry.ValueChanged += changes.Add;
            entry.Value = 12;
            entry.Value = -5;
            entry.Value = -10;

            Assert.Equal(1, entry.Value);
            Assert.Equal([1], changes);
            Assert.Same(entry, scope.Bind(
                "Gameplay", "Maximum Players", 7, "ignored",
                new ConfigurationRange<int>(1, 12), secret: false));
            registry.SetModEnabled("Sample.dll", false);
        }

        using (var registry = CreateRegistry(settingsPath, messages))
        using (var scope = registry.RegisterMod(CreateModInfo()))
        {
            var restored = scope.Bind(
                "Gameplay", "Maximum Players", 7, "Player limit",
                new ConfigurationRange<int>(1, 12), secret: false);
            Assert.Equal(1, restored.Value);
            Assert.False(registry.IsModEnabled("sample.DLL"));
        }

        Assert.Empty(messages);
    }

    [Fact]
    public void Invalid_persisted_value_falls_back_and_reports_a_warning()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, """
            {
              "mods": {
                "sample.mod": {
                  "values": { "General/Count": "not-an-integer" }
                }
              },
              "enabledMods": {}
            }
            """);
        var warnings = new List<string>();

        using var registry = new ConfigurationRegistry(
            settingsPath, _ => { }, warnings.Add, _ => { });
        using var scope = registry.RegisterMod(CreateModInfo());
        var entry = scope.Bind("General", "Count", 4, "", null, false);

        Assert.Equal(4, entry.Value);
        Assert.Single(warnings);
        Assert.Contains("sample.mod/General/Count", warnings[0]);
    }

    [Fact]
    public void Bind_rejects_ambiguous_or_unsupported_declarations()
    {
        using var directory = new TemporaryDirectory();
        using var registry = CreateRegistry(
            Path.Combine(directory.Path, "settings.json"), []);
        using var scope = registry.RegisterMod(CreateModInfo());

        scope.Bind("General", "Count", 1, "", null, false);
        Assert.Throws<InvalidOperationException>(() =>
            scope.Bind("General", "Count", 1f, "", null, false));
        Assert.Throws<ArgumentException>(() =>
            scope.Bind("General", "Secret", 1, "", null, true));
        Assert.Throws<ArgumentException>(() =>
            scope.Bind("General", "Flag", true, "",
                new ConfigurationRange<bool>(false, true), false));
        Assert.Throws<ArgumentException>(() =>
            scope.Bind("General", "Bad Range", 1, "",
                new ConfigurationRange<int>(10, 2), false));
        Assert.Throws<NotSupportedException>(() =>
            scope.Bind("General", "Object", new object(), "", null, false));
    }

    [Fact]
    public void Corrupt_document_is_replaced_by_defaults_without_crashing()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");
        File.WriteAllText(settingsPath, "{ invalid json");
        var warnings = new List<string>();

        using (var registry = new ConfigurationRegistry(
                   settingsPath, _ => { }, warnings.Add, _ => { }))
        using (var scope = registry.RegisterMod(CreateModInfo()))
            Assert.True(scope.Bind("General", "Enabled", true, "", null, false).Value);

        Assert.Single(warnings);
        using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
        Assert.True(document.RootElement.TryGetProperty("Mods", out _));
    }

    [Fact]
    public void Avalonia_window_placement_uses_the_existing_settings_document()
    {
        using var directory = new TemporaryDirectory();
        var settingsPath = Path.Combine(directory.Path, "settings.json");

        using (var registry = CreateRegistry(settingsPath, []))
            registry.RememberAvaloniaPlacement(
                new Briefcase.Rendering.AvaloniaWindowPlacement(120, 75, 910, 640));

        using var restored = CreateRegistry(settingsPath, []);
        var placement = restored.GetAvaloniaPlacement();
        Assert.Equal(120, placement.X);
        Assert.Equal(75, placement.Y);
        Assert.Equal(910, placement.Width);
        Assert.Equal(640, placement.Height);
    }

    [Fact]
    public void Configuration_draft_applies_or_cancels_one_page_without_immediate_changes()
    {
        using var directory = new TemporaryDirectory();
        using var registry = CreateRegistry(
            Path.Combine(directory.Path, "settings.json"), []);
        using var scope = registry.RegisterMod(CreateModInfo());
        var count = scope.Bind("General", "Count", 2, "", null, false);
        var enabled = scope.Bind("General", "Enabled", true, "", null, false);
        var draft = registry.GetConfigurationDraft(scope);

        draft.SetValue(count, 7);
        draft.SetValue(enabled, false);

        Assert.True(draft.IsDirty);
        Assert.True(registry.HasConfigurationDraft(scope.Info.Id));
        Assert.Equal(2, count.Value);
        Assert.True(enabled.Value);
        Assert.Same(draft, registry.GetConfigurationDraft(scope));

        draft.Cancel();
        Assert.False(draft.IsDirty);
        Assert.Equal(2, draft.GetValue(count));
        Assert.True((bool)draft.GetValue(enabled));

        draft.SetValue(count, 9);
        draft.Apply();
        Assert.False(draft.IsDirty);
        Assert.Equal(9, count.Value);
    }

    [Fact]
    public void Unloading_a_mod_discards_its_pending_configuration_draft()
    {
        using var directory = new TemporaryDirectory();
        using var registry = CreateRegistry(
            Path.Combine(directory.Path, "settings.json"), []);
        var scope = registry.RegisterMod(CreateModInfo());
        var count = scope.Bind("General", "Count", 2, "", null, false);
        registry.GetConfigurationDraft(scope).SetValue(count, 7);

        scope.Dispose();

        Assert.False(registry.HasConfigurationDraft(scope.Info.Id));
    }
    private static ConfigurationRegistry CreateRegistry(
        string path, List<string> messages) =>
        new(path, _ => { }, messages.Add, messages.Add);

    private static ModInfo CreateModInfo() =>
        new("sample.mod", "Sample", "Briefcase", "1.0.0", "Test mod");
}
