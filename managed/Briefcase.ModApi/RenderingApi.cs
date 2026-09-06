using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Numerics;
using System.Text;
using Briefcase.ModApi.Interop;
#if !BRIEFCASE_HEADLESS
using ImGuiNET;
#endif

namespace Briefcase.ModApi;

public readonly record struct RenderFrame(
    uint Width,
    uint Height,
    float DeltaSeconds,
    ulong FrameNumber,
    bool MenuVisible,
    ImGuiApi ImGui);

public readonly unsafe struct RenderingApi(NativeRenderingApi* api, ModContext context)
{
    private readonly NativeRenderingApi* _api = api;
    private readonly ModContext _context = context;

    public bool IsAvailable =>
        ManagedRenderingBridge.Current?.IsAvailable == true ||
        (_api != null && _api->ApiVersion == BriefcaseAbi.RenderingApiVersion &&
         _api->RegisterCallback != null && _api->UnregisterCallback != null);

    public bool MenuVisible
    {
        get
        {
            var managed = ManagedRenderingBridge.Current;
            if (managed?.IsAvailable == true) return managed.MenuVisible;
            return IsAvailable && _api->GetMenuVisible != null &&
                   _api->GetMenuVisible(_api->Context) != 0;
        }
        set
        {
            var managed = ManagedRenderingBridge.Current;
            if (managed?.IsAvailable == true)
            {
                managed.MenuVisible = value;
                return;
            }
            if (IsAvailable && _api->SetMenuVisible != null)
                _api->SetMenuVisible(_api->Context, value ? 1u : 0u);
        }
    }

    public IRenderRegistration Register(Action<RenderFrame> draw)
    {
        ArgumentNullException.ThrowIfNull(draw);
        var managed = ManagedRenderingBridge.Current;
        if (managed?.IsAvailable == true)
            return managed.Register(draw);
        if (!IsAvailable)
            throw new InvalidOperationException("The host rendering API is unavailable.");
        return new RenderRegistration(_api, _context, draw);
    }
}

internal interface IManagedRenderingService
{
    bool IsAvailable { get; }
    bool MenuVisible { get; set; }
    IRenderRegistration Register(Action<RenderFrame> draw);
}

internal static class ManagedRenderingBridge
{
    public static IManagedRenderingService? Current { get; set; }
}

public interface IRenderRegistration : IDisposable
{
    bool RenderWhenMenuHidden { get; set; }
}

internal sealed unsafe class RenderRegistration : IRenderRegistration
{
    private NativeRenderingApi* _api;
    private ulong _registrationId;
    private GCHandle _stateHandle;
    private bool _renderWhenMenuHidden;

    public RenderRegistration(
        NativeRenderingApi* api,
        ModContext context,
        Action<RenderFrame> draw)
    {
        _api = api;
        _stateHandle = GCHandle.Alloc(new CallbackState(context, draw));
        var userContext = (void*)GCHandle.ToIntPtr(_stateHandle);
        ulong registrationId = 0;
        var registered = api->RegisterCallback(
            api->Context, &Invoke, userContext, &registrationId) != 0;
        if (!registered)
        {
            _stateHandle.Free();
            _api = null;
            throw new InvalidOperationException("The native host rejected the render callback.");
        }
        _registrationId = registrationId;
    }

