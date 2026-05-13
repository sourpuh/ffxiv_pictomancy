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

internal class ProjectedTriFill : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
        public Matrix InvViewProj;
        public Vector2 RenderTargetSize;
        public Vector2 PixelToUv;
        public Vector3 CameraPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector3 A;
        public Vector3 B;
        public Vector3 C;
        public Vector4 ColorA;
        public Vector4 ColorB;
        public Vector4 ColorC;
        // x/y/z toggles AA on the edge opposite (BC/AC/AB); 0 = hard cut, 1 = fwidth fade.
        public Vector3 AaMask;
        public float ProjectionHeight;
        public float FadeStart;
        public float FadeStop;
        public Vector3 FresnelParams;
    }

    private class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _tris;

            internal Builder(RenderContext ctx, Data data)
            {
                _tris = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _tris.Dispose();
            }

            public void Add(Vector3 a, Vector3 b, Vector3 c, Vector4 colorA, Vector4 colorB, Vector4 colorC, Vector3 aaMask, PctDxParams p)
            {
                // Winding normalization
                float signed2D = (b.X - a.X) * (c.Z - a.Z) - (b.Z - a.Z) * (c.X - a.X);
                if (signed2D < 0f)
                {
                    (b, c) = (c, b);
                    (colorB, colorC) = (colorC, colorB);
                    aaMask = new Vector3(aaMask.X, aaMask.Z, aaMask.Y);
                }
                _tris.Add(new Instance()
                {
                    A = a,
                    B = b,
                    C = c,
                    ColorA = colorA,
                    ColorB = colorB,
                    ColorC = colorC,
                    AaMask = aaMask,
                    ProjectionHeight = p.ProjectionHeight,
                    FadeStart = p.FadeStart,
                    FadeStop = p.FadeStop,
                    FresnelParams = new Vector3(p.FresnelSpread, p.FresnelIntensity, p.FresnelOpacity),
                });
            }
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount, bool dynamic)
        {
            _buffer = new("ProjectedTri", ctx, maxCount, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // Prism per instance: 8 triangles = 24 verts.
        public void DrawRange(RenderContext ctx, int start, int instanceCount)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.DrawInstanced(24, instanceCount, 0, start);
        }
    }

    private readonly RenderContext _ctx;
    private readonly Data _data;
    private Data.Builder? _builder;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly InputLayout _il;
    private readonly VertexShader _vs;
    private readonly PixelShader _ps;
    private readonly RasterizerState _rs;

    public bool HasPending => _builder != null;

    public ProjectedTriFill(RenderContext ctx, int maxTriangles)
    {
        _ctx = ctx;
        _data = new(ctx, maxTriangles, true);
        var shader = ShapeSharedShader.Preamble + """
            // Bary denominator threshold; below this the triangle is degenerate on the XZ plane.
            static const float DEGENERATE_DENOM_EPS = 1e-10;
            // fwidth cap to prevent depth-discontinuity fringing.
            static const float BARY_AA_CAP = 0.1;

            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float4x4 invViewProj;
                float2 renderTargetSize;
                float2 pixelToUv;
                float3 cameraPos;
            };
            """ + ShapeSharedShader.Mixin + ShapeSharedShader.ProjectedMixin + """
            struct Tri
            {
                float3 a : WORLD0;
                float3 b : WORLD1;
                float3 c : WORLD2;
                float4 colorA : INSTANCECOLOR0;
                float4 colorB : INSTANCECOLOR1;
                float4 colorC : INSTANCECOLOR2;
                float3 aaMask : AAMASK;
                float projectionHeight : HEIGHT;
                float2 fadeParams : FADEPARAMS;
                float3 fresnelParams : FRESNELPARAMS;
            };

            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                nointerpolation float3 a : WORLD0;
                nointerpolation float3 b : WORLD1;
                nointerpolation float3 c : WORLD2;
                nointerpolation float2 yRange : YRANGE;
                nointerpolation float4 colorA : COLOR0;
                nointerpolation float4 colorB : COLOR1;
                nointerpolation float4 colorC : COLOR2;
                nointerpolation float3 aaMask : AAMASK;
                nointerpolation float2 fadeParams : FADEPARAMS;
                nointerpolation float3 fresnelParams : FRESNELPARAMS;
            };

            static const uint PRISM_LIST[24] = {
                3, 5, 4,           // +Y cap
                0, 1, 2,           // -Y cap
                3, 4, 1,  3, 1, 0, // side AB
                4, 5, 2,  4, 2, 1, // side BC
                5, 3, 0,  5, 0, 2, // side CA
            };

            VSOutput vs(in Tri instance, uint vertexId : SV_VERTEXID)
            {
                uint corner = PRISM_LIST[vertexId];
                uint vIdx = corner % 3;
                bool top = corner >= 3;

                float yLo = min(min(instance.a.y, instance.b.y), instance.c.y) - instance.projectionHeight;
                float yHi = max(max(instance.a.y, instance.b.y), instance.c.y) + instance.projectionHeight;

                float3 v = (vIdx == 0) ? instance.a : ((vIdx == 1) ? instance.b : instance.c);
                float3 worldPos = float3(v.x, top ? yHi : yLo, v.z);

                VSOutput o;
                o.projPos = mul(float4(worldPos, 1.0), viewProj);
                o.a = instance.a;
                o.b = instance.b;
                o.c = instance.c;
                o.yRange = float2(yLo, yHi);
                o.colorA = instance.colorA;
                o.colorB = instance.colorB;
                o.colorC = instance.colorC;
                o.fadeParams = instance.fadeParams;
                o.aaMask = instance.aaMask;
                o.fresnelParams = instance.fresnelParams;
                return o;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float2 uv = input.projPos.xy * pixelToUv;
                float sceneNdcZ = sampleSceneNdcZ(uv);
                float3 world = getWorldPos(input.projPos.xy, sceneNdcZ);

                if (world.y < input.yRange.x || world.y > input.yRange.y) discard;

                // XZ point-in-triangle via barycentric coordinates.
                float2 v0 = input.b.xz - input.a.xz;
                float2 v1 = input.c.xz - input.a.xz;
                float2 v2 = world.xz   - input.a.xz;
                float d00 = dot(v0, v0);
                float d01 = dot(v0, v1);
                float d11 = dot(v1, v1);
                float denom = d00 * d11 - d01 * d01;
                if (abs(denom) < DEGENERATE_DENOM_EPS) discard; // degenerate triangle on XZ plane
                float invDenom = 1.0 / denom;
                float d20 = dot(v2, v0);
                float d21 = dot(v2, v1);
                float bv = (d11 * d20 - d01 * d21) * invDenom;
                float bw = (d00 * d21 - d01 * d20) * invDenom;
                float bu = 1.0 - bv - bw;

                // Y-edge fade
                float yMid = (input.yRange.x + input.yRange.y) * 0.5;
                float yHalfRange = (input.yRange.y - input.yRange.x) * 0.5;
                float distFromMid = abs(world.y - yMid) / max(yHalfRange, DIVIDE_EPS);
                float heightAlpha = saturate((1.0 - distFromMid) / HEIGHT_FADE_BAND);

                // Anti aliasing
                float du = min(fwidth(bu), BARY_AA_CAP);
                float dv = min(fwidth(bv), BARY_AA_CAP);
                float dw = min(fwidth(bw), BARY_AA_CAP);
                if (bu < -du * input.aaMask.x || bv < -dv * input.aaMask.y || bw < -dw * input.aaMask.z) discard;
                float edgeAlpha = lerp(1.0, smoothstep(-du, du, bu), input.aaMask.x)
                                * lerp(1.0, smoothstep(-dv, dv, bv), input.aaMask.y)
                                * lerp(1.0, smoothstep(-dw, dw, bw), input.aaMask.z);

                float4 color = input.colorA * bu + input.colorB * bv + input.colorC * bw;
                color.a *= edgeAlpha * heightAlpha;
                color = applyFresnelRim(color, uv, world, input.fresnelParams);
                color = applyDistanceFade(color, sceneNdcZ, input.fadeParams);

                return color;
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        if (vs.HasErrors || vs.Bytecode == null)
            throw new InvalidOperationException($"ProjectedTri VS compile failed:\n{vs.Message}");
        PctService.Log.Debug($"ProjectedTri VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        if (ps.HasErrors || ps.Bytecode == null)
            throw new InvalidOperationException($"ProjectedTri PS compile failed:\n{ps.Message}");
        PctService.Log.Debug($"ProjectedTri PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, DXRenderer.AlignTo16<Constants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("WORLD", 1, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("WORLD", 2, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("INSTANCECOLOR", 0, Format.R32G32B32A32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("INSTANCECOLOR", 1, Format.R32G32B32A32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("INSTANCECOLOR", 2, Format.R32G32B32A32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("AAMASK", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("HEIGHT", 0, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("FADEPARAMS", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("FRESNELPARAMS", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
        ]);

        var rsDesc = RasterizerStateDescription.Default();
        rsDesc.CullMode = CullMode.Back;
        _rs = new(ctx.Device, rsDesc);
    }

    public void Dispose()
    {
        _builder?.Dispose();
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
        consts.InvViewProj.Transpose();
        _ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Add(Vector3 a, Vector3 b, Vector3 c, uint colorA, uint colorB, uint colorC, Vector3 aaMask, PctDxParams p)
    {
        (_builder ??= _data.Map(_ctx)).Add(a, b, c, colorA.ToVector4(), colorB.ToVector4(), colorC.ToVector4(), aaMask, p);
    }

    public void EndBuilder()
    {
        _builder?.Dispose();
        _builder = null;
    }

    internal void FlushRange(int start, int count)
    {
        Bind();
        _data.DrawRange(_ctx, start, count);
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
        _ctx.Context.Rasterizer.State = _rs;
    }
}
