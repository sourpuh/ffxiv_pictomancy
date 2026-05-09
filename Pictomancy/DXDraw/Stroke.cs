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

internal class Stroke : IDisposable
{
    public const int MAXIMUM_ARC_SEGMENTS = 240;

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
        public Vector3 World;
        public float Thickness;
        public Vector4 Color;
        public uint IsBoundary;
        public float OccludedAlpha;
        public float OcclusionTolerance;
        public float FadeStart;
        public float FadeStop;
    }

    private class Data : IDisposable
    {
        public class Builder : IDisposable
        {
            private RenderBuffer<Instance>.Builder _lines;

            internal Builder(RenderContext ctx, Data data)
            {
                _lines = data._buffer.Map(ctx);
            }

            public void Dispose()
            {
                _lines.Dispose();
            }

            public void Add(ref Instance inst) => _lines.Add(ref inst);
            public void Add(Vector3[] world, float thickness, Vector4 color, bool closed, PctDxParams p)
            {
                int n = world.Length;
                if (n < 2) return;

                if (closed && n >= 3)
                {
                    AddInstance(world[n - 1], thickness, color, true, p);
                    for (int i = 0; i < n; i++)
                        AddInstance(world[i], thickness, color, false, p);
                    AddInstance(world[0], thickness, color, false, p);
                    AddInstance(world[1], thickness, color, true, p);
                }
                else
                {
                    AddInstance(world[0], thickness, color, true, p);
                    for (int i = 0; i < n; i++)
                        AddInstance(world[i], thickness, color, false, p);
                    AddInstance(world[n - 1], thickness, color, true, p);
                }
            }

            private void AddInstance(Vector3 world, float thickness, Vector4 color, bool isBoundary, PctDxParams p)
            {
                _lines.Add(new Instance()
                {
                    World = world,
                    Thickness = thickness,
                    Color = color,
                    IsBoundary = isBoundary ? 1u : 0u,
                    OccludedAlpha = p.OccludedAlpha,
                    OcclusionTolerance = p.OcclusionTolerance,
                    FadeStart = p.FadeStart,
                    FadeStop = p.FadeStop,
                });
            }
        }

        private readonly RenderBuffer<Instance> _buffer;

        public Data(RenderContext ctx, int maxCount, bool dynamic)
        {
            _buffer = new("Stroke", ctx, maxCount, BindFlags.VertexBuffer, dynamic);
        }

        public void Dispose()
        {
            _buffer.Dispose();
        }

        public Builder Map(RenderContext ctx) => new(ctx, this);

        // Draw* should be called after Stroke.Bind set up its state
        public void DrawSubset(RenderContext ctx, int firstLine, int numLines)
        {
            ctx.Context.InputAssembler.SetVertexBuffers(0, new VertexBufferBinding(_buffer.Buffer, _buffer.ElementSize, 0));
            ctx.Context.Draw(numLines, firstLine);
        }

        public void DrawAll(RenderContext ctx) => DrawSubset(ctx, 0, _buffer.CurElements);
    }

    private readonly RenderContext _ctx;
    private readonly Data _data;
    private Data.Builder? _builder;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;
    private readonly InputLayout _il;
    private readonly VertexShader _vs;
    private readonly GeometryShader _gs;
    private readonly PixelShader _ps;

    public bool HasPending => _builder != null;

    public Stroke(RenderContext ctx, int maxSegments)
    {
        _ctx = ctx;
        _data = new(ctx, maxSegments, true);
        var shader = """
            #define FEATHER 2
            #define MITER_LIMIT 4.0
            
            cbuffer Constants : register(b0)
            {
                float4x4 viewProj;
                float2 renderTargetSize;
                float2 pixelToUv;
            };
            """ + ShapeSharedShader.Mixin + """
            struct Line
            {
                float3 world : WORLD;
                float thickness : THICKNESS;
                float4 color : COLOR;
                uint isBoundary : ISBOUNDARY;
                float4 fadeParams : FADEPARAMS;
            };

            struct VSOutput
            {
                float4 projPos : SV_POSITION;
                float thickness : THICKNESS;
                float4 color : COLOR;
                nointerpolation uint isBoundary : ISBOUNDARY;
                float4 fadeParams : FADEPARAMS;
            };

            struct GSOutput
            {
                float4 projPos : SV_POSITION;
                float4 color : COLOR;
                noperspective float normal : NORMAL;
                float thickness : THICKNESS;
                float4 fadeParams : FADEPARAMS;
            };

            VSOutput vs(in Line l)
            {
                VSOutput v;

                v.thickness = l.thickness + FEATHER / 2;
                v.color = l.color;
                v.isBoundary = l.isBoundary;
                v.fadeParams = l.fadeParams;

                v.projPos = mul(float4(l.world, 1), viewProj);
                return v;
            }

            float2 perpendicularNdc(float2 dirPx, float2 rtSize)
            {
                float2 perpPx = normalize(float2(dirPx.y, -dirPx.x));
                return perpPx * (2.0 / rtSize);
            }

            float2 miterOffsetNdc(float2 perpA, float2 perpB, float halfThicknessPx, float2 rtSize, float miterLimit)
            {
                float2 sum = perpA + perpB;
                if (dot(sum, sum) < 1e-6) {
                    // Hairpin
                    return perpA * (halfThicknessPx * 2.0 / rtSize);
                }
                float2 miter = normalize(sum);
                float invCos = 1.0 / max(dot(miter, perpB), 1.0 / miterLimit);
                return miter * (halfThicknessPx * invCos * 2.0 / rtSize);
            }

            float2 endOffsetNdc(float2 neighborDeltaPx, float neighborW, float2 perpSegPx, float2 dirSegPx, float halfThicknessPx)
            {
                float near = viewProj._m32;
                if (neighborW < near || dot(neighborDeltaPx, neighborDeltaPx) < 1e-4)
                {
                    return perpendicularNdc(dirSegPx, renderTargetSize) * halfThicknessPx;
                }
                float2 dirNeighborPx = normalize(neighborDeltaPx);
                float2 perpNeighborPx = float2(dirNeighborPx.y, -dirNeighborPx.x);
                return miterOffsetNdc(perpNeighborPx, perpSegPx, halfThicknessPx, renderTargetSize, MITER_LIMIT);
            }

            [maxvertexcount(4)]
            void gs(lineadj VSOutput input[4], inout TriangleStream<GSOutput> output)
            {
                VSOutput prev  = input[0];
                VSOutput start = input[1];
                VSOutput stop  = input[2];
                VSOutput next  = input[3];

                if (start.isBoundary || stop.isBoundary) return;

                if (start.projPos.w > stop.projPos.w)
                {
                    VSOutput tmp1 = start; start = stop; stop = tmp1;
                    VSOutput tmp2 = prev;  prev  = next; next = tmp2;
                }

                float near = viewProj._m32;
                if (start.projPos.w < near)
                {
            	    float t = (near - start.projPos.w) / (stop.projPos.w - start.projPos.w);
            	    start.projPos = lerp(start.projPos, stop.projPos, t);
                }

                float w0 = start.projPos.w;
                float w1 = stop.projPos.w;
                float4 p0 = start.projPos / w0;
                float4 p1 = stop.projPos  / w1;
                float2 prevNdc = prev.projPos.xy / max(prev.projPos.w, 1e-6);
                float2 nextNdc = next.projPos.xy / max(next.projPos.w, 1e-6);

                float2 startPx = p0.xy * (renderTargetSize * 0.5);
                float2 stopPx  = p1.xy * (renderTargetSize * 0.5);
                float2 prevPx  = prevNdc * (renderTargetSize * 0.5);
                float2 nextPx  = nextNdc * (renderTargetSize * 0.5);

                float2 dirSegPx = normalize(stopPx - startPx);
                float2 perpSegPx = float2(dirSegPx.y, -dirSegPx.x);

                float2 startOffsetNdc = endOffsetNdc(startPx - prevPx, prev.projPos.w, perpSegPx, dirSegPx, start.thickness * 0.5);
                float2 stopOffsetNdc  = endOffsetNdc(nextPx - stopPx,  next.projPos.w, perpSegPx, dirSegPx, stop.thickness  * 0.5);

                GSOutput v;
                v.thickness = start.thickness;
                v.color = start.color;
                v.fadeParams = start.fadeParams;
                v.normal = 1;
                v.projPos = (p0 + float4(startOffsetNdc, 0, 0)) * w0;
                output.Append(v);
                v.normal = -1;
                v.projPos = (p0 - float4(startOffsetNdc, 0, 0)) * w0;
                output.Append(v);

                v.thickness = stop.thickness;
                v.color = stop.color;
                v.fadeParams = stop.fadeParams;
                v.normal = 1;
                v.projPos = (p1 + float4(stopOffsetNdc, 0, 0)) * w1;
                output.Append(v);
                v.normal = -1;
                v.projPos = (p1 - float4(stopOffsetNdc, 0, 0)) * w1;
                output.Append(v);
            }

            float unfeather(float thickness, float normal)
            {
                float width = thickness - FEATHER;
                float pixel = abs(normal) * thickness - width;
                pixel = max(0, pixel);
                pixel /= FEATHER;
                return pixel;
            }

            float4 ps(GSOutput input) : SV_Target
            {
                float f = unfeather(input.thickness, input.normal);
                float4 color = input.color;
                color.a *= exp2(-2.7 * f * f);
                return applyShared(color, input.projPos.xyz, input.fadeParams);
            }
            """;

        var vs = ShaderBytecode.Compile(shader, "vs", "vs_5_0");
        PctService.Log.Debug($"Line VS compile: {vs.Message}");
        _vs = new(ctx.Device, vs.Bytecode);

        var gs = ShaderBytecode.Compile(shader, "gs", "gs_5_0");
        PctService.Log.Debug($"Line GS compile: {gs.Message}");
        _gs = new(ctx.Device, gs.Bytecode);

        var ps = ShaderBytecode.Compile(shader, "ps", "ps_5_0");
        PctService.Log.Debug($"Line PS compile: {ps.Message}");
        _ps = new(ctx.Device, ps.Bytecode);


        _constantBuffer = new(ctx.Device, 16 * 4 * 2, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);
        _il = new(ctx.Device, vs.Bytecode,
        [
            new InputElement("WORLD", 0, Format.R32G32B32_Float, -1, 0),
            new InputElement("THICKNESS", 0, Format.R32_Float, -1, 0),
            new InputElement("COLOR", 0, Format.R32G32B32A32_Float, -1, 0),
            new InputElement("ISBOUNDARY", 0, Format.R32_UInt, -1, 0),
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
        _gs.Dispose();
        _ps.Dispose();
    }

    public void UpdateConstants(Constants consts)
    {
        consts.ViewProj.Transpose();
        _ctx.Context.UpdateSubresource(ref consts, _constantBuffer);
    }

    public void Add(Vector3[] world, float thickness, uint color, bool closed, PctDxParams p)
    {
        (_builder ??= _data.Map(_ctx)).Add(world, thickness, color.ToVector4(), closed, p);
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
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.LineStripWithAdjacency;
        _ctx.Context.InputAssembler.InputLayout = _il;
        _ctx.Context.VertexShader.Set(_vs);
        _ctx.Context.VertexShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.GeometryShader.Set(_gs);
        _ctx.Context.GeometryShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.Set(_ps);
        _ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
    }

}
