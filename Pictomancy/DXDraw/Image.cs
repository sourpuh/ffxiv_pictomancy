using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace Pictomancy.DXDraw;

internal class Image : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
        public Matrix InvViewProj;
        public Vector2 RenderTargetSize;
        public Vector2 PixelToUv;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Instance
    {
        public Vector3 Center;
        public Vector3 Right;
        public Vector3 Down;
        public float OccludedAlpha;
        public float OcclusionTolerance;
        public float FadeStart;
        public float FadeStop;
        public float ProjectionHeight;
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

        public Data(RenderContext ctx, string name, int maxCount)
        {
            _buffer = new(name, ctx, maxCount, BindFlags.VertexBuffer, true);
        }

        public void Dispose() => _buffer.Dispose();
        public Builder Map(RenderContext ctx) => new(ctx, this);

        public void BindVertexBuffer(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
        }
    }

    private readonly List<Submission> _projectedImages = new();
    private readonly List<Submission> _flatImages = new();
    private readonly RenderContext _ctx;
    private readonly Data _projectedData;
    private readonly Data _flatData;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly InputLayout _il;
    private readonly VertexShader _vsFlat;
    private readonly PixelShader _psFlat;
    private readonly VertexShader _vsProj;
    private readonly PixelShader _psProj;
    private readonly RasterizerState _rsFlat;
    private readonly RasterizerState _rsProj;

    public bool HasPending => _projectedImages.Count > 0 || _flatImages.Count > 0;

    public Image(RenderContext ctx, int maxImages)
    {
        _ctx = ctx;
        _projectedData = new(ctx, "ImageProjected", maxImages);
        _flatData = new(ctx, "ImageFlat", maxImages);

        var shaderHeader = """
            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float4x4 invViewProj;
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

            struct ImageInstance
            {
                float3 center : WORLD0;
                float3 right : WORLD1;
                float3 down : WORLD2;
                float2 occlusionParams : OCCLUSIONPARAMS;
                float2 fadeParams : FADEPARAMS;
                float projectionHeight : HEIGHT;
            };
            """;

        var flatShader = ShapeSharedShader.Preamble + shaderHeader + ShapeSharedShader.Mixin + """
            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                float2 uv : TEXCOORD0;
                nointerpolation float2 occlusionParams : OCCLUSIONPARAMS;
                nointerpolation float2 fadeParams : FADEPARAMS;
            };

            VSOutput vs(in ImageInstance inst, uint vertexId : SV_VERTEXID)
            {
                float2 corner = float2(vertexId & 1u, (vertexId >> 1u) & 1u);
                float2 offset = corner - 0.5;
                float3 worldPos = inst.center + offset.x * inst.right + offset.y * inst.down;

                VSOutput o;
                o.projPos = mul(float4(worldPos, 1.0), viewProj);
                o.uv = corner;
                o.occlusionParams = inst.occlusionParams;
                o.fadeParams = inst.fadeParams;
                return o;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float4 color = _userTex.Sample(_userSampler, input.uv);
                return applyShared(color, input.projPos.xyz, input.occlusionParams, input.fadeParams);
            }
            """;

        var vsFlat = ShaderBytecode.Compile(flatShader, "vs", "vs_5_0");
        if (vsFlat.HasErrors || vsFlat.Bytecode == null)
            throw new InvalidOperationException($"Image flat VS compile failed:\n{vsFlat.Message}");
        PctService.Log.Debug($"Image flat VS compile: {vsFlat.Message}");
        _vsFlat = new(ctx.Device, vsFlat.Bytecode);

        var psFlat = ShaderBytecode.Compile(flatShader, "ps", "ps_5_0");
        if (psFlat.HasErrors || psFlat.Bytecode == null)
            throw new InvalidOperationException($"Image flat PS compile failed:\n{psFlat.Message}");
        PctService.Log.Debug($"Image flat PS compile: {psFlat.Message}");
        _psFlat = new(ctx.Device, psFlat.Bytecode);

        // Decal slab: render the bounding box; PS reconstructs scene world pos and tests it against the slab.
        // Slab extends +/- projectionHeight/2 along the plane normal so the decal projects in both directions.
        // Corner bits: x = u along right, y = v along down, z = -normal / +normal half.
        var projShader = ShapeSharedShader.Preamble + shaderHeader + ShapeSharedShader.Mixin + """
            float sampleSceneNdcZ(float2 uv)
            {
                // Skip pixels on character geometry; decals should hit world surfaces only.
                float3 sceneInfo = _sceneInfo.Sample(_sceneSampler, uv).rgb;
                clip(max(sceneInfo.r, max(sceneInfo.g, sceneInfo.b)) - CHARACTER_MASK_THRESHOLD);
                return _sceneDepth.Sample(_sceneSampler, uv).r;
            }

            float3 getWorldPos(float2 projPos, float sceneNdcZ)
            {
                float2 ndcXY;
                ndcXY.x = projPos.x / renderTargetSize.x * 2.0 - 1.0;
                ndcXY.y = 1.0 - projPos.y / renderTargetSize.y * 2.0;
                float4 worldH = mul(float4(ndcXY, sceneNdcZ, 1.0), invViewProj);
                return worldH.xyz / worldH.w;
            }

            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                nointerpolation float3 center : INSTCENTER;
                nointerpolation float3 right : INSTRIGHT;
                nointerpolation float3 down : INSTDOWN;
                nointerpolation float3 normal : INSTNORMAL;
                nointerpolation float projectionHeight : INSTHEIGHT;
                nointerpolation float2 fadeParams : FADEPARAMS;
            };

            static const uint BOX_LIST[36] = {
                3, 7, 5,  3, 5, 1, // +X face
                6, 2, 0,  6, 0, 4, // -X face
                6, 7, 3,  6, 3, 2, // +Y face
                5, 4, 0,  5, 0, 1, // -Y face
                7, 6, 4,  7, 4, 5, // +Z face
                2, 3, 1,  2, 1, 0, // -Z face
            };

            VSOutput vs(in ImageInstance instance, uint vertexId : SV_VERTEXID)
            {
                float3 normal = normalize(cross(instance.right, instance.down));
                uint corner = BOX_LIST[vertexId];
                float u = (corner & 1u) ? 0.5 : -0.5;
                float v = (corner & 2u) ? 0.5 : -0.5;
                float d = (corner & 4u) ? 0.5 : -0.5;
                float3 worldPos = instance.center + u * instance.right + v * instance.down + d * instance.projectionHeight * normal;

                VSOutput o;
                o.projPos = mul(float4(worldPos, 1.0), viewProj);
                o.center = instance.center;
                o.right = instance.right;
                o.down = instance.down;
                o.normal = normal;
                o.projectionHeight = instance.projectionHeight;
                o.fadeParams = instance.fadeParams;
                return o;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float2 sceneUv = input.projPos.xy * pixelToUv;
                float sceneNdcZ = sampleSceneNdcZ(sceneUv);
                float3 world = getWorldPos(input.projPos.xy, sceneNdcZ);

                float3 local = world - input.center;
                float rightLenSq = dot(input.right, input.right);
                float downLenSq = dot(input.down, input.down);
                float u = dot(local, input.right) / max(rightLenSq, DIVIDE_EPS);
                float v = dot(local, input.down) / max(downLenSq, DIVIDE_EPS);
                float depthFromPlane = dot(local, input.normal);
                float halfHeight = input.projectionHeight * 0.5;

                if (abs(u) > 0.5) discard;
                if (abs(v) > 0.5) discard;
                if (abs(depthFromPlane) > halfHeight) discard;

                float4 color = _userTex.Sample(_userSampler, float2(u + 0.5, v + 0.5));
                float depthNorm = abs(depthFromPlane) / max(halfHeight, DIVIDE_EPS);
                color.a *= saturate((1.0 - depthNorm) / HEIGHT_FADE_BAND);

                return applyDistanceFade(color, sceneNdcZ, input.fadeParams);
            }
            """;

        var vsProj = ShaderBytecode.Compile(projShader, "vs", "vs_5_0");
        if (vsProj.HasErrors || vsProj.Bytecode == null)
            throw new InvalidOperationException($"Image projected VS compile failed:\n{vsProj.Message}");
        PctService.Log.Debug($"Image projected VS compile: {vsProj.Message}");
        _vsProj = new(ctx.Device, vsProj.Bytecode);

        var psProj = ShaderBytecode.Compile(projShader, "ps", "ps_5_0");
        if (psProj.HasErrors || psProj.Bytecode == null)
            throw new InvalidOperationException($"Image projected PS compile failed:\n{psProj.Message}");
        PctService.Log.Debug($"Image projected PS compile: {psProj.Message}");
        _psProj = new(ctx.Device, psProj.Bytecode);

        _constantBuffer = new(ctx.Device, DXRenderer.AlignTo16<Constants>(), ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        _il = new(ctx.Device, vsFlat.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("WORLD", 1, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("WORLD", 2, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("OCCLUSIONPARAMS", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("FADEPARAMS", 0, Format.R32G32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("HEIGHT", 0, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
        ]);

        var rsDescFlat = RasterizerStateDescription.Default();
        rsDescFlat.CullMode = CullMode.None;
        _rsFlat = new(ctx.Device, rsDescFlat);

        var rsDescProj = RasterizerStateDescription.Default();
        rsDescProj.CullMode = CullMode.Back;
        _rsProj = new(ctx.Device, rsDescProj);
    }

    public void Dispose()
    {
        _projectedData.Dispose();
        _flatData.Dispose();
        _constantBuffer.Dispose();
        _il.Dispose();
        _vsFlat.Dispose();
        _psFlat.Dispose();
        _vsProj.Dispose();
        _psProj.Dispose();
        _rsFlat.Dispose();
        _rsProj.Dispose();
    }

    public void UpdateConstants(Constants consts)
    {
        consts.ViewProj.Transpose();
        consts.InvViewProj.Transpose();
        _ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Add(IntPtr nativePtr, Vector3 center, Vector3 right, Vector3 down, PctDxParams p)
    {
        if (nativePtr == IntPtr.Zero) return;
        // Skip degenerate plane (right parallel to down).
        if (Vector3.Cross(right, down).LengthSquared() < 1e-8f) return;

        bool projected = p.ProjectionHeight > 0f;

        var sub = new Submission
        {
            Inst = new Instance
            {
                Center = center,
                Right = right,
                Down = down,
                OccludedAlpha = p.OccludedAlpha,
                OcclusionTolerance = p.OcclusionTolerance,
                FadeStart = p.FadeStart,
                FadeStop = p.FadeStop,
                ProjectionHeight = projected ? p.ProjectionHeight : 0f,
            },
            NativePtr = nativePtr,
        };
        if (projected) _projectedImages.Add(sub);
        else _flatImages.Add(sub);
    }

    // Writes the projected instance buffer; called once before FlushProjectedRange.
    public void EndProjectedBuilder()
    {
        if (_projectedImages.Count == 0) return;
        using var b = _projectedData.Map(_ctx);
        for (int i = 0; i < _projectedImages.Count; i++)
            b.Add(_projectedImages[i].Inst);
    }

    public void FlushProjectedRange(int start, int count)
    {
        BindProjected();
        _projectedData.BindVertexBuffer(_ctx);
        for (int i = start; i < start + count; i++)
        {
            _ctx.Context.PixelShader.SetShaderResource(3, _ctx.GetUserSrv(_projectedImages[i].NativePtr));
            _ctx.Context.DrawInstanced(36, 1, 0, i);
        }
        _ctx.Context.PixelShader.SetShaderResource(3, null);
    }

    public void FlushFlat(Vector3 cameraPos)
    {
        if (_flatImages.Count > 0)
        {
            _flatImages.Sort((a, b) =>
            {
                float da = Vector3.DistanceSquared(a.Inst.Center, cameraPos);
                float db = Vector3.DistanceSquared(b.Inst.Center, cameraPos);
                return db.CompareTo(da);
            });

            using (var b = _flatData.Map(_ctx))
            {
                for (int i = 0; i < _flatImages.Count; i++)
                    b.Add(_flatImages[i].Inst);
            }

            BindFlat();
            _flatData.BindVertexBuffer(_ctx);
            for (int i = 0; i < _flatImages.Count; i++)
            {
                _ctx.Context.PixelShader.SetShaderResource(3, _ctx.GetUserSrv(_flatImages[i].NativePtr));
                _ctx.Context.DrawInstanced(4, 1, 0, i);
            }
            _ctx.Context.PixelShader.SetShaderResource(3, null);
        }

        _projectedImages.Clear();
        _flatImages.Clear();
    }

    private void BindFlat()
    {
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        _ctx.Context.InputAssembler.InputLayout = _il;
        _ctx.Context.VertexShader.Set(_vsFlat);
        _ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.Set(_psFlat);
        _ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.GeometryShader.Set(null);
        _ctx.Context.Rasterizer.State = _rsFlat;
    }

    private void BindProjected()
    {
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _ctx.Context.InputAssembler.InputLayout = _il;
        _ctx.Context.VertexShader.Set(_vsProj);
        _ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.Set(_psProj);
        _ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.GeometryShader.Set(null);
        _ctx.Context.Rasterizer.State = _rsProj;
    }
}
