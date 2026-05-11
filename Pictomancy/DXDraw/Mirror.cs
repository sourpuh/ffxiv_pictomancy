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

internal class Mirror : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
        public Vector2 PixelToUv;
        public float HasTexture;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex
    {
        public Vector3 Pos;
        public Vector2 Uv;
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
            private RenderBuffer<Vertex>.Builder _verts;

            internal Builder(RenderContext ctx, Data data)
            {
                _verts = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _verts.Dispose();
            }

            public void AddQuad(Vector3 bl, Vector3 br, Vector3 tr, Vector3 tl, Vector4 color, PctDxParams p)
            {
                // Two tris, CCW from the front (facing direction): BL, TL, TR  and  BL, TR, BR.
                Push(bl, new Vector2(0, 1), color, p);
                Push(tl, new Vector2(0, 0), color, p);
                Push(tr, new Vector2(1, 0), color, p);
                Push(bl, new Vector2(0, 1), color, p);
                Push(tr, new Vector2(1, 0), color, p);
                Push(br, new Vector2(1, 1), color, p);
            }

            private void Push(Vector3 pos, Vector2 uv, Vector4 color, PctDxParams p) =>
                _verts.Add(new Vertex
                {
                    Pos = pos,
                    Uv = uv,
                    Color = color,
                    OccludedAlpha = p.OccludedAlpha,
                    OcclusionTolerance = p.OcclusionTolerance,
                    FadeStart = p.FadeStart,
                    FadeStop = p.FadeStop,
                });
        }

        private readonly RenderBuffer<Vertex> _buffer;

        public Data(RenderContext ctx, int maxQuads, bool dynamic)
        {
            _buffer = new("Mirror", ctx, maxQuads * 6, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        public void DrawAll(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.Draw(_buffer.CurElements, 0);
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

    public Mirror(RenderContext ctx, int maxQuads)
    {
        _ctx = ctx;
        _data = new(ctx, maxQuads, true);
        var shader = ShapeSharedShader.Preamble + """
            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float2 pixelToUv;
                float hasTexture;
            };

            Texture2D<float4> _charaTex : register(t3);
            SamplerState _charaSampler
            {
                Filter = MIN_MAG_LINEAR_MIP_POINT;
                AddressU = CLAMP;
                AddressV = CLAMP;
            };
            """ + ShapeSharedShader.Mixin + """
            struct Vertex
            {
                float3 pos : WORLD;
                float2 uv : TEXCOORD;
                float4 color : COLOR;
                float2 occlusionParams : OCCLUSIONPARAMS;
                float2 fadeParams : FADEPARAMS;
            };

            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                float2 uv : TEXCOORD;
                float4 color : COLOR;
                float2 occlusionParams : OCCLUSIONPARAMS;
                float2 fadeParams : FADEPARAMS;
            };

            VSOutput vs(Vertex v)
            {
                VSOutput o;
                o.projPos = mul(float4(v.pos, 1), viewProj);
                // Horizontal mirror: flip U.
                o.uv = float2(1.0 - v.uv.x, v.uv.y);
                o.color = v.color;
                o.occlusionParams = v.occlusionParams;
                o.fadeParams = v.fadeParams;
                return o;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float4 sampled;
                if (hasTexture > 0.5)
                {
                    sampled = _charaTex.Sample(_charaSampler, input.uv);
                    sampled.a = 1.0;
                }
                else
                {
                    sampled = float4(1, 0, 1, 1);
                }
                float4 color = sampled * input.color;
                return applyShared(color, input.projPos.xyz, input.occlusionParams, input.fadeParams);
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        PctService.Log.Debug($"Mirror VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        PctService.Log.Debug($"Mirror PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, DXRenderer.AlignTo16<Constants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0),
            new InputElement("TEXCOORD", 0, Format.R32G32_Float, -1, 0),
            new InputElement("COLOR", 0, Format.R32G32B32A32_Float, -1, 0),
            new InputElement("OCCLUSIONPARAMS", 0, Format.R32G32_Float, -1, 0),
            new InputElement("FADEPARAMS", 0, Format.R32G32_Float, -1, 0),
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

    public void Add(Vector3 center, Vector3 facing, float width, float aspect, uint color, PctDxParams p)
    {
        var f = new Vector3(facing.X, 0, facing.Z);
        var len2 = f.LengthSquared();
        if (len2 < 1e-8f) return;
        f /= MathF.Sqrt(len2);

        var right = Vector3.Cross(Vector3.UnitY, f);
        var up = Vector3.UnitY;

        if (aspect <= 0) aspect = 1f;
        var height = width / aspect;

        var halfW = right * (width * 0.5f);
        var halfH = up * (height * 0.5f);

        var bl = center - halfW - halfH;
        var br = center + halfW - halfH;
        var tr = center + halfW + halfH;
        var tl = center - halfW + halfH;

        var b = _builder ??= _data.Map(_ctx);
        b.AddQuad(bl, br, tr, tl, color.ToVector4(), p);
    }

    public void Flush()
    {
        if (_builder == null) return;
        _builder.Dispose();
        _builder = null;
        Bind();
        _data.DrawAll(_ctx);
    }

    public void BindCharaTexture(ShaderResourceView? srv)
    {
        _ctx.Context.PixelShader.SetShaderResource(3, srv);
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