    public bool RenderWhenMenuHidden
    {
        get => _renderWhenMenuHidden;
        set
        {
            var api = _api;
            if (api == null || api->SetCallbackActive == null)
                throw new ObjectDisposedException(nameof(RenderRegistration));
            if (api->SetCallbackActive(
                    api->Context, _registrationId, value ? 1u : 0u) == 0)
                throw new InvalidOperationException("The native render registration is no longer valid.");
            _renderWhenMenuHidden = value;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Invoke(void* userContext, NativeRenderFrame* nativeFrame)
    {
        if (userContext == null || nativeFrame == null ||
            nativeFrame->StructSize < (uint)sizeof(NativeRenderFrame)) return;
        try
        {
            var handle = GCHandle.FromIntPtr((nint)userContext);
            if (handle.Target is not CallbackState state) return;
            state.Draw(new RenderFrame(
                nativeFrame->Width,
                nativeFrame->Height,
                nativeFrame->DeltaSeconds,
                nativeFrame->FrameNumber,
                nativeFrame->MenuVisible != 0,
                new ImGuiApi(state.Context.RenderingNative)));
        }
        catch (Exception exception)
        {
            try
            {
                var handle = GCHandle.FromIntPtr((nint)userContext);
                if (handle.Target is CallbackState state)
                    state.Context.Error($"Managed render callback failed: {exception}");
            }
            catch
            {
                // Exceptions cannot cross the unmanaged callback boundary.
            }
        }
    }

    public void Dispose()
    {
        var api = _api;
        if (api == null) return;
        _api = null;
        api->UnregisterCallback(api->Context, _registrationId);
        _registrationId = 0;
        if (_stateHandle.IsAllocated) _stateHandle.Free();
    }

    private sealed record CallbackState(ModContext Context, Action<RenderFrame> Draw);
}

public readonly unsafe struct ImGuiApi
{
    private readonly NativeRenderingApi* _api;
    private readonly bool _managed;

    internal ImGuiApi(NativeRenderingApi* api)
    {
        _api = api;
        _managed = false;
    }

    private ImGuiApi(bool managed)
    {
        _api = null;
        _managed = managed;
    }

    internal static ImGuiApi Managed => new(true);

    public bool Begin(string name, ref bool open, ImGuiWindowFlags flags = ImGuiWindowFlags.None)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
            return ImGui.Begin(name, ref open, (ImGuiNET.ImGuiWindowFlags)(uint)flags);
#endif
        if (_api == null || _api->Begin == null) return false;
        var byteCount = Encoding.UTF8.GetByteCount(name);
        var utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(name, utf8);
        uint nativeOpen = open ? 1u : 0u;
        fixed (byte* namePointer = utf8)
        {
            var result = _api->Begin(
                _api->Context, namePointer, (uint)utf8.Length, &nativeOpen, (uint)flags) != 0;
            open = nativeOpen != 0;
            return result;
        }
    }

    public void End()
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.End(); return; }
#endif
        if (_api != null && _api->End != null) _api->End(_api->Context);
    }

    public void Text(string text)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.TextUnformatted(text); return; }
#endif
        if (_api == null || _api->Text == null) return;
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var utf8 = byteCount <= 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(text, utf8);
        fixed (byte* pointer = utf8)
            _api->Text(_api->Context, pointer, (uint)utf8.Length);
    }

    public bool Checkbox(string label, ref bool value)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) return ImGui.Checkbox(label, ref value);
#endif
        if (_api == null || _api->Checkbox == null) return false;
        var byteCount = Encoding.UTF8.GetByteCount(label);
        var utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(label, utf8);
        uint nativeValue = value ? 1u : 0u;
        fixed (byte* pointer = utf8)
        {
            var changed = _api->Checkbox(
                _api->Context, pointer, (uint)utf8.Length, &nativeValue) != 0;
            value = nativeValue != 0;
            return changed;
        }
    }

    public bool SliderFloat(
        string label,
        ref float value,
        float minimum,
        float maximum,
        string format = "%.3f")
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
            return ImGui.SliderFloat(label, ref value, minimum, maximum, format);
#endif
        if (_api == null || _api->SliderFloat == null) return false;
        var labelCount = Encoding.UTF8.GetByteCount(label);
        var formatCount = Encoding.UTF8.GetByteCount(format);
        var labelUtf8 = labelCount <= 512 ? stackalloc byte[labelCount] : new byte[labelCount];
        var formatUtf8 = formatCount <= 64 ? stackalloc byte[formatCount] : new byte[formatCount];
        Encoding.UTF8.GetBytes(label, labelUtf8);
        Encoding.UTF8.GetBytes(format, formatUtf8);
        fixed (byte* labelPointer = labelUtf8)
        fixed (byte* formatPointer = formatUtf8)
        fixed (float* valuePointer = &value)
            return _api->SliderFloat(
                _api->Context, labelPointer, (uint)labelUtf8.Length, valuePointer,
                minimum, maximum, formatPointer, (uint)formatUtf8.Length) != 0;
    }

    public bool Button(string label, float width = 0, float height = 0)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) return ImGui.Button(label, new Vector2(width, height));
