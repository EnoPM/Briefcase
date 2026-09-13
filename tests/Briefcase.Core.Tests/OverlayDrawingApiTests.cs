using Briefcase.ModApi;

namespace Briefcase.Core.Tests;

public sealed class OverlayDrawingApiTests
{
    [Fact]
    public void Commands_are_captured_in_submission_order()
    {
        var buffer = new OverlayCommandBuffer(120);
        var overlay = new OverlayDrawingApi(buffer);

        overlay.DrawLine(1, 2, 3, 4, OverlayColor.Rgba(10, 20, 30), 2);
        overlay.DrawCircle(5, 6, 7, OverlayColor.Rgba(40, 50, 60), segments: 24, filled: true);
        overlay.DrawRectFilled(8, 9, 10, 11, OverlayColor.Rgba(70, 80, 90), 3);
        overlay.DrawText(12, 13, OverlayColor.Rgba(100, 110, 120), "BOT 7m");

        var snapshot = buffer.Snapshot(1920, 1080);

        Assert.Equal(1920u, snapshot.Width);
        Assert.Equal(1080u, snapshot.Height);
        Assert.Equal(4, snapshot.Commands.Length);
        Assert.Equal(OverlayCommandKind.Line, snapshot.Commands[0].Kind);
        Assert.Equal(OverlayCommandKind.Circle, snapshot.Commands[1].Kind);
        Assert.True(snapshot.Commands[1].Filled);
        Assert.Equal(24, snapshot.Commands[1].Segments);
        Assert.Equal(OverlayCommandKind.FilledRectangle, snapshot.Commands[2].Kind);
        Assert.Equal(OverlayCommandKind.Text, snapshot.Commands[3].Kind);
        Assert.Equal("BOT 7m", snapshot.Commands[3].Text);
        Assert.Equal(120, overlay.Framerate);
    }

    [Fact]
    public void Command_buffer_enforces_its_explicit_frame_limit()
    {
        var buffer = new OverlayCommandBuffer(60);
        var overlay = new OverlayDrawingApi(buffer);

        for (var index = 0; index < 5_000; index++)
            overlay.DrawLine(index, 0, index, 1, 0xffffffff);

        Assert.Equal(4096, buffer.Snapshot(1, 1).Commands.Length);
    }

    [Fact]
    public void Rgba_uses_the_stable_red_green_blue_alpha_byte_order()
    {
        Assert.Equal(0x44332211u, OverlayColor.Rgba(0x11, 0x22, 0x33, 0x44));
    }
}