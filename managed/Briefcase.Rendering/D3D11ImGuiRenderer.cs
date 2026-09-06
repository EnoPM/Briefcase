using System.Numerics;
using ImGuiNET;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace Briefcase.Rendering;

/// <summary>
/// Translates ImGui draw lists into Direct3D 11 commands. ImGui produces CPU
/// vertices and indices; this class uploads them and configures the small GPU
/// pipeline needed to draw the resulting triangles.
/// </summary>
internal sealed unsafe class D3D11ImGuiRenderer : IDisposable
{
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private ID3D11Buffer? _vertexBuffer;
    private ID3D11Buffer? _indexBuffer;
    private ID3D11Buffer? _constantBuffer;
    private ID3D11VertexShader? _vertexShader;
    private ID3D11PixelShader? _pixelShader;
    private ID3D11InputLayout? _inputLayout;
    private ID3D11SamplerState? _fontSampler;
    private ID3D11ShaderResourceView? _fontTexture;
    private ID3D11RasterizerState? _rasterizerState;
    private ID3D11BlendState? _blendState;
    private ID3D11DepthStencilState? _depthStencilState;
    private int _vertexCapacity = 5_000;
    private int _indexCapacity = 10_000;

    public D3D11ImGuiRenderer(ID3D11Device device, ID3D11DeviceContext context)
    {
        _device = device;
        _context = context;
        ImGui.GetIO().BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
        CreateDeviceObjects();
    }

    public void Render(ImDrawDataPtr drawData)
    {
        if (drawData.DisplaySize.X <= 0 || drawData.DisplaySize.Y <= 0 ||
            drawData.TotalVtxCount == 0) return;

        EnsureBuffers(drawData.TotalVtxCount, drawData.TotalIdxCount);
        UploadDrawData(drawData);
        UploadProjection(drawData);
        SetupRenderState(drawData);

        var globalIndexOffset = 0;
        var globalVertexOffset = 0;
        var clipOffset = drawData.DisplayPos;
        for (var listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            var list = drawData.CmdLists[listIndex];
            for (var commandIndex = 0; commandIndex < list.CmdBuffer.Size; commandIndex++)
            {
                var command = list.CmdBuffer[commandIndex];
                if (command.UserCallback != 0)
                    continue;

                var left = (int)(command.ClipRect.X - clipOffset.X);
                var top = (int)(command.ClipRect.Y - clipOffset.Y);
                var right = (int)(command.ClipRect.Z - clipOffset.X);
                var bottom = (int)(command.ClipRect.W - clipOffset.Y);
                if (right <= left || bottom <= top) continue;

                _context.RSSetScissorRect(left, top, right, bottom);
                if (_fontTexture is not null && command.TextureId == _fontTexture.NativePointer)
                    _context.PSSetShaderResource(0, _fontTexture);
                _context.DrawIndexed(
                    command.ElemCount,
                    checked(command.IdxOffset + (uint)globalIndexOffset),
                    checked((int)command.VtxOffset + globalVertexOffset));
            }
            globalIndexOffset += list.IdxBuffer.Size;
            globalVertexOffset += list.VtxBuffer.Size;
        }
    }