#endif
        if (_api == null || _api->Button == null) return false;
        var byteCount = Encoding.UTF8.GetByteCount(label);
        var utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(label, utf8);
        fixed (byte* pointer = utf8)
            return _api->Button(
                _api->Context, pointer, (uint)utf8.Length, width, height) != 0;
    }

    public bool BeginCombo(
        string label,
        string preview,
        ImGuiComboFlags flags = ImGuiComboFlags.None)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
            return ImGui.BeginCombo(label, preview, (ImGuiNET.ImGuiComboFlags)(uint)flags);
#endif
        if (_api == null || _api->BeginCombo == null) return false;
        var encodedLabel = Encoding.UTF8.GetBytes(label);
        var encodedPreview = Encoding.UTF8.GetBytes(preview);
        fixed (byte* labelPointer = encodedLabel)
        fixed (byte* previewPointer = encodedPreview)
        {
            return _api->BeginCombo(
                _api->Context,
                labelPointer, checked((uint)encodedLabel.Length),
                previewPointer, checked((uint)encodedPreview.Length),
                (uint)flags) != 0;
        }
    }

    public void EndCombo()
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.EndCombo(); return; }
#endif
        if (_api != null && _api->EndCombo != null) _api->EndCombo(_api->Context);
    }

    public bool InputText(
        string label,
        ref string value,
        int maximumUtf8Bytes = 255,
        ImGuiInputTextFlags flags = ImGuiInputTextFlags.None)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
            return ImGui.InputText(
                label, ref value, checked((uint)maximumUtf8Bytes + 1),
                (ImGuiNET.ImGuiInputTextFlags)(uint)flags);
#endif
        if (_api == null || _api->InputText == null) return false;
        if (maximumUtf8Bytes is < 1 or > 4095)
            throw new ArgumentOutOfRangeException(nameof(maximumUtf8Bytes));
        var labelUtf8 = Encoding.UTF8.GetBytes(label);
        var buffer = new byte[maximumUtf8Bytes + 1];
        var text = value.AsSpan();
        while (!text.IsEmpty && Encoding.UTF8.GetByteCount(text) > maximumUtf8Bytes)
            text = text[..^1];
        Encoding.UTF8.GetBytes(text, buffer);
        fixed (byte* labelPointer = labelUtf8)
        fixed (byte* bufferPointer = buffer)
        {
            if (_api->InputText(
                    _api->Context, labelPointer, checked((uint)labelUtf8.Length),
                    bufferPointer, checked((uint)buffer.Length), (uint)flags) == 0)
                return false;
        }
        var length = Array.IndexOf(buffer, (byte)0);
        if (length < 0) length = buffer.Length;
        value = Encoding.UTF8.GetString(buffer, 0, length);
        return true;
    }

    public void SameLine()
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.SameLine(); return; }
#endif
        if (_api != null && _api->SameLine != null) _api->SameLine(_api->Context);
    }

    public void SeparatorText(string label)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.SeparatorText(label); return; }
#endif
        if (_api == null || _api->SeparatorText == null) return;
        var byteCount = Encoding.UTF8.GetByteCount(label);
        var utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(label, utf8);
        fixed (byte* pointer = utf8)
            _api->SeparatorText(_api->Context, pointer, (uint)utf8.Length);
    }

    public void SetNextWindowPosition(
        float x,
        float y,
        ImGuiCondition condition = ImGuiCondition.None,
        float pivotX = 0,
        float pivotY = 0)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
        {
            ImGui.SetNextWindowPos(
                new Vector2(x, y), (ImGuiNET.ImGuiCond)(uint)condition,
                new Vector2(pivotX, pivotY));
            return;
        }
#endif
        if (_api != null && _api->SetNextWindowPosition != null)
            _api->SetNextWindowPosition(
                _api->Context, x, y, (uint)condition, pivotX, pivotY);
    }

    public void SetNextWindowSize(
        float width,
        float height,
        ImGuiCondition condition = ImGuiCondition.None)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
        {
            ImGui.SetNextWindowSize(
                new Vector2(width, height), (ImGuiNET.ImGuiCond)(uint)condition);
            return;
        }
