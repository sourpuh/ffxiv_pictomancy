using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Pictomancy.DXDraw;

internal class Sprite : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
        public Vector2 RenderTargetSize;
        public Vector2 PixelToUv;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector3 WorldPosition;
        public Vector2 Size;
        public Vector2 Offset;
        public float OccludedAlpha;
        public float OcclusionTolerance;
        public float FadeStart;
        public float FadeStop;
    }

    private struct Submission
    {
        public Instance Inst;
        public IntPtr NativePtr;
    }

    private class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _inner;
            internal Builder(RenderContext ctx, Data data) { _inner = data._buffer.Map(ctx); }
            public void Dispose() => _inner.Dispose();
            public void Add(Instance inst) => _inner.Add(ref inst);
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount)
        {
            _buffer = new("Sprite", ctx, maxCount, BindFlags.VertexBuffer, true);
        }

        public void Dispose() => _buffer.Dispose();
        public Builder Map(RenderContext ctx) => new(ctx, this);

        public void BindVertexBuffer(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
        }
    }

    private readonly List<Submission> _sprites = new();
    private readonly RenderContext _ctx;
    private readonly Data _data;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly InputLayout _il;
    private readonly VertexShader _vs;
    private readonly PixelShader _ps;
    private readonly RasterizerState _rs;

    public bool HasPending => _sprites.Count > 0;

    public Sprite(RenderContext ctx, int maxSprites)
    {
        _ctx = ctx;
        _data = new(ctx, maxSprites);
        var shader = ShapeSharedShader.Preamble + """
            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float2 renderTargetSize;
                float2 pixelToUv;
            };

            Texture2D<float4> _userTex : register(t3);
            SamplerState _userSampler
            {
                Filter = MIN_MAG_MIP_LINEAR;
                AddressU = CLAMP;
                AddressV = CLAMP;
            };

            struct SpriteInstance
            {
                float3 worldPosition : WORLD;
                float2 size : SIZE;
                float2 offset : OFFSET;
                float2 occlusionParams : OCCLUSIONPARAMS;
                float2 fadeParams : FADEPARAMS;
            };
            """ + ShapeSharedShader.Mixin + """
            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation float2 occlusionParams : OCCLUSIONPARAMS;
                nointerpolation float2 fadeParams : FADEPARAMS;
            };

            VSOutput vs(in SpriteInstance inst, uint vertexId : SV_VERTEXID)
            {
                float2 corner = float2(vertexId & 1u, (vertexId >> 1u) & 1u);
                float2 signedCorner = corner * 2.0 - 1.0;
                float2 halfSize = inst.size * 0.5;
                float2 pixelOffset = inst.offset + signedCorner * halfSize;

                float4 clipCenter = mul(float4(inst.worldPosition, 1.0), viewProj);

                VSOutput o;
                o.uv = corner;
                o.occlusionParams = inst.occlusionParams;
                o.fadeParams = inst.fadeParams;
                if (clipCenter.w <= 0.0)
                {
                    // discard vertex behind camera
                    o.projPos = float4(0.0, 0.0, -2.0, 1.0);
                    return o;
                }

                float2 ndcOffset = pixelOffset / renderTargetSize * float2(2.0, -2.0);
                float4 clipPos = clipCenter;
                clipPos.xy += ndcOffset * clipCenter.w;
                o.projPos = clipPos;
                return o;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float4 color = _userTex.Sample(_userSampler, input.uv);
                return applyShared(color, input.projPos.xyz, input.occlusionParams, input.fadeParams);
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        if (vs.HasErrors || vs.Bytecode == null)
            throw new InvalidOperationException($"Sprite VS compile failed:\n{vs.Message}");
        PctService.Log.Debug($"Sprite VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        if (ps.HasErrors || ps.Bytecode == null)
            throw new InvalidOperationException($"Sprite PS compile failed:\n{ps.Message}");
        PctService.Log.Debug($"Sprite PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, DXRenderer.AlignTo16<Constants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("SIZE", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("OFFSET", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("OCCLUSIONPARAMS", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("FADEPARAMS", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
        ]);

        var rsDesc = RasterizerStateDescription.Default();
        rsDesc.CullMode = CullMode.None;
        _rs = new(ctx.Device, rsDesc);
    }

    public void Dispose()
    {
        _data.Dispose();
        _constantBuffer.Dispose();
        _il.Dispose();
        _vs.Dispose();
        _ps.Dispose();
        _rs.Dispose();
    }

    public void UpdateConstants(Constants consts)
    {
        consts.ViewProj.Transpose();
        _ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Add(IntPtr nativePtr, Vector3 worldPosition, Vector2 screenSize, Vector2 offset, PctDxParams p)
    {
        if (nativePtr == IntPtr.Zero) return;
        _sprites.Add(new Submission
        {
            Inst = new Instance
            {
                WorldPosition = worldPosition,
                Size = screenSize,
                Offset = offset,
                OccludedAlpha = p.OccludedAlpha,
                OcclusionTolerance = p.OcclusionTolerance,
                FadeStart = p.FadeStart,
                FadeStop = p.FadeStop,
            },
            NativePtr = nativePtr,
        });
    }

    public void Flush(Vector3 cameraPos)
    {
        if (_sprites.Count == 0) return;
        _sprites.Sort((a, b) =>
        {
            float da = Vector3.DistanceSquared(a.Inst.WorldPosition, cameraPos);
            float db = Vector3.DistanceSquared(b.Inst.WorldPosition, cameraPos);
            return db.CompareTo(da);
        });

        using (var b = _data.Map(_ctx))
        {
            for (int i = 0; i < _sprites.Count; i++)
                b.Add(_sprites[i].Inst);
        }

        _data.BindVertexBuffer(_ctx);
        Bind();

        for (int i = 0; i < _sprites.Count; i++)
        {
            _ctx.Context.PixelShader.SetShaderResource(3, _ctx.GetUserSrv(_sprites[i].NativePtr));
            _ctx.Context.DrawInstanced(4, 1, 0, i);
        }

        _ctx.Context.PixelShader.SetShaderResource(3, null);
        _sprites.Clear();
    }

    private void Bind()
    {
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        _ctx.Context.InputAssembler.InputLayout = _il;
        _ctx.Context.VertexShader.Set(_vs);
        _ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.Set(_ps);
        _ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.GeometryShader.Set(null);
        _ctx.Context.Rasterizer.State = _rs;
    }
}
