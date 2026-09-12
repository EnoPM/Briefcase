using System.Runtime.InteropServices;

namespace Briefcase.Rendering;

internal static class Win32Native
{
    internal const uint WsPopup = 0x80000000;
    internal const uint WsCaption = 0x00C00000;
    internal const uint WsThickFrame = 0x00040000;
    internal const uint WsSystemMenu = 0x00080000;
    internal const uint WsMinimizeBox = 0x00020000;
    internal const uint WsMaximizeBox = 0x00010000;
    internal const int GwlWndProc = -4;
    internal const int GwlStyle = -16;
    internal const int GwlExStyle = -20;
    internal const int GwlHwndParent = -8;
    internal const nint HwndTop = -1;
    internal const uint WsExTopmost = 0x00000008;
    internal const uint WsExTransparent = 0x00000020;
    internal const uint WsExToolWindow = 0x00000080;
    internal const uint WsExLayered = 0x00080000;
    internal const uint WsExNoActivate = 0x08000000;
    internal const uint WsExNoRedirectionBitmap = 0x00200000;
    internal const uint SwHide = 0;
    internal const uint SwShowNoActivate = 4;
    internal const uint SwpNoSize = 0x0001;
    internal const uint SwpNoMove = 0x0002;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;
    internal const uint SwpShowWindow = 0x0040;
    internal const uint PmRemove = 0x0001;
    internal const uint GaRoot = 2;
    internal const uint LwaAlpha = 0x00000002;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmSize = 0x0005;
    internal const uint WmSetCursor = 0x0020;
    internal const uint WmMouseActivate = 0x0021;
    internal const uint WmNcCalcSize = 0x0083;
    internal const uint WmNcHitTest = 0x0084;
    internal const uint WmNcPaint = 0x0085;
    internal const uint WmNcActivate = 0x0086;
    internal const uint WmNcMouseMove = 0x00A0;
    internal const uint WmNcXButtonDoubleClick = 0x00AD;
    internal const uint WmSysCommand = 0x0112;
    internal const uint WmKeyDown = 0x0100;
    internal const uint WmKeyUp = 0x0101;
    internal const uint WmChar = 0x0102;
    internal const uint WmSysKeyDown = 0x0104;
    internal const uint WmSysKeyUp = 0x0105;
    internal const uint WmMouseMove = 0x0200;
    internal const uint WmLButtonDown = 0x0201;
    internal const uint WmLButtonUp = 0x0202;
    internal const uint WmRButtonDown = 0x0204;
    internal const uint WmRButtonUp = 0x0205;
    internal const uint WmMButtonDown = 0x0207;
    internal const uint WmMButtonUp = 0x0208;
    internal const uint WmMouseWheel = 0x020A;
    internal const uint WmXButtonDown = 0x020B;
    internal const uint WmXButtonUp = 0x020C;
    internal const uint WmMouseHWheel = 0x020E;
    internal const uint ScKeyMenu = 0xF100;
    internal const nint HtTransparent = -1;
    internal const nint HtNowhere = 0;
    internal const ushort HtClient = 1;
    internal const nint HtLeft = 10;
    internal const nint HtRight = 11;
    internal const nint HtTop = 12;
    internal const nint HtTopLeft = 13;
    internal const nint HtTopRight = 14;
    internal const nint HtBottom = 15;
    internal const nint HtBottomLeft = 16;
    internal const nint HtBottomRight = 17;
    internal const nint MaActivate = 1;
    internal const nint MaNoActivate = 3;
    internal const int VkEscape = 0x1B;
    internal const int VkF1 = 0x70;
    internal const int DwmwaNcRenderingPolicy = 2;
    internal const int DwmwaAllowNcPaint = 4;
    internal const int DwmNcRenderingEnabled = 2;
    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaBorderColor = 34;
    internal const int DwmwaCaptionColor = 35;
    internal const int DwmwaTextColor = 36;
    internal const uint DwmColorNone = 0xFFFFFFFE;
    internal const uint DarkCaptionColor = 0x00202020;
    internal const uint LightCaptionTextColor = 0x00F0F0F0;
    internal const int SmCxSizeFrame = 32;
    internal const int SmCxPaddedBorder = 92;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WindowClass
    {
        public uint Size;
        public uint Style;
        public WindowProcedure Procedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string ClassName;
        public nint SmallIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint WindowProcedure(nint window, uint message, nuint word, nint value);
    internal delegate bool EnumWindowsProcedure(nint window, nint value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ushort RegisterClassExW(ref WindowClass windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(
        uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu,
        nint instance, nint parameter);

    [DllImport("user32.dll")]
    internal static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint DefWindowProcW(nint window, uint message, nuint word, nint value);

    [DllImport("user32.dll")]
    internal static extern nint CallWindowProcW(
        nint previous, nint window, uint message, nuint word, nint value);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProcedure callback, nint value);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(nint window, char[] className, int maximum);

    [DllImport("user32.dll")]
    internal static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    internal static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    internal static extern bool GetWindowRect(nint window, out Rect rectangle);

    [DllImport("user32.dll")]
    internal static extern bool GetClientRect(nint window, out Rect rectangle);

    [DllImport("user32.dll")]
    internal static extern bool IsZoomed(nint window);

    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern int GetSystemMetricsForDpi(int index, uint dpi);

    [DllImport("user32.dll")]
    internal static extern bool ClientToScreen(nint window, ref Point point);

    [DllImport("user32.dll")]
    internal static extern bool ScreenToClient(nint window, ref Point point);

    [DllImport("user32.dll")]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    internal static extern bool SetWindowPos(
        nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(nint window, uint command);

    [DllImport("user32.dll")]
    internal static extern bool PeekMessageW(
        out Message message, nint window, uint minimum, uint maximum, uint remove);

    [DllImport("user32.dll")]
    internal static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    internal static extern nint DispatchMessageW(ref Message message);

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern nint GetAncestor(nint window, uint flags);

    [DllImport("user32.dll")]
    internal static extern nint SetFocus(nint window);

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    internal static extern nint GetCapture();

    [DllImport("user32.dll")]
    internal static extern nint SetCapture(nint window);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern bool ClipCursor(nint rectangle);

    [DllImport("user32.dll")]
    internal static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    internal static extern bool SetLayeredWindowAttributes(
        nint window, uint colorKey, byte alpha, uint flags);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    internal static extern int SetWindowTheme(
        nint window, string? subAppName, string? subIdList);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmSetWindowAttribute(
        nint window, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    internal static extern int DwmExtendFrameIntoClientArea(
        nint window, ref Margins margins);

    internal static short HighWord(nuint value) => unchecked((short)((value >> 16) & 0xffff));
    internal static ushort LowWord(nint value) => unchecked((ushort)((nuint)value & 0xffff));
    internal static short SignedLowWord(nint value) => unchecked((short)((nuint)value & 0xffff));
}