#endif
        if (_api != null && _api->SetNextWindowSize != null)
            _api->SetNextWindowSize(_api->Context, width, height, (uint)condition);
    }

    public bool BeginChild(
        string id,
        float width,
        float height,
        ImGuiChildFlags childFlags = ImGuiChildFlags.None,
        ImGuiWindowFlags windowFlags = ImGuiWindowFlags.None)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
            return ImGui.BeginChild(
                id, new Vector2(width, height),
                (ImGuiNET.ImGuiChildFlags)(uint)childFlags,
                (ImGuiNET.ImGuiWindowFlags)(uint)windowFlags);
#endif
        if (_api == null || _api->BeginChild == null) return false;
        var byteCount = Encoding.UTF8.GetByteCount(id);
        var utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(id, utf8);
        fixed (byte* pointer = utf8)
            return _api->BeginChild(
                _api->Context, pointer, (uint)utf8.Length, width, height,
                (uint)childFlags, (uint)windowFlags) != 0;
    }

    public void EndChild()
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.EndChild(); return; }
#endif
        if (_api != null && _api->EndChild != null) _api->EndChild(_api->Context);
    }

    public bool Selectable(
        string label,
        bool selected = false,
        ImGuiSelectableFlags flags = ImGuiSelectableFlags.None,
        float width = 0,
        float height = 0)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
            return ImGui.Selectable(
                label, selected, (ImGuiNET.ImGuiSelectableFlags)(uint)flags,
                new Vector2(width, height));
#endif
        if (_api == null || _api->Selectable == null) return false;
        var byteCount = Encoding.UTF8.GetByteCount(label);
        var utf8 = byteCount <= 512 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(label, utf8);
        fixed (byte* pointer = utf8)
            return _api->Selectable(
                _api->Context, pointer, (uint)utf8.Length, selected ? 1u : 0u,
                (uint)flags, width, height) != 0;
    }

    public void Separator()
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.Separator(); return; }
#endif
        if (_api != null && _api->Separator != null) _api->Separator(_api->Context);
    }

    public void Spacing()
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.Spacing(); return; }
#endif
        if (_api != null && _api->Spacing != null) _api->Spacing(_api->Context);
    }

    public bool SliderInt(
        string label,
        ref int value,
        int minimum,
        int maximum,
        string format = "%d")
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
            return ImGui.SliderInt(label, ref value, minimum, maximum, format);
#endif
        if (_api == null || _api->SliderInt == null) return false;
        var labelCount = Encoding.UTF8.GetByteCount(label);
        var formatCount = Encoding.UTF8.GetByteCount(format);
        var labelUtf8 = labelCount <= 512 ? stackalloc byte[labelCount] : new byte[labelCount];
        var formatUtf8 = formatCount <= 64 ? stackalloc byte[formatCount] : new byte[formatCount];
        Encoding.UTF8.GetBytes(label, labelUtf8);
        Encoding.UTF8.GetBytes(format, formatUtf8);
        fixed (byte* labelPointer = labelUtf8)
        fixed (byte* formatPointer = formatUtf8)
        fixed (int* valuePointer = &value)
            return _api->SliderInt(
                _api->Context, labelPointer, (uint)labelUtf8.Length, valuePointer,
                minimum, maximum, formatPointer, (uint)formatUtf8.Length) != 0;
    }

    public void SetNextItemWidth(float width)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed) { ImGui.SetNextItemWidth(width); return; }
#endif
        if (_api != null && _api->SetNextItemWidth != null)
            _api->SetNextItemWidth(_api->Context, width);
    }

    public void TextColored(uint rgba, string text)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
        {
            ImGui.TextColored(ToVector4(rgba), text);
            return;
        }
#endif
        if (_api == null || _api->TextColored == null) return;
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var utf8 = byteCount <= 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(text, utf8);
        fixed (byte* pointer = utf8)
            _api->TextColored(_api->Context, rgba, pointer, (uint)utf8.Length);
    }

    public float Framerate
    {
        get
        {
#if !BRIEFCASE_HEADLESS
            if (_managed) return ImGui.GetIO().Framerate;
#endif
            return _api != null && _api->GetFramerate != null
                ? _api->GetFramerate(_api->Context)
                : 0.0f;
        }
    }

    public void DrawCircle(
        float x,
        float y,
        float radius,
        uint rgba,
        int segments = 0,
        float thickness = 1.0f,
        bool filled = false)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
        {
            var draw = ImGui.GetForegroundDrawList();
            if (filled) draw.AddCircleFilled(new Vector2(x, y), radius, rgba, segments);
            else draw.AddCircle(new Vector2(x, y), radius, rgba, segments, thickness);
            return;
        }