    public void Dispose()
    {
        _depthStencilState?.Dispose();
        _blendState?.Dispose();
        _rasterizerState?.Dispose();
        _fontTexture?.Dispose();
        _fontSampler?.Dispose();
        _inputLayout?.Dispose();
        _pixelShader?.Dispose();
        _vertexShader?.Dispose();
        _constantBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _vertexBuffer?.Dispose();
    }

    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        if (_vertexBuffer is null || _vertexCapacity < vertexCount)
        {
            _vertexBuffer?.Dispose();
            _vertexCapacity = vertexCount + 5_000;
            _vertexBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = checked((uint)(_vertexCapacity * sizeof(ImDrawVert))),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.VertexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
        }
        if (_indexBuffer is null || _indexCapacity < indexCount)
        {
            _indexBuffer?.Dispose();
            _indexCapacity = indexCount + 10_000;
            _indexBuffer = _device.CreateBuffer(new BufferDescription
            {
                ByteWidth = checked((uint)(_indexCapacity * sizeof(ushort))),
                Usage = ResourceUsage.Dynamic,
                BindFlags = BindFlags.IndexBuffer,
                CPUAccessFlags = CpuAccessFlags.Write
            });
        }
    }

    private void UploadDrawData(ImDrawDataPtr drawData)
    {
        var vertices = _context.Map(
            _vertexBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        var indices = _context.Map(
            _indexBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        var vertexDestination = (ImDrawVert*)vertices.DataPointer;
        var indexDestination = (ushort*)indices.DataPointer;
        for (var index = 0; index < drawData.CmdListsCount; index++)
        {
            var list = drawData.CmdLists[index];
            var vertexBytes = checked(list.VtxBuffer.Size * sizeof(ImDrawVert));
            var indexBytes = checked(list.IdxBuffer.Size * sizeof(ushort));
            Buffer.MemoryCopy((void*)list.VtxBuffer.Data, vertexDestination, vertexBytes, vertexBytes);
            Buffer.MemoryCopy((void*)list.IdxBuffer.Data, indexDestination, indexBytes, indexBytes);
            vertexDestination += list.VtxBuffer.Size;
            indexDestination += list.IdxBuffer.Size;
        }
        _context.Unmap(_vertexBuffer!, 0);
        _context.Unmap(_indexBuffer!, 0);
    }

    private void UploadProjection(ImDrawDataPtr drawData)
    {
        var mapped = _context.Map(
            _constantBuffer!, 0, MapMode.WriteDiscard, Vortice.Direct3D11.MapFlags.None);
        var left = drawData.DisplayPos.X;
        var right = drawData.DisplayPos.X + drawData.DisplaySize.X;
        var top = drawData.DisplayPos.Y;
        var bottom = drawData.DisplayPos.Y + drawData.DisplaySize.Y;
        var matrix = new Span<float>((void*)mapped.DataPointer, 16);
        float[] values =
        [
            2.0f / (right - left), 0, 0, 0,
            0, 2.0f / (top - bottom), 0, 0,
            0, 0, 0.5f, 0,
            (right + left) / (left - right), (top + bottom) / (bottom - top), 0.5f, 1
        ];
        values.CopyTo(matrix);
        _context.Unmap(_constantBuffer!, 0);
    }

    private void SetupRenderState(ImDrawDataPtr drawData)
    {
        _context.RSSetViewport(new Viewport(0, 0, drawData.DisplaySize.X, drawData.DisplaySize.Y));
        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffer(0, _vertexBuffer!, (uint)sizeof(ImDrawVert), 0);
        _context.IASetIndexBuffer(_indexBuffer!, Format.R16_UInt, 0);
        _context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffer(0, _constantBuffer);
        _context.PSSetShader(_pixelShader);
        _context.PSSetSampler(0, _fontSampler);
        _context.GSSetShader(null);
        _context.HSSetShader(null);
        _context.DSSetShader(null);
        _context.CSSetShader(null);
        _context.OMSetBlendState(_blendState);
        _context.OMSetDepthStencilState(_depthStencilState);
        _context.RSSetState(_rasterizerState);
    }

    private void CreateDeviceObjects()
    {
        const string vertexSource = """
            cbuffer vertexBuffer : register(b0) { float4x4 ProjectionMatrix; };
            struct VS_INPUT { float2 pos : POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
            struct PS_INPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
            PS_INPUT main(VS_INPUT input) {
                PS_INPUT output;
                output.pos = mul(ProjectionMatrix, float4(input.pos.xy, 0, 1));
                output.uv = input.uv;
                output.col = input.col;
                return output;
            }
            """;
        Compiler.Compile(
            vertexSource, "main", "Briefcase.ImGui.Vertex", "vs_4_0",
            out var vertexBlob, out var vertexErrors).CheckError();
        vertexErrors?.Dispose();
        using (vertexBlob ?? throw new InvalidOperationException("Could not compile the ImGui vertex shader."))
        {
            _vertexShader = _device.CreateVertexShader(vertexBlob, null);
            InputElementDescription[] elements =
            [
                new("POSITION", 0, Format.R32G32_Float, 0, 0),
                new("TEXCOORD", 0, Format.R32G32_Float, 8, 0),
                new("COLOR", 0, Format.R8G8B8A8_UNorm, 16, 0)
            ];
            _inputLayout = _device.CreateInputLayout(elements, vertexBlob);
        }

        _constantBuffer = _device.CreateBuffer(new BufferDescription
        {
            ByteWidth = 64,
            Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ConstantBuffer,
            CPUAccessFlags = CpuAccessFlags.Write
        });

        const string pixelSource = """
            struct PS_INPUT { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 col : COLOR0; };
            sampler sampler0;
            Texture2D texture0;
            float4 main(PS_INPUT input) : SV_Target {
                return input.col * texture0.Sample(sampler0, input.uv);
            }
            """;
        Compiler.Compile(
            pixelSource, "main", "Briefcase.ImGui.Pixel", "ps_4_0",
            out var pixelBlob, out var pixelErrors).CheckError();
        pixelErrors?.Dispose();
        using (pixelBlob ?? throw new InvalidOperationException("Could not compile the ImGui pixel shader."))
            _pixelShader = _device.CreatePixelShader(pixelBlob, null);

        var blend = new BlendDescription { AlphaToCoverageEnable = false };
        blend.RenderTarget[0] = new RenderTargetBlendDescription
        {
            BlendEnable = true,
            // DirectComposition consumes this swap chain as premultiplied alpha.
            // The RGB factors below premultiply ImGui's straight-alpha colors
            // while blending them over pixels already drawn in this frame:
            //     RGBout = RGBsrc * Asrc + RGBdst * (1 - Asrc)
            // Alpha must use the matching source-over equation. Using
            // InverseSourceAlpha for SourceBlendAlpha would produce
            // Asrc * (1 - Asrc), making nearly opaque window backgrounds
            // paradoxically almost transparent.
            SourceBlend = Blend.SourceAlpha,
            DestinationBlend = Blend.InverseSourceAlpha,
            BlendOperation = BlendOperation.Add,
            SourceBlendAlpha = Blend.One,
            DestinationBlendAlpha = Blend.InverseSourceAlpha,
            BlendOperationAlpha = BlendOperation.Add,
            RenderTargetWriteMask = ColorWriteEnable.All
        };
        _blendState = _device.CreateBlendState(blend);
        _rasterizerState = _device.CreateRasterizerState(new RasterizerDescription
        {
            FillMode = FillMode.Solid,
            CullMode = CullMode.None,
            ScissorEnable = true,
            DepthClipEnable = true
        });
        _depthStencilState = _device.CreateDepthStencilState(new DepthStencilDescription
        {
            DepthEnable = false,
            DepthWriteMask = DepthWriteMask.All,
            DepthFunc = ComparisonFunction.Always,
            StencilEnable = false
        });
        CreateFontTexture();
    }

    private void CreateFontTexture()
    {
        var fonts = ImGui.GetIO().Fonts;
        fonts.GetTexDataAsRGBA32(out byte* pixels, out var width, out var height);
        var description = new Texture2DDescription
        {
            Width = checked((uint)width),
            Height = checked((uint)height),
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource
        };
        var initial = new SubresourceData(pixels, checked((uint)width * 4), 0);
        using var texture = _device.CreateTexture2D(description, [initial]);
        _fontTexture = _device.CreateShaderResourceView(texture);
        fonts.SetTexID(_fontTexture.NativePointer);
        fonts.ClearTexData();

        _fontSampler = _device.CreateSamplerState(new SamplerDescription
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap,
            AddressV = TextureAddressMode.Wrap,
            AddressW = TextureAddressMode.Wrap,
            ComparisonFunc = ComparisonFunction.Always,
            MinLOD = 0,
            MaxLOD = 0
        });
    }
}
