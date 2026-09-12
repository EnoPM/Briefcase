using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Runtime.Versioning;
using Briefcase.ModApi;

namespace Briefcase.Rendering;

/// <summary>
/// Coordinates the Avalonia client UI and toolkit-neutral mod overlay callbacks.
/// It owns no graphics device; Avalonia/Skia is the sole visual renderer.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ManagedRenderingHost : IManagedRenderingService, IDisposable
{
    private readonly Action<string> _info;
    private readonly Action<string> _error;
    private readonly string _avaloniaAssemblyPath;
    private readonly object _avaloniaState;
    private readonly Func<AvaloniaWindowPlacement>? _loadAvaloniaPlacement;
    private readonly Action<AvaloniaWindowPlacement>? _saveAvaloniaPlacement;
    private readonly bool _enableGameWindowChrome;
    private readonly FrameworkStartupProgress? _startupProgress;
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
        string avaloniaAssemblyPath,
        object avaloniaState,
        Func<AvaloniaWindowPlacement>? loadAvaloniaPlacement = null,
        Action<AvaloniaWindowPlacement>? saveAvaloniaPlacement = null,
        bool enableGameWindowChrome = false,
        FrameworkStartupProgress? startupProgress = null)
    {
        _info = info;
        _error = error;
        _avaloniaAssemblyPath = avaloniaAssemblyPath;
        _avaloniaState = avaloniaState;
        _loadAvaloniaPlacement = loadAvaloniaPlacement;
        _saveAvaloniaPlacement = saveAvaloniaPlacement;
        _enableGameWindowChrome = enableGameWindowChrome;
        _startupProgress = startupProgress;
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
            Name = "Briefcase client UI coordinator"
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
            _info("Client UI: waiting for the Unreal window");
            var gameWindow = WaitForGameWindow();
            if (gameWindow == 0) return;

            using var gameWindowChrome = CreateGameWindowChrome(gameWindow);
            using var avalonia = new LazyRetainedMenuHost(
                () => CreateAvaloniaHost(gameWindow), _error);
            _info("Client UI: starting Avalonia after the Unreal startup gate");
            avalonia.Prepare();

            Volatile.Write(ref _ready, 1);
            _info("Client UI: ready; F1 opens Briefcase configuration");
            RunFrames(gameWindow, avalonia, gameWindowChrome);
        }
        catch (Exception exception)
        {
            _error($"Client UI failed: {exception}");
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
            Volatile.Write(ref _started, 0);
            if (ReferenceEquals(ManagedRenderingBridge.Current, this))
                ManagedRenderingBridge.Current = null;
            _info("Client UI: stopped");
        }
    }

    private nint WaitForGameWindow()
    {
        while (!_stop.IsCancellationRequested)
        {
            var found = FindGameWindow();
            if (Win32Native.IsWindow(found)) return found;
            Thread.Sleep(100);
        }
        return 0;
    }

    private static nint FindGameWindow()
    {
        nint found = 0;
        var processId = checked((uint)Environment.ProcessId);
        Win32Native.EnumWindows((window, _) =>
        {
            Win32Native.GetWindowThreadProcessId(window, out var owner);
            if (owner != processId || !Win32Native.IsWindowVisible(window)) return true;
            var name = new char[128];
            var length = Win32Native.GetClassNameW(window, name, name.Length);
            if (length <= 0 || new string(name, 0, length) != "UnrealWindow") return true;
            found = window;
            return false;
        }, 0);
        return found;
    }

    private GameWindowChrome? CreateGameWindowChrome(nint gameWindow)
    {
        if (!_enableGameWindowChrome) return null;
        try { return new GameWindowChrome(gameWindow, _info, _error); }
        catch (Exception exception)
        {
            _error($"Could not initialize the optional game window chrome: {exception.Message}");
            return null;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private IRetainedMenuHost CreateAvaloniaHost(nint gameWindow)
    {
        var assemblyPath = Path.GetFullPath(_avaloniaAssemblyPath);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException(
                "The Briefcase Avalonia UI module is not installed.", assemblyPath);

        var loadContext = new OptionalUiLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var factoryType = assembly.GetType(
            "Briefcase.AvaloniaUi.AvaloniaUiFactory", throwOnError: true)!;
        var factory = factoryType.GetMethod(
            "Create", BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(factoryType.FullName, "Create");
        var created = factory.Invoke(null,
        [
            gameWindow,
            _avaloniaState,
            (Action<bool>)(visible => MenuVisible = visible),
            _loadAvaloniaPlacement,
            _saveAvaloniaPlacement,
            _info,
            _error
        ]);
        return created as IRetainedMenuHost
               ?? throw new InvalidCastException(
                   "The Avalonia UI factory returned an incompatible host.");
    }

    private sealed class OptionalUiLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;
        private readonly string _thirdPartyDirectory;

        public OptionalUiLoadContext(string mainAssemblyPath)
            : base("Briefcase Avalonia UI", isCollectible: false)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
            _thirdPartyDirectory = Path.GetFullPath(Path.Combine(
                Path.GetDirectoryName(mainAssemblyPath)!, "..", "..", "ThirdPartyLibraries"));
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = FindLoadedAssembly(assemblyName);
            if (shared is not null) return shared;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            if (path is not null && File.Exists(path)) return LoadFromAssemblyPath(path);
            if (string.IsNullOrWhiteSpace(assemblyName.Name)) return null;
            path = Path.Combine(_thirdPartyDirectory, assemblyName.Name + ".dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }

        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path is not null && File.Exists(path))
                return LoadUnmanagedDllFromPath(path);
            if (string.IsNullOrWhiteSpace(unmanagedDllName) ||
                Path.GetFileName(unmanagedDllName) != unmanagedDllName)
                return 0;
            path = Path.Combine(_thirdPartyDirectory, unmanagedDllName);
            if (!Path.HasExtension(path)) path += ".dll";
            return File.Exists(path) ? LoadUnmanagedDllFromPath(path) : 0;
        }
    }

    internal static Assembly? FindLoadedAssembly(AssemblyName requested) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(candidate =>
            AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), requested));

    private void RunFrames(
        nint gameWindow,
        LazyRetainedMenuHost avalonia,
        GameWindowChrome? gameWindowChrome)
    {
        var previous = Stopwatch.GetTimestamp();
        var frameNumber = 0UL;
        var appliedVisibility = false;
        var f1WasDown = false;

        while (!_stop.IsCancellationRequested && Win32Native.IsWindow(gameWindow))
        {
            gameWindowChrome?.Maintain();
            var foreground = Win32Native.GetAncestor(
                Win32Native.GetForegroundWindow(), Win32Native.GaRoot);
            var focused = foreground == gameWindow || avalonia.IsForeground;
            var f1Down = IsKeyDown(Win32Native.VkF1);
            if (f1Down && !f1WasDown && focused &&
                _startupProgress?.Snapshot.IsComplete != false)
                MenuVisible = !MenuVisible;
            f1WasDown = f1Down;
            if (!focused) MenuVisible = false;

            var visible = MenuVisible;
            if (visible != appliedVisibility)
            {
                avalonia.SetVisible(visible);
                appliedVisibility = visible;
                _info(visible ? "Client UI: menu opened" : "Client UI: menu closed");
            }

            if (!Win32Native.GetClientRect(gameWindow, out var rectangle) ||
                rectangle.Width <= 0 || rectangle.Height <= 0 ||
                Win32Native.IsIconic(gameWindow))
            {
                avalonia.SubmitOverlay(OverlayFrameSnapshot.Empty);
                Thread.Sleep(25);
                continue;
            }

            var current = Stopwatch.GetTimestamp();
            var delta = (float)Math.Clamp(
                Stopwatch.GetElapsedTime(previous, current).TotalSeconds,
                1.0 / 1000.0,
                0.25);
            previous = current;
            var buffer = new OverlayCommandBuffer(1 / delta);
            var frame = new RenderFrame(
                checked((uint)rectangle.Width),
                checked((uint)rectangle.Height),
                delta,
                ++frameNumber,
                visible,
                new OverlayDrawingApi(buffer));
            foreach (var callback in SnapshotCallbacks())
                if (visible || callback.RenderWhenMenuHidden)
                    callback.Invoke(frame, _error);
            avalonia.SubmitOverlay(buffer.Snapshot(frame.Width, frame.Height));
            Thread.Sleep(8);
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
                catch (Exception exception)
                {
                    error($"Managed overlay callback failed: {exception}");
                }
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
