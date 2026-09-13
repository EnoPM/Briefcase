using System.Buffers;
using System.Text;
using Briefcase.ModApi;
using Briefcase.ModApi.Interop;

namespace Briefcase.Rendering;

/// <summary>
/// Serializes toolkit-neutral managed draw commands into the versioned native
/// ABI. SubmitFrame copies the buffers synchronously, so pooled arrays can be
/// returned as soon as the call completes.
/// </summary>
internal sealed unsafe class NativeOverlayBridge
{
    private readonly NativeRenderingApi* _api;
    private readonly Action<string> _info;
    private readonly Action<string> _error;
    private NativeRenderingStatus _lastStatus = (NativeRenderingStatus)uint.MaxValue;
    private bool _submissionErrorReported;

    public NativeOverlayBridge(
        NativeRenderingApi* api,
        Action<string> info,
        Action<string> error)
    {
        _api = api;
        _info = info;
        _error = error;
    }

    public bool IsSupported =>
        _api is not null &&
        _api->StructSize >= sizeof(NativeRenderingApi) &&
        _api->ApiVersion == BriefcaseAbi.RenderingApiVersion &&
        _api->Start is not null &&
        _api->SubmitFrame is not null &&
        _api->GetStatus is not null;

    public void Start()
    {
        if (!IsSupported)
        {
            _error("Native overlay renderer is unavailable; passive mod drawings are disabled.");
            return;
        }

        if (_api->Start(_api->Context) == 0)
            _error("Native overlay renderer rejected startup.");
        ReportStatus();
    }

    public void Submit(OverlayFrameSnapshot snapshot, ulong frameNumber)
    {
        if (!IsSupported) return;

        var source = snapshot.Commands;
        var commands = ArrayPool<NativeOverlayCommand>.Shared.Rent(Math.Max(1, source.Length));
        var text = ArrayPool<byte>.Shared.Rent(
            Math.Max(1, Math.Min(EstimateTextBytes(source),
                checked((int)BriefcaseAbi.OverlayMaximumTextBytes))));
        try
        {
            var commandCount = 0;
            var textBytes = 0;
            foreach (var command in source)
            {
                if (commandCount >= BriefcaseAbi.OverlayMaximumCommands) break;
                var native = new NativeOverlayCommand
                {
                    StructSize = checked((uint)sizeof(NativeOverlayCommand)),
                    Kind = (NativeOverlayCommandKind)command.Kind,
                    X1 = command.X1,
                    Y1 = command.Y1,
                    X2 = command.X2,
                    Y2 = command.Y2,
                    Radius = command.Radius,
                    Thickness = command.Thickness,
                    Rounding = command.Rounding,
                    Color = command.Color,
                    Flags = command.Filled
                        ? NativeOverlayCommandFlags.Filled
                        : NativeOverlayCommandFlags.None,
                    Segments = command.Segments
                };

                if (command.Kind == OverlayCommandKind.Text)
                {
                    if (command.Text is null) continue;
                    var required = Encoding.UTF8.GetByteCount(command.Text);
                    if (required == 0 || required > text.Length - textBytes) continue;
                    native.TextOffset = checked((uint)textBytes);
                    native.TextLength = checked((uint)required);
                    textBytes += Encoding.UTF8.GetBytes(
                        command.Text.AsSpan(), text.AsSpan(textBytes, required));
                }

                commands[commandCount++] = native;
            }

            fixed (NativeOverlayCommand* commandPointer = commands)
            fixed (byte* textPointer = text)
            {
                var frame = new NativeOverlayFrame
                {
                    StructSize = checked((uint)sizeof(NativeOverlayFrame)),
                    Version = BriefcaseAbi.RenderingApiVersion,
                    Width = snapshot.Width,
                    Height = snapshot.Height,
                    CommandCount = checked((uint)commandCount),
                    TextBytes = checked((uint)textBytes),
                    Commands = commandCount == 0 ? null : commandPointer,
                    Utf8Text = textBytes == 0 ? null : textPointer,
                    FrameNumber = frameNumber
                };
                var result = _api->SubmitFrame(_api->Context, &frame);
                if (result != NativeRenderingResult.Ok && !_submissionErrorReported)
                {
                    _submissionErrorReported = true;
                    _error($"Native overlay frame was rejected: {result}.");
                }
            }
            ReportStatus();
        }
        finally
        {
            ArrayPool<NativeOverlayCommand>.Shared.Return(commands, clearArray: true);
            ArrayPool<byte>.Shared.Return(text);
        }
    }

    public void Clear() => Submit(OverlayFrameSnapshot.Empty, 0);

    private void ReportStatus()
    {
        if (!IsSupported) return;
        uint nativeError = 0;
        var status = _api->GetStatus(_api->Context, &nativeError);
        if (status == _lastStatus) return;
        _lastStatus = status;
        switch (status)
        {
            case NativeRenderingStatus.Starting:
                _info("Native overlay renderer: installing the D3D11 Present hook");
                break;
            case NativeRenderingStatus.Ready:
                _info("Native overlay renderer: D3D11 Present hook ready");
                break;
            case NativeRenderingStatus.Failed:
                _error($"Native overlay renderer failed (native error 0x{nativeError:X8}).");
                break;
        }
    }

    private static int EstimateTextBytes(IEnumerable<OverlayCommand> commands)
    {
        long total = 0;
        foreach (var command in commands)
        {
            if (command.Kind != OverlayCommandKind.Text || command.Text is null) continue;
            total += Encoding.UTF8.GetByteCount(command.Text);
            if (total >= BriefcaseAbi.OverlayMaximumTextBytes)
                return checked((int)BriefcaseAbi.OverlayMaximumTextBytes);
        }
        return checked((int)Math.Max(1, total));
    }
}
