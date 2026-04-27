using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;

namespace Pictomancy.DXDraw;

// Per-instance screen-space rectangle. Drawn with DSV-only target + stencil-replace state to
// mark clipped regions; subsequent shape draws use a stencil-equal-zero test to skip those pixels.
internal class ClipZone : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Vector2 ViewportSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector2 Min;
        public Vector2 Max;
    }

    private class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _rects;

            internal Builder(RenderContext ctx, Data data)
            {
                _rects = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _rects.Dispose();
            }

            public void Add(Vector2 min, Vector2 max) =>
                _rects.Add(new Instance() { Min = min, Max = max });
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount, bool dynamic)
        {
            _buffer = new("ClipZone", ctx, maxCount, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        public void DrawAll(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            // 4 verts per rect (TriangleStrip), one instance per rect.
            ctx.Context.DrawInstanced(4, _buffer.CurElements, 0, 0);
        }
    }

    private readonly RenderContext _ctx;
    private readonly Data _data;
    private Data.Builder? _builder;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly InputLayout _il;
    private readonly VertexShader _vs;
    private readonly PixelShader _ps;

    public bool HasPending => _builder != null;

    public ClipZone(RenderContext ctx, int maxRects)
    {
        _ctx = ctx;
        _data = new(ctx, maxRects, true);
        var shader = """
            cbuffer Constants : register(b0)
            {
                float2 viewportSize;
            };

            struct Rect
            {
                float2 rmin : RECTMIN;
                float2 rmax : RECTMAX;
            };

            struct VSOutput
            {
                float4 pos : SV_POSITION;
            };

            VSOutput vs(in Rect r, uint vid : SV_VertexID)
            {
                VSOutput o;
                // 0:(min,min) 1:(max,min) 2:(min,max) 3:(max,max) — TriangleStrip order.
                float2 px = float2(
                    (vid & 1) ? r.rmax.x : r.rmin.x,
                    (vid & 2) ? r.rmax.y : r.rmin.y);
                float2 ndc = float2(
                    px.x / viewportSize.x * 2.0 - 1.0,
                    1.0 - px.y / viewportSize.y * 2.0);
                o.pos = float4(ndc, 0, 1);
                return o;
            }

            void ps(VSOutput input)
            {
                // Stencil writes happen via the depth-stencil state; PS body intentionally empty.
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        PctService.Log.Debug($"ClipZone VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        PctService.Log.Debug($"ClipZone PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("RECTMIN", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("RECTMAX", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
        ]);
    }

    public void Dispose()
    {
        _builder?.Dispose();
        _data.Dispose();
        _constantBuffer.Dispose();
        _il.Dispose();
        _vs.Dispose();
        _ps.Dispose();
    }

    public void UpdateConstants(Constants consts)
    {
        _ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Add(Vector2 min, Vector2 max)
    {
        (_builder ??= _data.Map(_ctx)).Add(min, max);
    }

    public void Flush()
    {
        if (_builder == null) return;
        _builder.Dispose();
        _builder = null;
        Bind();
        _data.DrawAll(_ctx);
    }

    /// <summary>Drop any queued rects without drawing them. Use when the depth path is unavailable.</summary>
    public void Discard()
    {
        _builder?.Dispose();
        _builder = null;
    }

    private void Bind()
    {
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        _ctx.Context.InputAssembler.InputLayout = _il;
        _ctx.Context.VertexShader.Set(_vs);
        _ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.Set(_ps);
        _ctx.Context.GeometryShader.Set(null);
    }
}
