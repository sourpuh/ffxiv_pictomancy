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

internal class Sphere : IDisposable
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
        public Vector3 Origin;
        public float Radius;
        public Vector4 Color;
        public float OccludedAlpha;
        public float OcclusionTolerance;
        public float FadeStart;
        public float FadeStop;
        public Vector3 FresnelParams;
    }

    private class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _spheres;

            internal Builder(RenderContext ctx, Data data)
            {
                _spheres = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _spheres.Dispose();
            }

            public void Add(Vector3 origin, float radius, Vector4 color, PctDxParams p) =>
                _spheres.Add(new Instance
                {
                    Origin = origin,
                    Radius = radius,
                    Color = color,
                    OccludedAlpha = p.OccludedAlpha,
                    OcclusionTolerance = p.OcclusionTolerance,
                    FadeStart = p.FadeStart,
                    FadeStop = p.FadeStop,
                    FresnelParams = new Vector3(p.FresnelSpread, p.FresnelIntensity, p.FresnelOpacity),
                });
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount, bool dynamic)
        {
            _buffer = new("Sphere", ctx, maxCount, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // Box per instance: 6 quads = 36 verts.
        public void DrawAll(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.DrawInstanced(36, _buffer.CurElements, 0, 0);
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

    public Sphere(RenderContext ctx, int maxSpheres)
    {
        _ctx = ctx;
        _data = new(ctx, maxSpheres, true);
        var shader = ShapeSharedShader.Preamble + """
            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float4x4 invViewProj;
                float2 renderTargetSize;
                float2 pixelToUv;
                float3 cameraPos;
            };
            """ + ShapeSharedShader.Mixin + """
            struct Sphere
            {
                float3 origin : WORLD;
                float radius : RADIUS;
                float4 color : COLOR;
                float2 occlusionParams : OCCLUSIONPARAMS;
                float2 fadeParams : FADEPARAMS;
                // x = power, y = rgb intensity, z = alpha intensity.
                float3 fresnelParams : FRESNELPARAMS;
            };

            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                nointerpolation float3 origin : WORLD;
                nointerpolation float radius : RADIUS;
                nointerpolation float4 color : COLOR;
                nointerpolation float2 occlusionParams : OCCLUSIONPARAMS;
                nointerpolation float2 fadeParams : FADEPARAMS;
                nointerpolation float3 fresnelParams : FRESNELPARAMS;
            };

            static const uint BOX_LIST[36] = {
                3, 7, 5,  3, 5, 1, // +X face
                6, 2, 0,  6, 0, 4, // -X face
                6, 7, 3,  6, 3, 2, // +Y face
                5, 4, 0,  5, 0, 1, // -Y face
                7, 6, 4,  7, 4, 5, // +Z face
                2, 3, 1,  2, 1, 0, // -Z face
            };

            VSOutput vs(in Sphere instance, uint vertexId : SV_VERTEXID)
            {
                uint corner = BOX_LIST[vertexId];
                float3 sign = float3(
                    (corner & 1u) ? 1.0 : -1.0,
                    (corner & 2u) ? 1.0 : -1.0,
                    (corner & 4u) ? 1.0 : -1.0);
                float3 worldPos = instance.origin + sign * instance.radius;

                VSOutput o;
                o.projPos = mul(float4(worldPos, 1.0), viewProj);
                o.origin = instance.origin;
                o.radius = instance.radius;
                o.color = instance.color;
                o.occlusionParams = instance.occlusionParams;
                o.fadeParams = instance.fadeParams;
                o.fresnelParams = instance.fresnelParams;
                return o;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float2 ndcXY;
                ndcXY.x = input.projPos.x / renderTargetSize.x * 2.0 - 1.0;
                ndcXY.y = 1.0 - input.projPos.y / renderTargetSize.y * 2.0;
                float4 worldNearH = mul(float4(ndcXY, 1.0, 1.0), invViewProj);
                float3 worldNear = worldNearH.xyz / worldNearH.w;
                float4 worldFarH = mul(float4(ndcXY, SKY_DEPTH_EPS, 1.0), invViewProj);
                float3 worldFar = worldFarH.xyz / worldFarH.w;
                float3 rayDir = normalize(worldFar - worldNear);

                float2 uv = input.projPos.xy * pixelToUv;
                float sceneNdcZ = _sceneDepth.Sample(_sceneSampler, uv).r;
                float4 sceneH = mul(float4(ndcXY, sceneNdcZ, 1.0), invViewProj);
                float3 scenePos = sceneH.xyz / sceneH.w;
                float sceneSphereSdf = length(scenePos - input.origin) - input.radius;

                const float PROTRUDE_BAND_FRAC = 0.15;
                const float PROTRUDE_RIM_SCALE = 0.5;
                float protrudeBand = max(input.radius * PROTRUDE_BAND_FRAC, DIVIDE_EPS);
                float protrudeFalloff = saturate(1.0 - abs(sceneSphereSdf) / protrudeBand);

                bool shadeAsScene = sceneSphereSdf < 0;
                float3 hit;
                float hitNdcZ;
                float3 N;
                float coverage = 1.0;
                if (shadeAsScene)
                {
                    hit = scenePos;
                    hitNdcZ = sceneNdcZ;
                    N = normalize(_sceneNormal.Sample(_sceneSampler, uv).rgb * 2.0 - 1.0);
                }
                else
                {
                    float3 oc = worldNear - input.origin;
                    float b = dot(oc, rayDir);
                    float c = dot(oc, oc) - input.radius * input.radius;
                    float disc = b * b - c;

                    float silhouette = sqrt(max(dot(oc, oc) - b * b, 0.0)) - input.radius;
                    float aa = fwidth(silhouette);
                    coverage = 1.0 - smoothstep(-aa, aa, silhouette);
                    if (coverage <= 0.0) discard;

                    float sqrtDisc = sqrt(max(disc, 0.0));
                    float tNear = -b - sqrtDisc;
                    float tFar  = -b + sqrtDisc;
                    float tHit = tNear >= 0 ? tNear : tFar;
                    if (tHit < 0) discard;

                    hit = worldNear + tHit * rayDir;
                    float4 hitClip = mul(float4(hit, 1.0), viewProj);
                    hitNdcZ = hitClip.z / hitClip.w;
                    N = normalize(hit - input.origin);
                }

                // Protrude rim feeds into the shared fresnel as extraRim and same intensity/opacity knobs as the angular component.
                float spread = input.fresnelParams.x;
                float protrudeRim = (spread > 0.0) ? pow(protrudeFalloff, 1.0 / spread) * PROTRUDE_RIM_SCALE : 0.0;
                float4 color = applyFresnelRim(input.color, N, -rayDir, input.fresnelParams, protrudeRim);
                color.a *= coverage;

                if (shadeAsScene)
                {
                    return applyDistanceFade(color, hitNdcZ, input.fadeParams);
                }
                return applyShared(color, float3(input.projPos.xy, hitNdcZ), input.occlusionParams, input.fadeParams);
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        if (vs.HasErrors || vs.Bytecode == null)
            throw new InvalidOperationException($"Sphere VS compile failed:\n{vs.Message}");
        PctService.Log.Debug($"Sphere VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        if (ps.HasErrors || ps.Bytecode == null)
            throw new InvalidOperationException($"Sphere PS compile failed:\n{ps.Message}");
        PctService.Log.Debug($"Sphere PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, DXRenderer.AlignTo16<Constants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("RADIUS", 0, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("COLOR", 0, Format.R32G32B32A32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("OCCLUSIONPARAMS", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
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

    public void Add(Vector3 origin, float radius, uint color, PctDxParams p)
    {
        (_builder ??= _data.Map(_ctx)).Add(origin, radius, color.ToVector4(), p);
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
        _ctx.Context.Rasterizer.State = _rs;
    }
}
