using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Briefcase.Rendering;

internal sealed class OverlayWindow : IDisposable
{
    private static OverlayWindow? _current;
    private readonly nint _instance;
    private readonly string _className;
    private readonly Win32Native.WindowProcedure _procedure;
    private bool _interactive;

    public OverlayWindow(nint gameWindow)
    {
        GameWindow = gameWindow;
        _current = this;
        _instance = Win32Native.GetModuleHandleW(null);
        _className = $"Briefcase.ManagedOverlay.{Environment.ProcessId}";
        _procedure = WindowProcedure;

        var windowClass = new Win32Native.WindowClass
        {
            Size = checked((uint)Marshal.SizeOf<Win32Native.WindowClass>()),
            Instance = _instance,
            Procedure = _procedure,
            ClassName = _className
        };
        if (Win32Native.RegisterClassExW(ref windowClass) == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not register the overlay window class.");

        const uint extendedStyle =
            Win32Native.WsExToolWindow | Win32Native.WsExNoRedirectionBitmap |
            Win32Native.WsExLayered | Win32Native.WsExTransparent |
            Win32Native.WsExNoActivate | Win32Native.WsExTopmost;
        Handle = Win32Native.CreateWindowExW(
            extendedStyle, _className, "Briefcase managed overlay",
            Win32Native.WsPopup, 100, 100, 800, 500,
            gameWindow, 0, _instance, 0);
        if (Handle == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create the overlay window.");
        if (!Win32Native.SetLayeredWindowAttributes(Handle, 0, 255, Win32Native.LwaAlpha))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not configure overlay transparency.");
    }

    public nint Handle { get; private set; }
    public nint GameWindow { get; }
    public Action<uint, nuint, nint>? MessageReceived { get; set; }
    public bool Interactive => _interactive;

    public static nint FindGameWindow()
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

    public bool IsGameOrOverlayForeground()
    {
        var root = Win32Native.GetAncestor(Win32Native.GetForegroundWindow(), Win32Native.GaRoot);
        return root == GameWindow || root == Handle;
    }

    public bool GameIsMinimized => Win32Native.IsIconic(GameWindow);

    public void PumpMessages()
    {
        while (Win32Native.PeekMessageW(out var message, 0, 0, 0, Win32Native.PmRemove))
        {
            Win32Native.TranslateMessage(ref message);
            Win32Native.DispatchMessageW(ref message);
        }
    }

    public bool AlignToGame(out uint width, out uint height)
    {
        width = 0;
        height = 0;
        if (!Win32Native.GetClientRect(GameWindow, out var rectangle) ||
            rectangle.Width <= 0 || rectangle.Height <= 0) return false;
        var origin = new Win32Native.Point();
        if (!Win32Native.ClientToScreen(GameWindow, ref origin)) return false;
        Win32Native.SetWindowPos(
            Handle, Win32Native.HwndTop, origin.X, origin.Y,
            rectangle.Width, rectangle.Height,
            Win32Native.SwpNoActivate | Win32Native.SwpShowWindow);
        width = checked((uint)rectangle.Width);
        height = checked((uint)rectangle.Height);
        return true;
    }

    public void Hide() => Win32Native.ShowWindow(Handle, Win32Native.SwHide);

    public void SetInteractive(bool interactive, bool returnFocus)
    {
        if (_interactive == interactive) return;
        _interactive = interactive;
        var style = (long)Win32Native.GetWindowLongPtr(Handle, Win32Native.GwlExStyle);
        if (interactive)
            style &= ~(Win32Native.WsExTransparent | Win32Native.WsExNoActivate);
        else
            style |= Win32Native.WsExTransparent | Win32Native.WsExNoActivate;
        Win32Native.SetWindowLongPtr(Handle, Win32Native.GwlExStyle, (nint)style);
        Win32Native.SetWindowPos(
            Handle, interactive ? Win32Native.HwndTop : 0,
            0, 0, 0, 0,
            Win32Native.SwpNoMove | Win32Native.SwpNoSize |
            (interactive ? 0 : Win32Native.SwpNoZOrder) |
            Win32Native.SwpNoActivate | Win32Native.SwpFrameChanged);

        if (interactive)
        {
            Win32Native.ClipCursor(0);
            Win32Native.SetForegroundWindow(Handle);
            Win32Native.SetFocus(Handle);
        }
        else
        {
            if (Win32Native.GetCapture() == Handle) Win32Native.ReleaseCapture();
            if (returnFocus && Win32Native.IsWindow(GameWindow))
                Win32Native.SetForegroundWindow(GameWindow);
        }
    }

    public void Dispose()
    {
        MessageReceived = null;
        if (Handle != 0)
        {
            Win32Native.DestroyWindow(Handle);
            Handle = 0;
        }
        Win32Native.UnregisterClassW(_className, _instance);
        if (ReferenceEquals(_current, this)) _current = null;
    }

    private static nint WindowProcedure(nint window, uint message, nuint word, nint value)
    {
        var current = _current;
        current?.MessageReceived?.Invoke(message, word, value);
        if (current is not null)
        {
            if (message == Win32Native.WmMouseActivate)
                return current._interactive ? Win32Native.MaActivate : Win32Native.MaNoActivate;
            if (message == Win32Native.WmNcHitTest && !current._interactive)
                return Win32Native.HtTransparent;
            if (message == Win32Native.WmSetCursor && current._interactive &&
                Win32Native.LowWord(value) == 1)
            {
                Win32Native.SetCursor(0);
                return 1;
            }
            if (message == Win32Native.WmSysCommand && ((uint)word & 0xfff0) == Win32Native.ScKeyMenu)
                return 0;
        }
        return Win32Native.DefWindowProcW(window, message, word, value);
    }
}
