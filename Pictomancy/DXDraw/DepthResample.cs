using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System.Runtime.InteropServices;

namespace Pictomancy.DXDraw;

internal class DepthResample : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    public struct Constants
    {
        public SharpDX.Vector2 UvScale;
        public float Bias;
    }

    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly VertexShader _vs;
    private readonly PixelShader _ps;

    public DepthResample(RenderContext ctx)
    {
        var shader = """
            cbuffer Constants : register(b0)
            {
                float2 uvScale;
                float bias;
            };

            Texture2D<float4> sceneDepth : register(t0);

            SamplerState DepthSampler
            {
                Filter = MIN_MAG_MIP_POINT;
                AddressU = CLAMP;
                AddressV = CLAMP;
            };

            struct VSOutput
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD;
            };

            VSOutput vs(uint id : SV_VertexID)
            {
                VSOutput output;
                float2 uv = float2((id << 1) & 2, id & 2);
                output.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
                output.uv = uv;
                return output;
            }

            float ps(VSOutput input) : SV_Depth
            {
                float depth = sceneDepth.Sample(DepthSampler, input.uv * uvScale).r;
                return saturate(depth / (1.0 + bias * depth));
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        PctService.Log.Debug($"DepthResample VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        PctService.Log.Debug($"DepthResample PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);

        _constantBuffer = new(ctx.Device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
    }

    public void Dispose()
    {
        _constantBuffer.Dispose();
        _vs.Dispose();
        _ps.Dispose();
    }

    public void Draw(RenderContext ctx, ShaderResourceView sceneDepthSRV, SharpDX.Vector2 uvScale, float bias)
    {
        var consts = new Constants { UvScale = uvScale, Bias = bias };
        ctx.Context.UpdateSubresource(ref consts, _constantBuffer);

        ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        ctx.Context.InputAssembler.InputLayout = null;
        ctx.Context.VertexShader.Set(_vs);
        ctx.Context.PixelShader.Set(_ps);
        ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        ctx.Context.PixelShader.SetShaderResource(0, sceneDepthSRV);
        ctx.Context.GeometryShader.Set(null);
        ctx.Context.Draw(3, 0);
        ctx.Context.PixelShader.SetShaderResource(0, null);
    }
}
