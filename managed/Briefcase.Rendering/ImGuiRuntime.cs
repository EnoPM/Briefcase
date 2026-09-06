using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using ImGuiNET;

namespace Briefcase.Rendering;

/// <summary>
/// Owns the Dear ImGui context used by Briefcase.
///
/// Context ownership belongs to managed code so mods can use ImGui.NET directly.
/// The platform and renderer backends will feed input into this context and render
/// the <see cref="ImDrawDataPtr"/> produced by <see cref="EndFrame"/>.
/// </summary>
public sealed unsafe class ImGuiRuntime : IDisposable
{
    private nint _context;
    private nint _fontMemory;
    private nint _glyphRangesMemory;
    private bool _frameStarted;

    public bool IsInitialized => _context != 0;

    public void Initialize()
    {
        if (_context != 0)
            throw new InvalidOperationException("The ImGui context is already initialized.");

        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        ImGui.StyleColorsDark();

        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;
        io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;
        io.NativePtr->IniFilename = null;
        io.NativePtr->LogFilename = null;
        LoadInterFont(io);
    }

    public void BeginFrame(Vector2 displaySize, float deltaSeconds)
    {
        EnsureInitialized();

        if (_frameStarted)
        {
            throw new InvalidOperationException("An ImGui frame is already in progress.");
        }

        if (displaySize.X <= 0 || displaySize.Y <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(displaySize));
        }

        ImGui.SetCurrentContext(_context);
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = displaySize;
        io.DeltaTime = Math.Max(deltaSeconds, 1.0f / 1000.0f);

        ImGui.NewFrame();
        _frameStarted = true;
    }

    public ImDrawDataPtr EndFrame()
    {
        EnsureInitialized();

        if (!_frameStarted)
        {
            throw new InvalidOperationException("No ImGui frame is in progress.");
        }

        ImGui.SetCurrentContext(_context);
        ImGui.Render();
        _frameStarted = false;
        return ImGui.GetDrawData();
    }

    public void Dispose()
    {
        if (_context == 0)
        {
            return;
        }

        ImGui.SetCurrentContext(_context);
        if (_frameStarted)
        {
            ImGui.EndFrame();
            _frameStarted = false;
        }

        ImGui.DestroyContext(_context);
        _context = 0;
        if (_fontMemory != 0)
        {
            Marshal.FreeHGlobal(_fontMemory);
            _fontMemory = 0;
        }
        if (_glyphRangesMemory != 0)
        {
            Marshal.FreeHGlobal(_glyphRangesMemory);
            _glyphRangesMemory = 0;
        }
        GC.SuppressFinalize(this);
    }

    private void EnsureInitialized()
    {
        ObjectDisposedException.ThrowIf(_context == 0, this);
    }

    private void LoadInterFont(ImGuiIOPtr io)
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Briefcase.Rendering.Inter.ttf");
        if (stream is null || stream.Length is <= 0 or > int.MaxValue)
        {
            io.Fonts.AddFontDefault();
            return;
        }

        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        _fontMemory = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, _fontMemory, bytes.Length);

        var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
        try
        {
            // The managed runtime owns this allocation until the ImGui context
            // is destroyed, so the native font atlas must not free it.
            config.FontDataOwnedByAtlas = false;
            config.OversampleH = 2;
            config.OversampleV = 2;
            config.PixelSnapH = false;

            // ImGui's default range ends at U+00FF. Briefcase UI also uses
            // Latin Extended-A names (for example Hyō) and General Punctuation
            // characters such as the em dash and narrow no-break space.
            // ImGui retains this pointer until the atlas is built, so keep the
            // small unmanaged array alive for the lifetime of the context.
            _glyphRangesMemory = Marshal.AllocHGlobal(sizeof(ushort) * 7);
            var glyphRanges = new Span<ushort>((void*)_glyphRangesMemory, 7);
            glyphRanges[0] = 0x0020;
            glyphRanges[1] = 0x00FF;
            glyphRanges[2] = 0x0100;
            glyphRanges[3] = 0x017F;
            glyphRanges[4] = 0x2000;
            glyphRanges[5] = 0x206F;
            glyphRanges[6] = 0;
            io.NativePtr->FontDefault = io.Fonts.AddFontFromMemoryTTF(
                _fontMemory, bytes.Length, 18.0f, config,
                _glyphRangesMemory).NativePtr;
        }
        finally
        {
            ImGuiNative.ImFontConfig_destroy(config.NativePtr);
        }
    }
}
