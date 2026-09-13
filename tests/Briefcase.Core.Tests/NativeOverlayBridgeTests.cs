using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;
using Briefcase.Rendering;

namespace Briefcase.Core.Tests;

public sealed unsafe class NativeOverlayBridgeTests
{
    private static int _startCalls;
    private static NativeOverlayFrame _frame;
    private static NativeOverlayCommand[] _commands = [];
    private static string _text = string.Empty;

    [Fact]
    public void Managed_commands_cross_the_bounded_native_abi()
    {
        _startCalls = 0;
        _frame = default;
        _commands = [];
        _text = string.Empty;
        var api = new NativeRenderingApi
        {
            StructSize = checked((uint)sizeof(NativeRenderingApi)),
            ApiVersion = BriefcaseAbi.RenderingApiVersion,
            Start = &Start,
            SubmitFrame = &Submit,
            GetStatus = &GetStatus
        };
        var messages = new List<string>();
        var bridge = new NativeOverlayBridge(&api, messages.Add, messages.Add);
        var buffer = new OverlayCommandBuffer(120);
        var drawing = new OverlayDrawingApi(buffer);
        drawing.DrawCircle(10, 20, 30, 0x44332211, segments: 32, thickness: 2);
        drawing.DrawText(40, 50, 0x88776655, "ESP é");

        bridge.Start();
        bridge.Submit(buffer.Snapshot(2560, 1440), 42);

        Assert.Equal(1, _startCalls);
        Assert.Equal(2560u, _frame.Width);
        Assert.Equal(1440u, _frame.Height);
        Assert.Equal(42ul, _frame.FrameNumber);
        Assert.Equal(2, _commands.Length);
        Assert.Equal(NativeOverlayCommandKind.Circle, _commands[0].Kind);
        Assert.Equal(32, _commands[0].Segments);
        Assert.Equal(NativeOverlayCommandKind.Text, _commands[1].Kind);
        Assert.Equal("ESP é", _text);
        Assert.Contains(messages, item => item.Contains("Present hook ready", StringComparison.Ordinal));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static uint Start(void* context)
    {
        _ = context;
        Interlocked.Increment(ref _startCalls);
        return 1;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeRenderingResult Submit(void* context, NativeOverlayFrame* frame)
    {
        _ = context;
        _frame = *frame;
        _frame.Commands = null;
        _frame.Utf8Text = null;
        _commands = new NativeOverlayCommand[frame->CommandCount];
        for (var index = 0; index < _commands.Length; index++)
            _commands[index] = frame->Commands[index];
        if (_commands.Length > 1)
        {
            var command = _commands[1];
            _text = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(
                frame->Utf8Text + command.TextOffset,
                checked((int)command.TextLength)));
        }
        return NativeRenderingResult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static NativeRenderingStatus GetStatus(void* context, uint* nativeError)
    {
        _ = context;
        if (nativeError is not null) *nativeError = 0;
        return NativeRenderingStatus.Ready;
    }
}
