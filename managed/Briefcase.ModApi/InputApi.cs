using System.Runtime.InteropServices;

namespace Briefcase.ModApi;

/// <summary>
/// Read-only access to Windows virtual-key state for local client mods.
/// The API never injects input: callers can only observe whether a key is held.
/// </summary>
public readonly struct InputApi
{
    public bool IsAvailable => OperatingSystem.IsWindows();

    public bool IsDown(VirtualKey key) => IsDown((int)key);

    public bool IsDown(int virtualKey) =>
        IsAvailable && virtualKey is > 0 and < 256 &&
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public bool GameHasFocus
    {
        get
        {
            if (!IsAvailable) return false;
            var foreground = NativeMethods.GetForegroundWindow();
            if (foreground == 0) return false;
            NativeMethods.GetWindowThreadProcessId(foreground, out var processId);
            return processId == Environment.ProcessId;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int virtualKey);

        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(nint window, out int processId);
    }
}

public enum VirtualKey
{
    LeftMouse = 0x01,
    RightMouse = 0x02,
    MiddleMouse = 0x04,
    Mouse4 = 0x05,
    Mouse5 = 0x06,
    Backspace = 0x08,
    Tab = 0x09,
    Enter = 0x0D,
    Shift = 0x10,
    Control = 0x11,
    Alt = 0x12,
    Space = 0x20,
    A = 0x41,
    B = 0x42,
    C = 0x43,
    D = 0x44,
    E = 0x45,
    F = 0x46,
    G = 0x47,
    H = 0x48,
    I = 0x49,
    J = 0x4A,
    K = 0x4B,
    L = 0x4C,
    M = 0x4D,
    N = 0x4E,
    O = 0x4F,
    P = 0x50,
    Q = 0x51,
    R = 0x52,
    S = 0x53,
    T = 0x54,
    U = 0x55,
    V = 0x56,
    W = 0x57,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B
}
