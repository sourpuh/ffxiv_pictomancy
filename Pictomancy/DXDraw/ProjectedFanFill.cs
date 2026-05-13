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

internal class ProjectedFanFill : IDisposable
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
        public float InnerRadius;
        public float OuterRadius;
        public float MinAngle;
        public float MaxAngle;
        public Vector4 ColorOrigin;
        public Vector4 ColorEnd;
        public float ProjectionHeight;
        public float FadeStart;
        public float FadeStop;
        public Vector3 FresnelParams;
    }

    private class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _fans;

            internal Builder(RenderContext ctx, Data data)
            {
                _fans = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _fans.Dispose();
            }

            public void Add(Vector3 world, float innerRadius, float outerRadius, float minAngle, float maxAngle, Vector4 colorOrigin, Vector4 colorEnd, PctDxParams p) =>
                _fans.Add(new Instance()
                {
                    Origin = world,
                    InnerRadius = innerRadius,
                    OuterRadius = outerRadius,
                    MinAngle = minAngle,
                    MaxAngle = maxAngle,
                    ColorOrigin = colorOrigin,
                    ColorEnd = colorEnd,
                    ProjectionHeight = p.ProjectionHeight,
                    FadeStart = p.FadeStart,
                    FadeStop = p.FadeStop,
                    FresnelParams = new Vector3(p.FresnelSpread, p.FresnelIntensity, p.FresnelOpacity),
                });
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount, bool dynamic)
        {
            _buffer = new("ProjectedFan", ctx, maxCount, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // Box per instance: 6 quads = 36 verts.
        public void DrawRange(RenderContext ctx, int firstInstance, int instanceCount)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.DrawInstanced(36, instanceCount, 0, firstInstance);
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

    public ProjectedFanFill(RenderContext ctx, int maxFans)
    {
        _ctx = ctx;
        _data = new(ctx, maxFans, true);
        var shader = ShapeSharedShader.Preamble + """
            // Tolerance for "essentially full circle"; below this the angle AA pass is skipped.
            static const float FULL_CIRCLE_EPS = 1e-3;
            // fwidth cap (meters) to prevent depth-discontinuity fringing.
            static const float RADIUS_AA_CAP_M = 0.5;
            static const float ANGLE_AA_CAP_RAD = 0.1;

            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float4x4 invViewProj;
                float2 renderTargetSize;
                float2 pixelToUv;
                float3 cameraPos;
            };
            """ + ShapeSharedShader.Mixin + ShapeSharedShader.ProjectedMixin + """
            struct Fan
            {
                float3 origin : WORLD;
                float innerRadius : RADIUS0;
                float outerRadius : RADIUS1;
                float minAngle : ANGLE0;
                float maxAngle : ANGLE1;
                float4 colorOrigin : INSTANCECOLOR0;
                float4 colorEnd : INSTANCECOLOR1;
                float projectionHeight : HEIGHT;
                float2 fadeParams : FADEPARAMS;
                float3 fresnelParams : FRESNELPARAMS;
            };

            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                nointerpolation float3 origin : ORIGIN;
                nointerpolation float4 ranges : RANGES;
                nointerpolation float projectionHeight : HEIGHT;
                nointerpolation float4 colorOrigin : COLOR0;
                nointerpolation float4 colorEnd : COLOR1;
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

            VSOutput vs(in Fan instance, uint vertexId : SV_VERTEXID)
            {
                uint corner = BOX_LIST[vertexId];
                float3 sign = float3(
                    (corner & 1) ? 1.0 : -1.0,
                    (corner & 2) ? 1.0 : -1.0,
                    (corner & 4) ? 1.0 : -1.0);
                float3 worldPos = instance.origin + float3(
                    sign.x * instance.outerRadius,
                    sign.y * instance.projectionHeight,
                    sign.z * instance.outerRadius);

                VSOutput o;
                o.projPos = mul(float4(worldPos, 1.0), viewProj);
                o.origin = instance.origin;
                o.ranges = float4(instance.innerRadius, instance.outerRadius, instance.minAngle, instance.maxAngle);
                o.projectionHeight = instance.projectionHeight;
                o.colorOrigin = instance.colorOrigin;
                o.colorEnd = instance.colorEnd;
                o.fadeParams = instance.fadeParams;
                o.fresnelParams = instance.fresnelParams;
                return o;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float2 uv = input.projPos.xy * pixelToUv;
                float sceneNdcZ = sampleSceneNdcZ(uv);
                float3 world = getWorldPos(input.projPos.xy, sceneNdcZ);

                float3 local = world - input.origin;
                if (abs(local.y) > input.projectionHeight) discard;

                float r = sqrt(local.x * local.x + local.z * local.z);
                float innerR = input.ranges.x;
                float outerR = input.ranges.y;
                float rw = min(fwidth(r), RADIUS_AA_CAP_M);
                if (r < innerR - rw || r > outerR + rw) discard;

                // Angle test, centering around 0 so the +/-pi wrap is at the back of the fan.
                float minA = input.ranges.z;
                float maxA = input.ranges.w;
                float halfWidth = (maxA - minA) * 0.5;
                float twoPi = 2.0 * PI;
                float angleAlpha = 1.0;
                if (halfWidth < PI - FULL_CIRCLE_EPS)
                {
                    float aCenter = atan2(-local.x, local.z) - (minA + maxA) * 0.5;
                    aCenter = aCenter - twoPi * floor(aCenter / twoPi + 0.5);
                    float aw = min(fwidth(aCenter), ANGLE_AA_CAP_RAD);
                    if (abs(aCenter) > halfWidth + aw) discard;
                    angleAlpha = 1.0 - smoothstep(halfWidth - aw, halfWidth + aw, abs(aCenter));
                }

                // Y-edge fade
                float yNorm = abs(local.y) / max(input.projectionHeight, DIVIDE_EPS);
                float heightAlpha = saturate((1.0 - yNorm) / HEIGHT_FADE_BAND);

                // Anti aliasing
                float innerEdge = (innerR > 0.0) ? smoothstep(innerR - rw, innerR + rw, r) : 1.0;
                float outerEdge = 1.0 - smoothstep(outerR - rw, outerR + rw, r);
                float radiusAlpha = innerEdge * outerEdge;

                float t = (outerR > innerR) ? saturate((r - innerR) / (outerR - innerR)) : 0.0;
                float4 color = lerp(input.colorOrigin, input.colorEnd, t);
                color.a *= radiusAlpha * angleAlpha * heightAlpha;
                color = applyFresnelRim(color, uv, world, input.fresnelParams);
                color = applyDistanceFade(color, sceneNdcZ, input.fadeParams);

                return color;
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        PctService.Log.Debug($"ProjectedFan VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        PctService.Log.Debug($"ProjectedFan PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, DXRenderer.AlignTo16<Constants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("RADIUS", 0, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("RADIUS", 1, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("ANGLE", 0, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("ANGLE", 1, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("INSTANCECOLOR", 0, Format.R32G32B32A32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("INSTANCECOLOR", 1, Format.R32G32B32A32_Float, -1, 0, InputClassification.PerInstanceData, 1),
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

    public void Add(Vector3 origin, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint colorOrigin, uint colorEnd, PctDxParams p)
    {
        (_builder ??= _data.Map(_ctx)).Add(origin, innerRadius, outerRadius, minAngle, maxAngle, colorOrigin.ToVector4(), colorEnd.ToVector4(), p);
    }

    public void EndBuilder()
    {
        _builder?.Dispose();
        _builder = null;
    }

    public void FlushRange(int start, int count)
    {
        Bind();
        _data.DrawRange(_ctx, start, count);
    }

    internal void Bind()
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
