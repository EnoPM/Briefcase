using System.Numerics;
using ImGuiNET;

namespace Briefcase.Rendering;

internal sealed class ImGuiInputBackend
{
    private readonly OverlayWindow _window;

    public ImGuiInputBackend(OverlayWindow window)
    {
        _window = window;
        _window.MessageReceived = ProcessMessage;
    }

    public void Update()
    {
        var io = ImGui.GetIO();
        if (_window.Interactive && Win32Native.GetCursorPos(out var cursor) &&
            Win32Native.ScreenToClient(_window.Handle, ref cursor))
            io.AddMousePosEvent(cursor.X, cursor.Y);
        else
            io.AddMousePosEvent(-float.MaxValue, -float.MaxValue);
    }

    public void Clear()
    {
        var io = ImGui.GetIO();
        io.ClearInputKeys();
        io.ClearEventsQueue();
    }

    private void ProcessMessage(uint message, nuint word, nint value)
    {
        var io = ImGui.GetIO();
        switch (message)
        {
            case Win32Native.WmLButtonDown: SetMouseButton(io, 0, true); break;
            case Win32Native.WmLButtonUp: SetMouseButton(io, 0, false); break;
            case Win32Native.WmRButtonDown: SetMouseButton(io, 1, true); break;
            case Win32Native.WmRButtonUp: SetMouseButton(io, 1, false); break;
            case Win32Native.WmMButtonDown: SetMouseButton(io, 2, true); break;
            case Win32Native.WmMButtonUp: SetMouseButton(io, 2, false); break;
            case Win32Native.WmXButtonDown:
                SetMouseButton(io, Win32Native.HighWord(word) == 1 ? 3 : 4, true);
                break;
            case Win32Native.WmXButtonUp:
                SetMouseButton(io, Win32Native.HighWord(word) == 1 ? 3 : 4, false);
                break;
            case Win32Native.WmMouseWheel:
                io.AddMouseWheelEvent(0, Win32Native.HighWord(word) / 120.0f);
                break;
            case Win32Native.WmMouseHWheel:
                io.AddMouseWheelEvent(Win32Native.HighWord(word) / 120.0f, 0);
                break;
            case Win32Native.WmKeyDown:
            case Win32Native.WmSysKeyDown:
                AddKeyEvent(io, checked((int)word), true);
                break;
            case Win32Native.WmKeyUp:
            case Win32Native.WmSysKeyUp:
                AddKeyEvent(io, checked((int)word), false);
                break;
            case Win32Native.WmChar when word is > 0 and <= 0xffff:
                io.AddInputCharacter((uint)word);
                break;
        }
    }

    private void SetMouseButton(ImGuiIOPtr io, int button, bool down)
    {
        if (down && Win32Native.GetCapture() == 0)
            Win32Native.SetCapture(_window.Handle);
        io.AddMouseButtonEvent(button, down);
        if (!down && !ImGui.IsAnyMouseDown() && Win32Native.GetCapture() == _window.Handle)
            Win32Native.ReleaseCapture();
    }

    private static void AddKeyEvent(ImGuiIOPtr io, int virtualKey, bool down)
    {
        var key = virtualKey switch
        {
            0x09 => ImGuiKey.Tab,
            0x0D => ImGuiKey.Enter,
            0x1B => ImGuiKey.Escape,
            0x20 => ImGuiKey.Space,
            0x21 => ImGuiKey.PageUp,
            0x22 => ImGuiKey.PageDown,
            0x23 => ImGuiKey.End,
            0x24 => ImGuiKey.Home,
            0x25 => ImGuiKey.LeftArrow,
            0x26 => ImGuiKey.UpArrow,
            0x27 => ImGuiKey.RightArrow,
            0x28 => ImGuiKey.DownArrow,
            0x2D => ImGuiKey.Insert,
            0x2E => ImGuiKey.Delete,
            0x08 => ImGuiKey.Backspace,
            >= 0x30 and <= 0x39 => ImGuiKey._0 + (virtualKey - 0x30),
            >= 0x41 and <= 0x5A => ImGuiKey.A + (virtualKey - 0x41),
            _ => ImGuiKey.None
        };
        if (key != ImGuiKey.None) io.AddKeyEvent(key, down);
        io.AddKeyEvent(ImGuiKey.ModCtrl, IsDown(0x11));
        io.AddKeyEvent(ImGuiKey.ModShift, IsDown(0x10));
        io.AddKeyEvent(ImGuiKey.ModAlt, IsDown(0x12));
    }

    private static bool IsDown(int key) => (Win32Native.GetAsyncKeyState(key) & 0x8000) != 0;
}
