using FFXIVClientStructs.FFXIV.Client.Game.Control;
using SharpDX.Direct3D11;
using System.Numerics;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Pictomancy.DXDraw;

internal class DXRenderer : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();
    internal RenderTarget? RenderTarget { get; private set; }
    public TriFill TriFill { get; init; }
    public FanFill? FanFill { get; init; }
    public Stroke? Stroke { get; init; }
    public FullScreenPass FSP { get; init; }
    public DepthResample DepthResample { get; init; }
    public ClipZone ClipZone { get; init; }

    private readonly DepthStencilState _resampleDSS;
    private readonly DepthStencilState _clipZoneDSS;
    private readonly DepthStencilState _shapeDSS;

    public SharpDX.Matrix ViewProj { get; private set; }
    public SharpDX.Vector2 ViewportSize { get; private set; }

    public bool FanDegraded => FanFill == null;

    public bool StrokeDegraded => Stroke == null;

    public DXRenderer(PctOptions options)
    {
        try
        {
            // uncomment to test linux fanfill fallback renderer
            //throw new Exception("test exception please ignore");
            FanFill = new(RenderContext, options.MaxFans);
        }
        catch (Exception)
        {
            PctService.Log.Error("[Pictomancy] Failed to compile fan shader; starting in degraded mode.");
        }
        try
        {
            // uncomment to test linux imgui fallback renderer
            //throw new Exception("test exception please ignore");
            Stroke = new(RenderContext, options.MaxStrokeSegments);
        }
        catch (Exception)
        {
            PctService.Log.Error("[Pictomancy] Failed to compile stroke shader; starting in degraded mode.");
        }

        // TriFill's buffer doubles as the fan-fallback path's storage in degraded mode.
        TriFill = new(RenderContext, options.MaxTriangleVertices + (FanDegraded ? options.MaxFans * 360 : 0));

        FSP = new(RenderContext);
        DepthResample = new(RenderContext);
        ClipZone = new(RenderContext, options.MaxClipZones);

        var resampleDesc = DepthStencilStateDescription.Default();
        resampleDesc.IsDepthEnabled = true;
        resampleDesc.DepthWriteMask = DepthWriteMask.All;
        resampleDesc.DepthComparison = Comparison.Always;
        resampleDesc.IsStencilEnabled = false;
        _resampleDSS = new(RenderContext.Device, resampleDesc);

        var clipZoneDesc = DepthStencilStateDescription.Default();
        clipZoneDesc.IsDepthEnabled = false;
        clipZoneDesc.DepthWriteMask = DepthWriteMask.Zero;
        clipZoneDesc.IsStencilEnabled = true;
        clipZoneDesc.StencilReadMask = 0xFF;
        clipZoneDesc.StencilWriteMask = 0xFF;
        clipZoneDesc.FrontFace = new DepthStencilOperationDescription
        {
            FailOperation = StencilOperation.Keep,
            DepthFailOperation = StencilOperation.Keep,
            PassOperation = StencilOperation.Replace,
            Comparison = Comparison.Always,
        };
        clipZoneDesc.BackFace = clipZoneDesc.FrontFace;
        _clipZoneDSS = new(RenderContext.Device, clipZoneDesc);

        var shapeDesc = DepthStencilStateDescription.Default();
        shapeDesc.IsDepthEnabled = true;
        shapeDesc.DepthWriteMask = DepthWriteMask.Zero;
        shapeDesc.DepthComparison = Comparison.GreaterEqual;
        shapeDesc.IsStencilEnabled = true;
        shapeDesc.StencilReadMask = 0xFF;
        shapeDesc.StencilWriteMask = 0;
        shapeDesc.FrontFace = new DepthStencilOperationDescription
        {
            FailOperation = StencilOperation.Keep,
            DepthFailOperation = StencilOperation.Keep,
            PassOperation = StencilOperation.Keep,
            Comparison = Comparison.Equal,
        };
        shapeDesc.BackFace = shapeDesc.FrontFace;
        _shapeDSS = new(RenderContext.Device, shapeDesc);
    }

    public void Dispose()
    {
        RenderTarget?.Dispose();
        TriFill.Dispose();
        FanFill?.Dispose();
        Stroke?.Dispose();
        ClipZone.Dispose();
        FSP.Dispose();
        DepthResample.Dispose();
        _resampleDSS.Dispose();
        _clipZoneDSS.Dispose();
        _shapeDSS.Dispose();
        RenderContext.Dispose();
    }

    internal unsafe void BeginFrame()
    {
        var device = Device.Instance();
        ViewportSize = new(device->Width, device->Height);
        ViewProj = *(SharpDX.Matrix*)&Control.Instance()->ViewProjectionMatrix;

        // Detect 3D resolution scaling
        bool resolutionScaled = false;
        var rtm = FFXIVClientStructs.FFXIV.Client.Graphics.Render.RenderTargetManager.Instance();
        var unk70 = rtm != null ? *(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x70) : null;
        if (unk70 != null)
        {
            resolutionScaled = unk70->ActualWidth != device->Width || unk70->ActualHeight != device->Height;
        }
        bool useMask = PctService.Hints.UIMask == UIMask.BackbufferAlpha
            && PctService.Hints.AutoDraw != AutoDraw.NativeOverlay
            && !resolutionScaled;
        FSP.UpdateConstants(RenderContext, new()
        {
            MaxAlpha = PctService.Hints.MaxAlphaFraction,
            UseMask = useMask ? 1f : 0f,
        });

        if (RenderTarget == null || RenderTarget.Size != ViewportSize)
        {
            RenderTarget?.Dispose();
            RenderTarget = new(RenderContext, (int)ViewportSize.X, (int)ViewportSize.Y, PctService.Hints.AlphaBlendMode);
        }
        RenderTarget.Bind(RenderContext);
    }

    internal unsafe RenderTarget EndFrame(ShaderResourceView? sceneDepthSRV, SharpDX.Vector2 sceneDepthUvScale)
    {
        TriFill.UpdateConstants(new() { ViewProj = ViewProj });
        FanFill?.UpdateConstants(new() { ViewProj = ViewProj });
        Stroke?.UpdateConstants(new() { ViewProj = ViewProj, RenderTargetSize = new Vector2(ViewportSize.X, ViewportSize.Y) });

        bool hasShapes = TriFill.HasPending || FanFill?.HasPending == true || Stroke?.HasPending == true;
        bool hasClipZones = ClipZone.HasPending;

        if (hasShapes && sceneDepthSRV != null)
        {
            // Resample scene depth into our viewport-sized DSV.
            RenderContext.Context.OutputMerger.SetTargets(RenderTarget!.LocalDepthDSV);
            RenderContext.Context.ClearDepthStencilView(RenderTarget.LocalDepthDSV, DepthStencilClearFlags.Depth | DepthStencilClearFlags.Stencil, 0f, 0);
            RenderContext.Context.OutputMerger.SetDepthStencilState(_resampleDSS);
            float near = ViewProj.M43;
            float viewProjBias = near > 0f ? PctService.Hints.DepthBias / near : 0f;
            DepthResample.Draw(RenderContext, sceneDepthSRV, sceneDepthUvScale, viewProjBias);

            if (hasClipZones)
            {
                // Stamp stencil = 1 inside each clip rect.
                ClipZone.UpdateConstants(new() { ViewportSize = new Vector2(ViewportSize.X, ViewportSize.Y) });
                RenderContext.Context.OutputMerger.SetDepthStencilState(_clipZoneDSS, 1);
                ClipZone.Flush();
            }

            // Bind RTV+DSV with read-only depth-test for shape draws.
            RenderContext.Context.OutputMerger.SetTargets(RenderTarget.LocalDepthDSV, RenderTarget.BaseRTV);
            RenderContext.Context.OutputMerger.SetDepthStencilState(_shapeDSS, 0);
        }

        TriFill.Flush();
        FanFill?.Flush();
        Stroke?.Flush();

        var device = Device.Instance();
        if (device != null &&
            device->SwapChain != null &&
            device->SwapChain->BackBuffer != null &&
            device->SwapChain->BackBuffer->D3D11Texture2D != null)
        {
            var backBuffer = new Texture2D((IntPtr)device->SwapChain->BackBuffer->D3D11Texture2D);
            RenderTarget!.ExecuteFSP(RenderContext, backBuffer, FSP);
        }
        else
        {
            PctService.Log.Warning("[Pictomancy] DXRenderer.EndFrame: Device or BackBuffer is null; skipping combined pass.");
        }

        RenderContext.Execute();
        return RenderTarget;
    }
    public void DrawText(Vector2 position, string text)
    {
        throw new NotImplementedException("try again later");
        //RenderContext.Context2.BeginDraw();
        //RenderContext.Context2.DrawText(text, position);
        //RenderContext.Context2.EndDraw();
    }
    public void DrawTriangle(Vector3 a, Vector3 b, Vector3 c, uint colorA, uint colorB, uint colorC)
        => TriFill.Add(a, b, c, colorA, colorB, colorC);

    public void AddClipZone(Vector2 min, Vector2 max) => ClipZone.Add(min, max);

    private void DrawTriangleFan(Vector3 center, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint innerColor, uint outerColor, uint numSegments = 0)
    {
        float totalAngle = maxAngle - minAngle;
        if (numSegments == 0) numSegments = (uint)(MathF.Abs(totalAngle) * 8);

        float angleStep = totalAngle / numSegments;

        Vector3 prev = new();
        for (int step = 0; step <= numSegments; step++)
        {
            float angle = MathF.PI / 2 + minAngle + step * angleStep;
            Vector3 offset = new(MathF.Cos(angle), 0, MathF.Sin(angle));

            if (step > 0)
            {
                if (innerRadius > 0)
                {
                    DrawTriangle(center + innerRadius * prev, center + outerRadius * prev, center + outerRadius * offset, innerColor, outerColor, outerColor);
                    DrawTriangle(center + outerRadius * offset, center + innerRadius * offset, center + innerRadius * prev, outerColor, innerColor, innerColor);
                }
                else
                {
                    DrawTriangle(center, center + outerRadius * prev, center + outerRadius * offset, innerColor, outerColor, outerColor);
                }
            }
            prev = offset;
        }
    }
    public void DrawFan(Vector3 center, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint innerColor, uint outerColor, uint numSegments = 0)
    {
        if (!FanDegraded && numSegments == 0)
        {
            FanFill.Add(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor);
        }
        else
        {
            DrawTriangleFan(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor, numSegments);
        }
    }

    public void DrawStroke(IEnumerable<Vector3> world, float thickness, uint color, bool closed = false)
    {
        Stroke?.Add(world.ToArray(), thickness, color, closed);
    }

    private static unsafe SharpDX.Matrix ReadMatrix(IntPtr address)
    {
        var p = (float*)address;
        SharpDX.Matrix mtx = new();
        for (var i = 0; i < 16; i++)
            mtx[i] = *p++;
        return mtx;
    }
}
