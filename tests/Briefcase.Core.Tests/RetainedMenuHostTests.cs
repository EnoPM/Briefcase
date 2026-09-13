using System.Runtime.Versioning;
using Briefcase.Rendering;

namespace Briefcase.Core.Tests;

public sealed class RetainedMenuHostTests
{
    [Fact]
    public void Optional_host_is_not_created_until_the_menu_is_shown()
    {
        var creations = 0;
        var starts = 0;
        using var lazy = new LazyRetainedMenuHost(
            () =>
            {
                creations++;
                return new FakeRetainedMenuHost(() => starts++);
            },
            _ => { });

        Assert.True(lazy.UseRetained);
        lazy.SetVisible(false);
        Assert.Equal(0, creations);

        lazy.Prepare();
        Assert.Equal(1, creations);
        Assert.Equal(1, starts);

        lazy.SetVisible(true);
        Assert.Equal(1, creations);
        lazy.SetVisible(false);
        lazy.SetVisible(true);
        Assert.Equal(1, creations);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Optional_ui_resolves_the_loaded_rendering_contract_across_load_contexts()
    {
        var rendering = typeof(ManagedRenderingHost).Assembly;

        var resolved = ManagedRenderingHost.FindLoadedAssembly(rendering.GetName());

        Assert.Same(rendering, resolved);
    }

    [Fact]
    public void Suspension_requested_before_creation_is_applied_when_host_is_prepared()
    {
        bool? applied = null;
        using var lazy = new LazyRetainedMenuHost(
            () => new FakeRetainedMenuHost(() => { }, value => applied = value),
            _ => { });

        lazy.SetSuspended(true);
        Assert.Null(applied);

        lazy.Prepare();
        Assert.True(applied is true);

        lazy.SetSuspended(false);
        Assert.True(applied is false);
    }
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Hotkey_requires_a_stable_release_before_another_press()
    {
        var hotkey = new ManagedRenderingHost.StableReleaseHotkey(100);

        Assert.True(hotkey.Update(true, 0));
        Assert.False(hotkey.Update(true, 10));
        Assert.False(hotkey.Update(false, 20));
        Assert.False(hotkey.Update(false, 119));
        Assert.False(hotkey.Update(false, 120));
        Assert.True(hotkey.Update(true, 121));
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void Hotkey_ignores_a_transient_release_during_focus_transfer()
    {
        var hotkey = new ManagedRenderingHost.StableReleaseHotkey(100);

        Assert.True(hotkey.Update(true, 0));
        Assert.False(hotkey.Update(false, 20));
        Assert.False(hotkey.Update(true, 30));
        Assert.False(hotkey.Update(false, 40));
        Assert.False(hotkey.Update(false, 139));
        Assert.False(hotkey.Update(false, 140));
        Assert.True(hotkey.Update(true, 141));
    }
    [Fact]
    public void Offscreen_saved_position_is_brought_fully_inside_the_game()
    {
        var fitted = AvaloniaPlacementConstraints.Fit(
            new AvaloniaWindowPlacement(-500, 2_000, 900, 650),
            1_920,
            1_080,
            900,
            650,
            620,
            420,
            1);

        Assert.Equal(0, fitted.X);
        Assert.Equal(430, fitted.Y);
        Assert.Equal(900, fitted.Width);
        Assert.Equal(650, fitted.Height);
    }

    [Fact]
    public void Initial_size_accounts_for_monitor_scaling()
    {
        var fitted = AvaloniaPlacementConstraints.Fit(
            null,
            2_560,
            1_440,
            900,
            650,
            620,
            420,
            1.5);

        Assert.Equal(1_350, fitted.Width);
        Assert.Equal(975, fitted.Height);
        Assert.Equal(605, fitted.X);
        Assert.Equal(232.5, fitted.Y);
    }


    [Fact]
    public void Relative_menu_size_preserves_configured_edge_margins()
    {
        var size = AvaloniaMenuLayout.Calculate(2_000, 1_000, 12.5, 8);

        Assert.Equal(1_500, size.Width);
        Assert.Equal(840, size.Height);
    }

    private sealed class FakeRetainedMenuHost(
        Action started,
        Action<bool>? suspensionChanged = null) : IRetainedMenuHost
    {
        public bool HasFailed => false;
        public bool IsForeground => false;
        public void Start() => started();
        public void SetVisible(bool visible) { }
        public void SetSuspended(bool suspended) => suspensionChanged?.Invoke(suspended);
        public void SubmitOverlay(Briefcase.ModApi.OverlayFrameSnapshot frame) { }
        public void Dispose() { }
    }
}
