using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Pictomancy.DXDraw;

internal class TriFill : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
        public Vector2 PixelToUv;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector3 Point;
        public Vector4 Color;
        public float OccludedAlpha;
        public float OcclusionTolerance;
        public float FadeStart;
        public float FadeStop;
    }

    private class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _points;

            internal Builder(RenderContext ctx, Data data)
            {
                _points = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _points.Dispose();
            }

            public void Add(ref Instance inst) => _points.Add(ref inst);
            public void Add(Vector3 world, Vector4 color, PctDxParams p) =>
                _points.Add(new Instance()
                {
                    Point = world,
                    Color = color,
                    OccludedAlpha = p.OccludedAlpha,
                    OcclusionTolerance = p.OcclusionTolerance,
                    FadeStart = p.FadeStart,
                    FadeStop = p.FadeStop,
                });
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount, bool dynamic)
        {
            _buffer = new("Triangle", ctx, maxCount, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // Draw* should be called after TriFill.Bind set up its state
        public void DrawSubset(RenderContext ctx, int firstPoint, int numPoints)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.Draw(numPoints, firstPoint);
        }

        public void DrawAll(RenderContext ctx) => DrawSubset(ctx, 0, _buffer.CurElements);
    }

    private readonly RenderContext _ctx;
    private readonly Data _data;
    private Data.Builder? _builder;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly InputLayout _il;
    private readonly VertexShader _vs;
    private readonly PixelShader _ps;

    public bool HasPending => _builder != null;

    public TriFill(RenderContext ctx, int maxVertices)
    {
        _ctx = ctx;
        _data = new(ctx, maxVertices, true);
        var shader = """
            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float2 pixelToUv;
            };
            """ + ShapeSharedShader.Mixin + """
            struct Point
            {
                float3 pos : WORLD;
                float4 color : COLOR;
                float4 fadeParams : FADEPARAMS;
            };

            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                float4 color : COLOR;
                float4 fadeParams : FADEPARAMS;
            };

            VSOutput vs(Point v)
            {
                VSOutput vs;
                vs.projPos = mul(float4(v.pos, 1), viewProj);
                vs.color = v.color;
                vs.fadeParams = v.fadeParams;
                return vs;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                return applyShared(input.color, input.projPos.xyz, input.fadeParams);
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        PctService.Log.Debug($"Point VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        PctService.Log.Debug($"Point PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, 16 * 5, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0),
            new InputElement("COLOR", 0, Format.R32G32B32A32_Float, -1, 0),
            new InputElement("FADEPARAMS", 0, Format.R32G32B32A32_Float, -1, 0),
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
        consts.ViewProj.Transpose();
        _ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Add(Vector3 a, Vector3 b, Vector3 c, uint colorA, uint colorB, uint colorC, PctDxParams p)
    {
        var b_ = _builder ??= _data.Map(_ctx);
        b_.Add(a, colorA.ToVector4(), p);
        b_.Add(b, colorB.ToVector4(), p);
        b_.Add(c, colorC.ToVector4(), p);
    }

    public void Flush()
    {
        if (_builder == null) return;
        _builder.Dispose();
        _builder = null;
        Bind();
        _data.DrawAll(_ctx);
    }

    private void Bind()
    {
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _ctx.Context.InputAssembler.InputLayout = _il;
        _ctx.Context.VertexShader.Set(_vs);
        _ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.Set(_ps);
        _ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.GeometryShader.Set(null);
    }
}
