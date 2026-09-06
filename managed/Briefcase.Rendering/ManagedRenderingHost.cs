using System.Diagnostics;
using Briefcase.ModApi;
using ImGuiNET;

namespace Briefcase.Rendering;

/// <summary>
/// Runs the complete Briefcase overlay on one managed background thread. That
/// thread owns the Win32 window, ImGui context, Direct3D device, and every render
/// callback, which avoids cross-thread ImGui and D3D access.
/// </summary>
public sealed class ManagedRenderingHost : IManagedRenderingService, IDisposable
{
    private readonly Action<string> _info;
    private readonly Action<string> _error;
    private readonly Func<RenderFrame, bool> _drawFrameworkMenu;
    private readonly object _callbacksGate = new();
    private readonly List<Registration> _callbacks = [];
    private readonly CancellationTokenSource _stop = new();
    private Thread? _thread;
    private int _menuVisible;
    private int _started;
    private int _ready;

    public ManagedRenderingHost(
        Action<string> info,
        Action<string> error,
        Func<RenderFrame, bool> drawFrameworkMenu)
    {
        _info = info;
        _error = error;
        _drawFrameworkMenu = drawFrameworkMenu;
    }

    public bool IsAvailable => Volatile.Read(ref _started) != 0;
    public bool IsReady => Volatile.Read(ref _ready) != 0;

    public bool MenuVisible
    {
        get => Volatile.Read(ref _menuVisible) != 0;
        set => Volatile.Write(ref _menuVisible, value ? 1 : 0);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        ManagedRenderingBridge.Current = this;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Briefcase managed renderer"
        };
        _thread.Start();
    }

    public IRenderRegistration Register(Action<RenderFrame> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        if (!IsAvailable)
            throw new InvalidOperationException("The managed rendering host has not started.");
        var registration = new Registration(this, draw);
        lock (_callbacksGate) _callbacks.Add(registration);
        return registration;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _started, 0) == 0) return;
        if (ReferenceEquals(ManagedRenderingBridge.Current, this))
            ManagedRenderingBridge.Current = null;
        _stop.Cancel();
        if (_thread is { IsAlive: true } && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
        _stop.Dispose();
    }

    private void Run()
    {
        try
        {
            _info("Managed rendering host: waiting for the Unreal window");
            nint gameWindow = 0;
            while (!_stop.IsCancellationRequested)
            {
                gameWindow = OverlayWindow.FindGameWindow();
                if (Win32Native.IsWindow(gameWindow)) break;
                Thread.Sleep(100);
            }
            if (_stop.IsCancellationRequested) return;

            using var window = new OverlayWindow(gameWindow);
            using var imgui = new ImGuiRuntime();
            imgui.Initialize();
            var input = new ImGuiInputBackend(window);
            using var surface = new DirectCompositionSurface();
            surface.Initialize(window.Handle, 800, 500);
            using var renderer = new D3D11ImGuiRenderer(surface.Device, surface.Context);

            Volatile.Write(ref _ready, 1);
            _info("Managed rendering host: ready; F1 opens Briefcase configuration");
            RunFrames(window, input, imgui, surface, renderer);
        }
        catch (Exception exception)
        {
            _error($"Managed rendering host failed: {exception}");
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
            Volatile.Write(ref _started, 0);
            if (ReferenceEquals(ManagedRenderingBridge.Current, this))
                ManagedRenderingBridge.Current = null;
            _info("Managed rendering host: stopped");
        }
    }

    private void RunFrames(
        OverlayWindow window,
        ImGuiInputBackend input,
        ImGuiRuntime imgui,
        DirectCompositionSurface surface,
        D3D11ImGuiRenderer renderer)
    {
        var previous = Stopwatch.GetTimestamp();
        var frameNumber = 0UL;
        var appliedVisibility = false;
        var f1WasDown = false;

        while (!_stop.IsCancellationRequested && Win32Native.IsWindow(window.GameWindow))
        {
            window.PumpMessages();
            var focused = window.IsGameOrOverlayForeground();
            var f1Down = IsKeyDown(Win32Native.VkF1);
            if (f1Down && !f1WasDown && focused) MenuVisible = !MenuVisible;
            f1WasDown = f1Down;
            if ((Win32Native.GetAsyncKeyState(Win32Native.VkEscape) & 1) != 0 && window.Interactive)
                MenuVisible = false;
            if (!focused) MenuVisible = false;

            var visible = MenuVisible;
            if (visible != appliedVisibility)
            {
                window.SetInteractive(visible, !visible);
                ImGui.GetIO().MouseDrawCursor = visible;
                if (!visible) input.Clear();
                appliedVisibility = visible;
                _info(visible
                    ? "Managed rendering host: menu opened"
                    : "Managed rendering host: menu closed");
            }

            var callbacks = SnapshotCallbacks();
            var renderInBackground = callbacks.Any(item => item.RenderWhenMenuHidden);
            if ((!visible && !renderInBackground) || window.GameIsMinimized)
            {
                window.Hide();
                Thread.Sleep(10);
                continue;
            }
            if (!window.AlignToGame(out var width, out var height))
            {
                Thread.Sleep(10);
                continue;
            }

            surface.Resize(width, height);
            var current = Stopwatch.GetTimestamp();
            var delta = (float)Stopwatch.GetElapsedTime(previous, current).TotalSeconds;
            previous = current;
            input.Update();
            imgui.BeginFrame(new System.Numerics.Vector2(width, height), delta);

            var frame = new RenderFrame(
                width, height, delta, ++frameNumber, visible, ImGuiApi.Managed);
            foreach (var callback in callbacks)
                callback.Invoke(frame, _error);
            if (visible)
            {
                try
                {
                    if (!_drawFrameworkMenu(frame)) MenuVisible = false;
                }
                catch (Exception exception)
                {
                    _error($"Briefcase configuration menu failed: {exception}");
                }
            }

            var drawData = imgui.EndFrame();
            surface.BeginFrame();
            renderer.Render(drawData);
            surface.Present();
        }
    }

    private Registration[] SnapshotCallbacks()
    {
        lock (_callbacksGate) return [.. _callbacks.Where(item => item.IsActive)];
    }

    private void Remove(Registration registration)
    {
        lock (_callbacksGate) _callbacks.Remove(registration);
    }

    private static bool IsKeyDown(int key) =>
        (Win32Native.GetAsyncKeyState(key) & 0x8000) != 0;

    private sealed class Registration : IRenderRegistration
    {
        private readonly ManagedRenderingHost _owner;
        private readonly Action<RenderFrame> _draw;
        private readonly object _invocationGate = new();
        private int _active = 1;
        private int _renderWhenMenuHidden;

        public Registration(ManagedRenderingHost owner, Action<RenderFrame> draw)
        {
            _owner = owner;
            _draw = draw;
        }

        public bool IsActive => Volatile.Read(ref _active) != 0;

        public bool RenderWhenMenuHidden
        {
            get => Volatile.Read(ref _renderWhenMenuHidden) != 0;
            set => Volatile.Write(ref _renderWhenMenuHidden, value ? 1 : 0);
        }

        public void Invoke(RenderFrame frame, Action<string> error)
        {
            lock (_invocationGate)
            {
                if (!IsActive) return;
                try { _draw(frame); }
                catch (Exception exception) { error($"Managed render callback failed: {exception}"); }
            }
        }

        public void Dispose()
        {
            lock (_invocationGate)
            {
                if (Interlocked.Exchange(ref _active, 0) == 0) return;
            }
            _owner.Remove(this);
        }
    }
}
