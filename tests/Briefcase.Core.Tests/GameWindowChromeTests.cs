using System.Runtime.Versioning;
using Briefcase.Rendering;

namespace Briefcase.Core.Tests;

[SupportedOSPlatform("windows")]
public sealed class GameWindowChromeTests
{
    [Fact]
    public void System_chrome_replaces_popup_and_preserves_unrelated_style_bits()
    {
        const uint visible = 0x10000000;
        var result = GameWindowChrome.AddSystemChrome(Win32Native.WsPopup | visible);

        Assert.Equal(0u, result & Win32Native.WsPopup);
        Assert.NotEqual(0u, result & Win32Native.WsCaption);
        Assert.NotEqual(0u, result & Win32Native.WsThickFrame);
        Assert.NotEqual(0u, result & Win32Native.WsSystemMenu);
        Assert.NotEqual(0u, result & Win32Native.WsMinimizeBox);
        Assert.NotEqual(0u, result & Win32Native.WsMaximizeBox);
        Assert.NotEqual(0u, result & visible);
    }

    [Theory]
    [InlineData(Win32Native.WmNcHitTest)]
    [InlineData(Win32Native.WmNcPaint)]
    [InlineData(Win32Native.WmNcActivate)]
    [InlineData(Win32Native.WmNcMouseMove)]
    [InlineData(Win32Native.WmNcXButtonDoubleClick)]
    public void Unreal_non_client_messages_use_the_default_window_procedure(uint message)
    {
        Assert.True(GameWindowChrome.UsesDefaultNonClientProcedure(message, 0));
    }

    [Fact]
    public void Client_rectangle_calculation_is_customized()
    {
        Assert.False(GameWindowChrome.UsesDefaultNonClientProcedure(
            Win32Native.WmNcCalcSize, 0));
    }

    [Theory]
    [InlineData(101, 101, 13)]
    [InlineData(150, 101, 12)]
    [InlineData(199, 150, 11)]
    [InlineData(101, 199, 16)]
    [InlineData(150, 150, 0)]
    public void Invisible_resize_border_returns_the_expected_hit_test(
        int x, int y, int expected)
    {
        var window = new Win32Native.Rect
        {
            Left = 100,
            Top = 100,
            Right = 200,
            Bottom = 200
        };

        Assert.Equal((nint)expected,
            GameWindowChrome.HitTestResizeBorder(window, 8, x, y));
    }

    [Fact]
    public void Client_mouse_messages_still_use_unreals_window_procedure()
    {
        Assert.False(GameWindowChrome.UsesDefaultNonClientProcedure(
            Win32Native.WmMouseMove, 0));
        Assert.False(GameWindowChrome.UsesDefaultNonClientProcedure(
            Win32Native.WmSetCursor, Win32Native.HtClient));
    }
}