#endif
        if (_api != null && _api->DrawCircle != null)
            _api->DrawCircle(
                _api->Context, x, y, radius, rgba, segments, thickness, filled ? 1u : 0u);
    }

    public void DrawLine(
        float x1,
        float y1,
        float x2,
        float y2,
        uint rgba,
        float thickness = 1.0f)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
        {
            ImGui.GetForegroundDrawList().AddLine(
                new Vector2(x1, y1), new Vector2(x2, y2), rgba, thickness);
            return;
        }
#endif
        if (_api != null && _api->DrawLine != null)
            _api->DrawLine(_api->Context, x1, y1, x2, y2, rgba, thickness);
    }

    public void DrawRectFilled(
        float minimumX,
        float minimumY,
        float maximumX,
        float maximumY,
        uint rgba,
        float rounding = 0)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
        {
            ImGui.GetForegroundDrawList().AddRectFilled(
                new Vector2(minimumX, minimumY), new Vector2(maximumX, maximumY),
                rgba, rounding);
            return;
        }
#endif
        if (_api != null && _api->DrawRectFilled != null)
            _api->DrawRectFilled(
                _api->Context, minimumX, minimumY, maximumX, maximumY, rgba, rounding);
    }

    public void DrawText(float x, float y, uint rgba, string text)
    {
#if !BRIEFCASE_HEADLESS
        if (_managed)
        {
            ImGui.GetForegroundDrawList().AddText(new Vector2(x, y), rgba, text);
            return;
        }
#endif
        if (_api == null || _api->DrawText == null) return;
        var byteCount = Encoding.UTF8.GetByteCount(text);
        var utf8 = byteCount <= 1024 ? stackalloc byte[byteCount] : new byte[byteCount];
        Encoding.UTF8.GetBytes(text, utf8);
        fixed (byte* pointer = utf8)
            _api->DrawText(
                _api->Context, x, y, rgba, pointer, (uint)utf8.Length);
    }

#if !BRIEFCASE_HEADLESS
    private static Vector4 ToVector4(uint rgba) => new(
        (rgba & 0xff) / 255.0f,
        ((rgba >> 8) & 0xff) / 255.0f,
        ((rgba >> 16) & 0xff) / 255.0f,
        ((rgba >> 24) & 0xff) / 255.0f);
#endif
}

[Flags]
public enum ImGuiWindowFlags : uint
{
    None = 0,
    NoTitleBar = 1u << 0,
    NoResize = 1u << 1,
    NoMove = 1u << 2,
    NoScrollbar = 1u << 3,
    NoCollapse = 1u << 5,
    NoSavedSettings = 1u << 8
}

[Flags]
public enum ImGuiChildFlags : uint
{
    None = 0,
    Borders = 1u << 0,
    AlwaysUseWindowPadding = 1u << 1,
    ResizeX = 1u << 2,
    ResizeY = 1u << 3
}

[Flags]
public enum ImGuiSelectableFlags : uint
{
    None = 0,
    DontClosePopups = 1u << 0,
    SpanAllColumns = 1u << 1,
    AllowDoubleClick = 1u << 2,
    Disabled = 1u << 3
}

[Flags]
public enum ImGuiComboFlags : uint
{
    None = 0,
    PopupAlignLeft = 1u << 0,
    HeightSmall = 1u << 1,
    HeightRegular = 1u << 2,
    HeightLarge = 1u << 3,
    HeightLargest = 1u << 4,
    NoArrowButton = 1u << 5,
    NoPreview = 1u << 6,
    WidthFitPreview = 1u << 7
}

[Flags]
public enum ImGuiInputTextFlags : uint
{
    None = 0,
    Password = 1u << 15
}

[Flags]
public enum ImGuiCondition : uint
{
    None = 0,
    Always = 1u << 0,
    Once = 1u << 1,
    FirstUseEver = 1u << 2,
    Appearing = 1u << 3
}

public static class ImGuiColor
{
    // Dear ImGui stores colors as AABBGGRR on little-endian Windows builds.
    public static uint Rgba(byte red, byte green, byte blue, byte alpha = 255) =>
        red | ((uint)green << 8) | ((uint)blue << 16) | ((uint)alpha << 24);
}
