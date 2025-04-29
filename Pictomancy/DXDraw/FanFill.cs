using SharpDX;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Runtime.InteropServices;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace Pictomancy.DXDraw;

internal class FanFill : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public Matrix ViewProj;
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
    }

    public class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _circles;

            internal Builder(RenderContext ctx, Data data)
            {
                _circles = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _circles.Dispose();
            }

            public void Add(ref Instance inst) => _circles.Add(ref inst);
            public void Add(Vector3 world, float innerRadius, float outerRadius, float minAngle, float maxAngle, Vector4 colorOrigin, Vector4 colorEnd) =>
                _circles.Add(new Instance()
                {
                    Origin = world,
                    InnerRadius = innerRadius,
                    OuterRadius = outerRadius,
                    MinAngle = minAngle,
                    MaxAngle = maxAngle,
                    ColorOrigin = colorOrigin,
                    ColorEnd = colorEnd
                });
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount, bool dynamic)
        {
            _buffer = new("Fan", ctx, maxCount, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // Draw* should be called after FanFill.Bind set up its state
        public void DrawAll(RenderContext ctx)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.DrawInstanced(722, _buffer.CurElements, 0, 0);
        }
    }

    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly InputLayout _il;
    private readonly VertexShader _vs;
    private readonly PixelShader _ps;
    public FanFill(RenderContext ctx)
    {
        var shader = """
            #define PI 3.14159265359f
            #define SEGMENTS 360

            struct Constants
            {
                float4x4 viewProj;
            };
            Constants k : register(c0);
            Texture2DArray textureSampler : register(t0);
            SamplerState textureSamplerState : register(s0);
            
            struct Fan
            {
                float3 origin : WORLD;
                float innerRadius : RADIUS0;
                float outerRadius : RADIUS1;
                float minAngle : ANGLE0;
                float maxAngle : ANGLE1;
                float4 colorOrigin : INSTANCECOLOR0;
                float4 colorEnd : INSTANCECOLOR1;
            };

            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                float4 color : COLOR;
                float2 texCoord : TEXCOORD;
                int textureIndex : TEXCOORD1;
            };

            VSOutput vs(in Fan instance, uint vertexId: SV_VERTEXID, uint instanceId: SV_INSTANCEID)
            {
                VSOutput o;

                uint i = vertexId / 2;

                float radius = 0;
                if (vertexId % 2 == 0) {
                    o.color = instance.colorOrigin;
                    o.texCoord.y = 0;
                    radius = instance.innerRadius;
                } else {
                    o.color = instance.colorEnd;
                    o.texCoord.y = 1;//instance.outerRadius - instance.innerRadius;
                    radius = instance.outerRadius;
                }
                float totalAngle = instance.maxAngle - instance.minAngle;
                float angleStep =  totalAngle / SEGMENTS;
                float angle = PI / 2 + instance.minAngle + i * angleStep;
                float3 offset = radius * float3(cos(angle), 0, sin(angle));

                o.texCoord.x = (angle - PI/2) / (PI*2);
                o.projPos = mul(float4(instance.origin + offset, 1.0), k.viewProj);
                o.textureIndex = 1;
                return o;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float4 texture = textureSampler.Sample(textureSamplerState, float3(input.texCoord, input.textureIndex));
                return input.color * texture;
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        PictoService.Log.Debug($"Circle VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        PictoService.Log.Debug($"Circle PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, 16 * 4, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("RADIUS", 0, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("RADIUS", 1, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("ANGLE", 0, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("ANGLE", 1, Format.R32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("INSTANCECOLOR", 0, Format.R32G32B32A32_Float, -1, 0, InputClassification.PerInstanceData, 1),
            new InputElement("INSTANCECOLOR", 1, Format.R32G32B32A32_Float, -1, 0, InputClassification.PerInstanceData, 1),
        ]);
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _il.Dispose();
        _vs.Dispose();
        _ps.Dispose();
    }

    public void UpdateConstants(RenderContext ctx, Constants consts)
    {
        consts.ViewProj.Transpose();
        ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Bind(RenderContext ctx)
    {
        ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleStrip;
        ctx.Context.InputAssembler.InputLayout = _il;
        ctx.Context.VertexShader.Set(_vs);
        ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        ctx.Context.PixelShader.Set(_ps);
        ctx.Context.PixelShader.SetShaderResources(0, ctx.Texture);
        ctx.Context.PixelShader.SetSampler(0, ctx.SamplerState);
        ctx.Context.GeometryShader.Set(null);
    }

    public void Draw(RenderContext ctx, Data data)
    {
        Bind(ctx);
        data.DrawAll(ctx);
    }
}
