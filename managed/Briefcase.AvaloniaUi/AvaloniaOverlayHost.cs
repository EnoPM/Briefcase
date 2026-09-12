using System.Diagnostics;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Skia;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Win32;
using Briefcase.Rendering;
using Briefcase.ModApi;

namespace Briefcase.AvaloniaUi;

/// <summary>
/// Owns Briefcase's client overlay on a dedicated STA thread. The lightweight
/// startup view and the configuration menu have separate visual lifetimes.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class AvaloniaOverlayHost : IRetainedMenuHost
{
    private const double MenuWidth = 900;
    private const double MenuHeight = 650;
    private const double HiddenMenuScale = 0.965;
    private static readonly TimeSpan OpenTransitionDuration = TimeSpan.FromMilliseconds(180);
    private static readonly TimeSpan CloseTransitionDuration = TimeSpan.FromMilliseconds(140);
    private static AvaloniaOverlayHost? s_initializing;

    private readonly nint _gameWindow;
    private readonly Func<object> _createMenuContent;
    private readonly Action<bool> _visibilityChanged;
    private readonly Action<string> _info;
    private readonly Action<string> _error;
    private readonly FrameworkStartupProgress? _startupProgress;
    private readonly bool _recreateMenuOnClose;
    private readonly double _horizontalMarginPercent;
    private readonly double _verticalMarginPercent;
    private readonly object _overlayGate = new();
    private OverlayFrameSnapshot _pendingOverlay = OverlayFrameSnapshot.Empty;
    private Thread? _thread;
    private Window? _window;
    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private nint _windowHandle;
    private int _started;
    private int _ready;
    private int _visible;
    private int _requestedVisible;
    private int _failed;
    private int _disposing;
    private DispatcherTimer? _boundsTimer;
    private DispatcherTimer? _transitionTimer;
    private Grid? _backdrop;
    private ContentControl? _surface;
    private AvaloniaOverlayDrawingView? _overlayDrawing;
    private ScaleTransform? _animatedScale;
    private Control? _menuContent;
    private Control? _menuChrome;
    private AvaloniaStartupView? _startupView;
    private int _loadingVisible;
    private int _menuCreated;
    private int _destroyMenuAfterClose;
    private int _clearLoadingAfterClose;
    private int _openMenuAfterClose;
    private int _overlayUpdateQueued;
    private long _transitionStarted;
    private double _transitionFrom;
    private double _transitionTo;
    private double _transitionDurationSeconds;
    private double _transitionProgress;

    public AvaloniaOverlayHost(
        nint gameWindow,
        Func<object> createMenuContent,
        Action<bool> visibilityChanged,
        Func<AvaloniaWindowPlacement>? loadPlacement,
        Action<AvaloniaWindowPlacement>? savePlacement,
        Action<string> info,
        Action<string> error,
        FrameworkStartupProgress? startupProgress = null,
        bool recreateMenuOnClose = false,
        double horizontalMarginPercent = 12.5,
        double verticalMarginPercent = 8)
    {
        _gameWindow = gameWindow;
        _createMenuContent = createMenuContent;
        _visibilityChanged = visibilityChanged;
        // The full-screen modal surface is centered and has no persisted geometry.
        _ = loadPlacement;
        _ = savePlacement;
        _info = info;
        _error = error;
        _startupProgress = startupProgress;
        _recreateMenuOnClose = recreateMenuOnClose;
        _horizontalMarginPercent = Math.Clamp(horizontalMarginPercent, 0, 45);
        _verticalMarginPercent = Math.Clamp(verticalMarginPercent, 0, 45);
    }

    public bool HasStarted => Volatile.Read(ref _started) != 0;
    public bool IsReady => Volatile.Read(ref _ready) != 0;
    public bool IsVisible => Volatile.Read(ref _visible) != 0;
    internal bool IsStartupVisible => Volatile.Read(ref _loadingVisible) != 0;
    internal bool IsMenuCreated => Volatile.Read(ref _menuCreated) != 0;
    public bool HasFailed => Volatile.Read(ref _failed) != 0;
    public bool IsForeground =>
        _windowHandle != 0 &&
        Win32Native.GetAncestor(Win32Native.GetForegroundWindow(), Win32Native.GaRoot) ==
        _windowHandle;

    public void Start()
    {
        if (Volatile.Read(ref _disposing) != 0 ||
            Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Briefcase Avalonia UI"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetVisible(bool visible)
    {
        Volatile.Write(ref _requestedVisible, visible ? 1 : 0);
        if (visible) Start();
        if (!IsReady) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_startupProgress?.Snapshot is { IsComplete: false }) return;
            ApplyVisibility(visible);
        });
    }

    public void SubmitOverlay(OverlayFrameSnapshot frame)
    {
        lock (_overlayGate) _pendingOverlay = frame;
        if (!IsReady || Interlocked.Exchange(ref _overlayUpdateQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(ApplyPendingOverlay);
    }

    private void ApplyPendingOverlay()
    {
        OverlayFrameSnapshot frame;
        lock (_overlayGate)
        {
            frame = _pendingOverlay;
            Volatile.Write(ref _overlayUpdateQueued, 0);
        }
        var drawing = _overlayDrawing;
        var window = _window;
        if (drawing is null || window is null) return;
        drawing.Update(frame);
        if (drawing.HasContent)
        {
            if (!window.IsVisible)
            {
                window.Show();
                AttachToGame(window);
            }
            ConfigureInteraction(false);
            CoverGameClient(window);
            _boundsTimer?.Start();
        }
        else if (Volatile.Read(ref _visible) == 0 &&
                 Volatile.Read(ref _loadingVisible) == 0 &&
                 _transitionProgress <= 0)
        {
            _boundsTimer?.Stop();
            window.Hide();
        }
    }

    private void ApplyVisibility(bool visible)
    {
        var window = _window;
        if (window is null) return;
        if (visible)
        {
            // Reopening during a close cancels deferred destruction. The menu tree
            // is always complete before the opening animation can render a frame.
            Interlocked.Exchange(ref _destroyMenuAfterClose, 0);
            EnsureMenuContent();
            CoverGameClient(window);
            if (!window.IsVisible)
            {
                SetTransitionProgress(0);
                window.Show();
                AttachToGame(window);
                CoverGameClient(window);
            }
            ConfigureInteraction(true);
            _boundsTimer?.Start();
            window.Activate();
            Volatile.Write(ref _visible, 1);
            BeginTransition(1);
            return;
        }

        Volatile.Write(ref _visible, 0);
        if (_recreateMenuOnClose)
            Interlocked.Exchange(ref _destroyMenuAfterClose, 1);
        BeginTransition(0);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0) return;
        if (_startupProgress is not null)
            _startupProgress.Changed -= OnStartupProgressChanged;
        _boundsTimer?.Stop();
        _transitionTimer?.Stop();
        if (IsReady)
        {
            Dispatcher.UIThread.Post(() =>
            {
                DestroyMenuContent();
                DestroyLoadingContent();
                _window?.Close();
                _lifetime?.Shutdown();
            });
        }
        if (_thread is { IsAlive: true } && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(3));
        _window = null;
        _boundsTimer = null;
        _transitionTimer = null;
        _backdrop = null;
        _surface = null;
        _overlayDrawing = null;
        _animatedScale = null;
        _lifetime = null;
        _windowHandle = 0;
        Volatile.Write(ref _ready, 0);
    }

    internal void InitializeApplication(IClassicDesktopStyleApplicationLifetime lifetime)
    {
        _lifetime = lifetime;
        lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (Volatile.Read(ref _disposing) != 0)
        {
            lifetime.Shutdown();
            return;
        }

        _window = CreateWindow();
        if (Volatile.Read(ref _disposing) != 0)
        {
            _window.Close();
            lifetime.Shutdown();
            return;
        }

        Volatile.Write(ref _ready, 1);
        _info("Avalonia overlay: ready (software rendering)");
        if (_startupProgress is { } startup)
        {
            startup.Changed += OnStartupProgressChanged;
            ApplyStartupProgress(startup.Snapshot);
        }
        else if (Volatile.Read(ref _requestedVisible) != 0)
        {
            ApplyVisibility(true);
        }
    }

    private void Run()
    {
        try
        {
            if (Interlocked.CompareExchange(ref s_initializing, this, null) is not null)
                throw new InvalidOperationException(
                    "Only one Avalonia application can run in the Briefcase process.");

            AppBuilder.Configure<BriefcaseAvaloniaApplication>()
                .UseWin32()
                .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] })
                .UseSkia()
                .UseHarfBuzz()
                .StartWithClassicDesktopLifetime(
                    Array.Empty<string>(), ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _failed, 1);
            _error($"Avalonia overlay failed: {exception}");
        }
        finally
        {
            Interlocked.CompareExchange(ref s_initializing, null, this);
            Volatile.Write(ref _started, 0);
            Volatile.Write(ref _ready, 0);
            Volatile.Write(ref _visible, 0);
        }
    }

    private Window CreateWindow()
    {
        _surface = new ContentControl
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch
        };
        _backdrop = new Grid
        {
            Background = new SolidColorBrush(Color.Parse("#A6000000")),
            Opacity = 0,
            Children = { _surface }
        };
        _overlayDrawing = new AvaloniaOverlayDrawingView();
        var root = new Grid
        {
            Background = Brushes.Transparent,
            Children = { _overlayDrawing, _backdrop }
        };

        var window = new Window
        {
            Title = "Briefcase",
            Width = MenuWidth,
            Height = MenuHeight,
            Background = Brushes.Transparent,
            Content = root,
            CanResize = false,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            WindowDecorations = WindowDecorations.None,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent]
        };
        window.KeyDown += (_, args) =>
        {
            if (args.Key != Key.Escape) return;
            CloseFromUser();
            args.Handled = true;
        };
        window.Opened += (_, _) =>
        {
            AttachToGame(window);
            CoverGameClient(window);
        };
        window.Closing += (_, args) =>
        {
            if (Volatile.Read(ref _disposing) != 0) return;
            args.Cancel = true;
            CloseFromUser();
        };

        _boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _boundsTimer.Tick += (_, _) =>
        {
            if (window.IsVisible) CoverGameClient(window);
        };
        _transitionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _transitionTimer.Tick += (_, _) => AdvanceTransition();
        return window;
    }

    private void CloseFromUser()
    {
        if (Volatile.Read(ref _loadingVisible) != 0)
        {
            if (_startupProgress?.Snapshot.HasFailed != true) return;
            Volatile.Write(ref _loadingVisible, 0);
            Interlocked.Exchange(ref _clearLoadingAfterClose, 1);
            BeginTransition(0);
            return;
        }
        if (Interlocked.Exchange(ref _requestedVisible, 0) == 0 &&
            Volatile.Read(ref _visible) == 0)
            return;

        ApplyVisibility(false);
        _visibilityChanged(false);
    }

    private void BeginTransition(double target)
    {
        target = Math.Clamp(target, 0, 1);
        if (Math.Abs(_transitionProgress - target) < 0.001)
        {
            CompleteTransition(target);
            return;
        }

        _transitionFrom = _transitionProgress;
        _transitionTo = target;
        var fullDuration = target > _transitionProgress
            ? OpenTransitionDuration
            : CloseTransitionDuration;
        _transitionDurationSeconds = Math.Max(
            0.001, fullDuration.TotalSeconds * Math.Abs(target - _transitionProgress));
        _transitionStarted = Stopwatch.GetTimestamp();
        _transitionTimer?.Start();
    }

    private void AdvanceTransition()
    {
        var elapsed = Stopwatch.GetElapsedTime(_transitionStarted).TotalSeconds;
        var position = Math.Clamp(elapsed / _transitionDurationSeconds, 0, 1);
        var eased = _transitionTo > _transitionFrom
            ? 1 - Math.Pow(1 - position, 3)
            : Math.Pow(position, 3);
        SetTransitionProgress(_transitionFrom + ((_transitionTo - _transitionFrom) * eased));
        if (position >= 1) CompleteTransition(_transitionTo);
    }

    private void SetTransitionProgress(double progress)
    {
        _transitionProgress = Math.Clamp(progress, 0, 1);
        if (_backdrop is not null) _backdrop.Opacity = _transitionProgress;
        if (_animatedScale is null) return;
        var scale = HiddenMenuScale + ((1 - HiddenMenuScale) * _transitionProgress);
        _animatedScale.ScaleX = scale;
        _animatedScale.ScaleY = scale;
    }

    private void CompleteTransition(double target)
    {
        _transitionTimer?.Stop();
        SetTransitionProgress(target);
        if (target > 0) return;

        ConfigureInteraction(false);
        if (_overlayDrawing?.HasContent != true)
        {
            _boundsTimer?.Stop();
            _window?.Hide();
        }
        if (Interlocked.Exchange(ref _clearLoadingAfterClose, 0) != 0)
            DestroyLoadingContent();
        if (Interlocked.Exchange(ref _destroyMenuAfterClose, 0) != 0)
            DestroyMenuContent();
        if (Interlocked.Exchange(ref _openMenuAfterClose, 0) != 0 &&
            Volatile.Read(ref _requestedVisible) != 0)
        {
            ApplyVisibility(true);
            return;
        }
        if (Win32Native.IsWindow(_gameWindow)) Win32Native.SetForegroundWindow(_gameWindow);
    }

    private void OnStartupProgressChanged(FrameworkStartupSnapshot snapshot)
    {
        if (Volatile.Read(ref _disposing) != 0 || !IsReady) return;
        Dispatcher.UIThread.Post(() => ApplyStartupProgress(snapshot));
    }

    private void ApplyStartupProgress(FrameworkStartupSnapshot snapshot)
    {
        if (_window is null || _surface is null) return;
        if (!snapshot.IsComplete || snapshot.HasFailed)
        {
            EnsureLoadingContent();
            _startupView!.Update(snapshot);
            ShowStartupWindow();
            if (snapshot.HasFailed) ConfigureInteraction(true);
            return;
        }

        // A caller may provide an already-complete progress object. In that case
        // no startup visual was created and there is nothing to animate away.
        if (_startupView is null)
        {
            if (Volatile.Read(ref _requestedVisible) != 0) ApplyVisibility(true);
            return;
        }

        _startupView.Update(snapshot);
        Volatile.Write(ref _loadingVisible, 0);
        Interlocked.Exchange(ref _clearLoadingAfterClose, 1);
        if (Volatile.Read(ref _requestedVisible) != 0)
            Interlocked.Exchange(ref _openMenuAfterClose, 1);
        BeginTransition(0);
    }

    private void EnsureLoadingContent()
    {
        if (_startupView is not null || _surface is null) return;
        _animatedScale = null;
        _startupView = new AvaloniaStartupView(CloseFromUser);
        _surface.Content = _startupView;
    }

    private void DestroyLoadingContent()
    {
        if (ReferenceEquals(_surface?.Content, _startupView)) _surface!.Content = null;
        _startupView = null;
    }

    private void ShowStartupWindow()
    {
        var window = _window;
        if (window is null) return;
        var wasLoading = Interlocked.Exchange(ref _loadingVisible, 1) != 0;
        if (window.IsVisible) return;
        SetTransitionProgress(wasLoading ? _transitionProgress : 0);
        window.Show();
        AttachToGame(window);
        ConfigureInteraction(false);
        CoverGameClient(window);
        _boundsTimer?.Start();
        BeginTransition(1);
    }

    private void EnsureMenuContent()
    {
        if (Interlocked.Exchange(ref _menuCreated, 1) != 0 || _surface is null) return;
        try
        {
            if (_createMenuContent() is not Control content)
                throw new InvalidOperationException(
                    "The retained menu content factory did not return an Avalonia Control.");

            var close = new Button
            {
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 7,
                    Children =
                    {
                        AvaloniaIcons.Create(Briefcase.ClientModApi.UiIcon.Close, 15),
                        new TextBlock { Text = "Close", VerticalAlignment = VerticalAlignment.Center }
                    }
                },
                Padding = new Thickness(14, 5)
            };
            close.Click += (_, _) => CloseFromUser();
            var title = new TextBlock
            {
                Text = "BRIEFCASE",
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Color.Parse("#48DBB8")),
                VerticalAlignment = VerticalAlignment.Center
            };
            var subtitle = new TextBlock
            {
                Text = "Configuration",
                Margin = new Thickness(12, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.Parse("#AEB6C2")),
                VerticalAlignment = VerticalAlignment.Center
            };
            var header = new Grid
            {
                Height = 48,
                Margin = new Thickness(16, 8, 12, 4),
                ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
            };
            header.Children.Add(title);
            Grid.SetColumn(subtitle, 1);
            header.Children.Add(subtitle);
            Grid.SetColumn(close, 2);
            header.Children.Add(close);
            var body = new ContentControl { Content = content };
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            layout.Children.Add(header);
            Grid.SetRow(body, 1);
            layout.Children.Add(body);

            _animatedScale = new ScaleTransform(HiddenMenuScale, HiddenMenuScale);
            _menuChrome = new Border
            {
                Width = MenuWidth,
                Height = MenuHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(Color.Parse("#F2181D25")),
                BorderBrush = new SolidColorBrush(Color.Parse("#46505E")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                RenderTransform = _animatedScale,
                RenderTransformOrigin = RelativePoint.Center,
                Child = layout
            };
            _menuContent = content;
            _surface.Content = _menuChrome;
            _info(_recreateMenuOnClose
                ? "Avalonia menu: created for this opening"
                : "Avalonia menu: created and cached");
        }
        catch
        {
            Volatile.Write(ref _menuCreated, 0);
            throw;
        }
    }

    private void DestroyMenuContent()
    {
        if (_menuChrome is null && _menuContent is null) return;
        if (ReferenceEquals(_surface?.Content, _menuChrome)) _surface!.Content = null;
        if (_menuContent is IDisposable disposable) disposable.Dispose();
        _menuContent = null;
        _menuChrome = null;
        _animatedScale = null;
        Volatile.Write(ref _menuCreated, 0);
        _info("Avalonia menu: released after close");
    }

    private void AttachToGame(Window window)
    {
        var handle = window.TryGetPlatformHandle()?.Handle ?? 0;
        if (handle == 0) return;
        _windowHandle = handle;
        Win32Native.SetWindowLongPtr(handle, Win32Native.GwlHwndParent, _gameWindow);
    }

    private void CoverGameClient(Window window)
    {
        if (!TryGetGameClient(out var rectangle, out var origin) ||
            rectangle.Width <= 0 || rectangle.Height <= 0)
            return;
        var scale = EffectiveScale(window);
        var width = rectangle.Width / scale;
        var height = rectangle.Height / scale;
        if (_menuChrome is Border menu)
        {
            var size = AvaloniaMenuLayout.Calculate(
                width,
                height,
                _horizontalMarginPercent,
                _verticalMarginPercent);
            if (Math.Abs(menu.Width - size.Width) >= 0.5) menu.Width = size.Width;
            if (Math.Abs(menu.Height - size.Height) >= 0.5) menu.Height = size.Height;
        }
        if (Math.Abs(window.Width - width) >= 0.5) window.Width = width;
        if (Math.Abs(window.Height - height) >= 0.5) window.Height = height;
        if (window.Position.X != origin.X || window.Position.Y != origin.Y)
            window.Position = new PixelPoint(origin.X, origin.Y);
    }

    private void ConfigureInteraction(bool interactive)
    {
        var handle = _windowHandle;
        if (!Win32Native.IsWindow(handle)) return;
        var style = (long)Win32Native.GetWindowLongPtr(handle, Win32Native.GwlExStyle);
        if (interactive)
            style &= ~(Win32Native.WsExTransparent | Win32Native.WsExNoActivate);
        else
            style |= Win32Native.WsExTransparent | Win32Native.WsExNoActivate;
        Win32Native.SetWindowLongPtr(handle, Win32Native.GwlExStyle, (nint)style);
        Win32Native.SetWindowPos(
            handle,
            Win32Native.HwndTop,
            0,
            0,
            0,
            0,
            Win32Native.SwpNoMove |
            Win32Native.SwpNoSize |
            Win32Native.SwpNoActivate |
            Win32Native.SwpFrameChanged);
    }

    private bool TryGetGameClient(out Win32Native.Rect rectangle, out Win32Native.Point origin)
    {
        rectangle = default;
        origin = default;
        return Win32Native.GetClientRect(_gameWindow, out rectangle) &&
               Win32Native.ClientToScreen(_gameWindow, ref origin);
    }

    private static double EffectiveScale(Window window) =>
        double.IsFinite(window.RenderScaling) && window.RenderScaling > 0
            ? window.RenderScaling
            : 1;

    private sealed class BriefcaseAvaloniaApplication : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://Briefcase.AvaloniaUi/"))
            {
                Source = new Uri("avares://Briefcase.AvaloniaUi/Styles/BriefcaseTheme.axaml")
            });
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
                s_initializing?.InitializeApplication(lifetime);
            base.OnFrameworkInitializationCompleted();
        }
    }
}
