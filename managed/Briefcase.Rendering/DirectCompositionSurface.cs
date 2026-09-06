using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;

namespace Briefcase.Rendering;

/// <summary>
/// Owns the transparent DirectComposition swap chain used by the overlay.
/// All methods are called by the single rendering thread.
/// </summary>
internal sealed class DirectCompositionSurface : IDisposable
{
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDXGISwapChain1? _swapChain;
    private ID3D11RenderTargetView? _renderTarget;
    private IDCompositionDevice? _composition;
    private IDCompositionTarget? _compositionTarget;
    private IDCompositionVisual? _visual;
    private uint _width;
    private uint _height;

    public ID3D11Device Device => _device ?? throw new ObjectDisposedException(nameof(DirectCompositionSurface));
    public ID3D11DeviceContext Context => _context ?? throw new ObjectDisposedException(nameof(DirectCompositionSurface));

    public void Initialize(nint window, uint width, uint height)
    {
        var levels = new[] { FeatureLevel.Level_11_0, FeatureLevel.Level_10_0 };
        var result = D3D11CreateDevice(
            null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
            levels, out _device, out _, out _context);
        if (result.Failure)
            result = D3D11CreateDevice(
                null, DriverType.Warp, DeviceCreationFlags.BgraSupport,
                levels, out _device, out _, out _context);
        result.CheckError();

        using var dxgiDevice = Device.QueryInterface<IDXGIDevice>();
        DXGI.CreateDXGIFactory2(false, out IDXGIFactory2? factory).CheckError();
        if (factory is null)
            throw new InvalidOperationException("DXGI did not return a factory.");
        using (factory)
        {
            var description = new SwapChainDescription1
            {
                Width = width,
                Height = height,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                BufferUsage = Vortice.DXGI.Usage.RenderTargetOutput,
                BufferCount = 2,
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipSequential,
                AlphaMode = AlphaMode.Premultiplied
            };
            _swapChain = factory.CreateSwapChainForComposition(Device, description, null);
        }

        _composition = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _composition.CreateTargetForHwnd(window, true, out _compositionTarget).CheckError();
        _composition.CreateVisual(out _visual).CheckError();
        _visual.SetContent(_swapChain).CheckError();
        _compositionTarget.SetRoot(_visual).CheckError();
        _composition.Commit().CheckError();

        _width = width;
        _height = height;
        CreateRenderTarget();
    }

    public void Resize(uint width, uint height)
    {
        if (width == _width && height == _height) return;
        Context.OMSetRenderTargets(Array.Empty<ID3D11RenderTargetView>());
        _renderTarget?.Dispose();
        _renderTarget = null;
        _swapChain!.ResizeBuffers(0, width, height, Format.Unknown, SwapChainFlags.None).CheckError();
        _width = width;
        _height = height;
        CreateRenderTarget();
    }

    public void BeginFrame()
    {
        var target = _renderTarget ?? throw new InvalidOperationException("The render target is unavailable.");
        Context.OMSetRenderTargets(target);
        Context.ClearRenderTargetView(target, new Color4(0, 0, 0, 0));
    }

    public void Present() => _swapChain!.Present(1, PresentFlags.None).CheckError();

    public void Dispose()
    {
        _renderTarget?.Dispose();
        _visual?.Dispose();
        _compositionTarget?.Dispose();
        _composition?.Dispose();
        _swapChain?.Dispose();
        _context?.Dispose();
        _device?.Dispose();
        _renderTarget = null;
        _visual = null;
        _compositionTarget = null;
        _composition = null;
        _swapChain = null;
        _context = null;
        _device = null;
    }

    private void CreateRenderTarget()
    {
        using var buffer = _swapChain!.GetBuffer<ID3D11Texture2D>(0);
        _renderTarget = Device.CreateRenderTargetView(buffer);
    }
}
