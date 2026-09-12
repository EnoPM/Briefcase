using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Briefcase.Rendering;

/// <summary>
/// Restores the standard Windows frame on Unreal's top-level window. Some
/// Deceive Inc builds expose Windowed and Borderless modes while applying the
/// same WS_POPUP style to both, which prevents Windows snap layouts.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GameWindowChrome : IDisposable
{
    private const uint RequiredStyle =
        Win32Native.WsCaption |
        Win32Native.WsThickFrame |
        Win32Native.WsSystemMenu |
        Win32Native.WsMinimizeBox |
        Win32Native.WsMaximizeBox;
    private const uint ManagedStyleMask = RequiredStyle | Win32Native.WsPopup;

    private readonly nint _window;
    private readonly uint _originalManagedStyle;
    private readonly Action<string> _info;
    private readonly Action<string> _error;
    private readonly Win32Native.WindowProcedure _windowProcedure;
    private nint _originalWindowProcedure;
    private nint _windowProcedurePointer;
    private long _nextCheck;
    private bool _changed;
    private bool _reportedSuccess;
    private bool _reportedFailure;

    public GameWindowChrome(nint window, Action<string> info, Action<string> error)
    {
        if (!Win32Native.IsWindow(window))
            throw new ArgumentException("The Unreal window handle is invalid.", nameof(window));
        _window = window;
        _info = info;
        _error = error;
        _originalManagedStyle = ReadStyle(window) & ManagedStyleMask;
        _windowProcedure = WindowProcedure;
        InstallNonClientProcedure();
        Maintain(force: true);
    }

    /// <summary>
    /// Unreal may recreate its borderless style after changing video options.
    /// This inexpensive check runs twice per second and writes only when the
    /// relevant style bits changed.
    /// </summary>
    public void Maintain(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now < _nextCheck) return;
        _nextCheck = now + 500;
        if (!Win32Native.IsWindow(_window)) return;

        var current = ReadStyle(_window);
        var desired = AddSystemChrome(current);
        if (!force && current == desired) return;
        if (!TrySetStyle(desired, out var error))
        {
            if (!_reportedFailure)
            {
                _reportedFailure = true;
                _error($"Could not enable the game window chrome: {error}");
            }
            return;
        }

        _changed = true;
        if (!_reportedSuccess)
        {
            _reportedSuccess = true;
            _info("Game window chrome: title bar, resize border, and Windows snap enabled");
        }
    }

    internal static uint AddSystemChrome(uint style) =>
        (style & ~Win32Native.WsPopup) | RequiredStyle;

    internal static bool UsesDefaultNonClientProcedure(uint message, nint value) =>
        message is Win32Native.WmNcHitTest or
            Win32Native.WmNcPaint or
            Win32Native.WmNcActivate ||
        message >= Win32Native.WmNcMouseMove &&
            message <= Win32Native.WmNcXButtonDoubleClick ||
        message == Win32Native.WmSetCursor &&
            Win32Native.LowWord(value) != Win32Native.HtClient;

    public void Dispose()
    {
        if (!Win32Native.IsWindow(_window))
        {
            _originalWindowProcedure = 0;
            _windowProcedurePointer = 0;
            return;
        }
        if (_changed)
        {
            var current = ReadStyle(_window);
            var restored = (current & ~ManagedStyleMask) | _originalManagedStyle;
            TrySetStyle(restored, out _);
        }
        RemoveNonClientProcedure();
    }

    private void InstallNonClientProcedure()
    {
        _windowProcedurePointer = Marshal.GetFunctionPointerForDelegate(_windowProcedure);
        Marshal.SetLastPInvokeError(0);
        _originalWindowProcedure = Win32Native.SetWindowLongPtr(
            _window, Win32Native.GwlWndProc, _windowProcedurePointer);
        var error = Marshal.GetLastPInvokeError();
        if (_originalWindowProcedure == 0)
            throw new Win32Exception(
                error, "Could not install the Unreal non-client window procedure.");
    }

    private void RemoveNonClientProcedure()
    {
        if (_originalWindowProcedure == 0 || !Win32Native.IsWindow(_window)) return;
        if (Win32Native.GetWindowLongPtr(_window, Win32Native.GwlWndProc) ==
            _windowProcedurePointer)
            Win32Native.SetWindowLongPtr(
                _window, Win32Native.GwlWndProc, _originalWindowProcedure);
        _originalWindowProcedure = 0;
        _windowProcedurePointer = 0;
    }

    private nint WindowProcedure(nint window, uint message, nuint word, nint value)
    {
        if (message == Win32Native.WmNcCalcSize)
            return CalculateNonClientArea(window, message, word, value);
        if (message == Win32Native.WmNcHitTest)
        {
            var resizeHit = HitTestResizeBorder(value);
            if (resizeHit != Win32Native.HtNowhere) return resizeHit;
        }
        if (UsesDefaultNonClientProcedure(message, value))
            return Win32Native.DefWindowProcW(window, message, word, value);
        var original = Volatile.Read(ref _originalWindowProcedure);
        return original != 0
            ? Win32Native.CallWindowProcW(original, window, message, word, value)
            : Win32Native.DefWindowProcW(window, message, word, value);
    }

    private nint CalculateNonClientArea(
        nint window, uint message, nuint word, nint value)
    {
        var result = Win32Native.DefWindowProcW(window, message, word, value);
        if (value == 0 || Win32Native.IsZoomed(window)) return result;

        // DefWindowProc reserves the resize frame inside the window rectangle.
        // Give the side and bottom pixels back to Unreal. The top frame stays
        // intact because DWM positions the caption text and buttons below it.
        // HitTestResizeBorder keeps every resize target available to the mouse.
        var border = ResizeBorderThickness(window);
        var client = Marshal.PtrToStructure<Win32Native.Rect>(value);
        client.Left -= border;
        client.Right += border;
        client.Bottom += border;
        Marshal.StructureToPtr(client, value, false);
        return result;
    }

    private nint HitTestResizeBorder(nint value)
    {
        if (Win32Native.IsZoomed(_window) ||
            !Win32Native.GetWindowRect(_window, out var window))
            return Win32Native.HtNowhere;
        return HitTestResizeBorder(
            window,
            ResizeBorderThickness(_window),
            Win32Native.SignedLowWord(value),
            Win32Native.HighWord(unchecked((nuint)value)));
    }

    internal static nint HitTestResizeBorder(
        Win32Native.Rect window, int border, int x, int y)
    {
        var left = x >= window.Left && x < window.Left + border;
        var right = x < window.Right && x >= window.Right - border;
        var top = y >= window.Top && y < window.Top + border;
        var bottom = y < window.Bottom && y >= window.Bottom - border;

        if (top) return left ? Win32Native.HtTopLeft :
            right ? Win32Native.HtTopRight : Win32Native.HtTop;
        if (bottom) return left ? Win32Native.HtBottomLeft :
            right ? Win32Native.HtBottomRight : Win32Native.HtBottom;
        if (left) return Win32Native.HtLeft;
        if (right) return Win32Native.HtRight;
        return Win32Native.HtNowhere;
    }

    private static int ResizeBorderThickness(nint window)
    {
        var dpi = Win32Native.GetDpiForWindow(window);
        return Win32Native.GetSystemMetricsForDpi(
                   Win32Native.SmCxSizeFrame, dpi) +
               Win32Native.GetSystemMetricsForDpi(
                   Win32Native.SmCxPaddedBorder, dpi);
    }

    private bool TrySetStyle(uint style, out string error)
    {
        Marshal.SetLastPInvokeError(0);
        var previous = Win32Native.SetWindowLongPtr(
            _window, Win32Native.GwlStyle, unchecked((nint)(nuint)style));
        var lastError = Marshal.GetLastPInvokeError();
        if (previous == 0 && lastError != 0)
        {
            error = new Win32Exception(lastError).Message;
            return false;
        }
        // Unreal creates this window as a borderless surface and can leave the
        // DWM frame extended over the whole window. Resetting the theme and
        // margins turns the newly enabled caption into a real system bar.
        Win32Native.SetWindowTheme(_window, null, null);
        var frameMargins = default(Win32Native.Margins);
        Win32Native.DwmExtendFrameIntoClientArea(_window, ref frameMargins);
        var nonClientRendering = Win32Native.DwmNcRenderingEnabled;
        Win32Native.DwmSetWindowAttribute(
            _window, Win32Native.DwmwaNcRenderingPolicy,
            ref nonClientRendering, sizeof(int));
        var allowNonClientPaint = 0;
        Win32Native.DwmSetWindowAttribute(
            _window, Win32Native.DwmwaAllowNcPaint,
            ref allowNonClientPaint, sizeof(int));
        var darkMode = 1;
        Win32Native.DwmSetWindowAttribute(
            _window, Win32Native.DwmwaUseImmersiveDarkMode,
            ref darkMode, sizeof(int));
        SetDwmColor(Win32Native.DwmwaBorderColor, Win32Native.DwmColorNone);
        SetDwmColor(Win32Native.DwmwaCaptionColor, Win32Native.DarkCaptionColor);
        SetDwmColor(Win32Native.DwmwaTextColor, Win32Native.LightCaptionTextColor);
        if (!Win32Native.SetWindowPos(
                _window, 0, 0, 0, 0, 0,
                Win32Native.SwpNoMove |
                Win32Native.SwpNoSize |
                Win32Native.SwpNoZOrder |
                Win32Native.SwpNoActivate |
                Win32Native.SwpFrameChanged))
        {
            error = new Win32Exception(Marshal.GetLastPInvokeError()).Message;
            return false;
        }
        error = "";
        return true;
    }

    private void SetDwmColor(int attribute, uint color)
    {
        var value = unchecked((int)color);
        Win32Native.DwmSetWindowAttribute(_window, attribute, ref value, sizeof(int));
    }

    private static uint ReadStyle(nint window) =>
        unchecked((uint)(nuint)Win32Native.GetWindowLongPtr(window, Win32Native.GwlStyle));
}
