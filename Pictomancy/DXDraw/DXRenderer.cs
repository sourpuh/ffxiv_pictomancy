using FFXIVClientStructs.FFXIV.Client.Game.Control;
using SharpDX.Direct3D11;
using System.Numerics;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Pictomancy.DXDraw;

internal class DXRenderer : IDisposable
{
    public const int MAX_FANS = 2048;
    public const int MAX_TRIS = 1024;
    public const int MAX_STROKE_SEGMENTS = MAX_FANS * Stroke.MAXIMUM_ARC_SEGMENTS / 2;
    public const int MAX_CLIP_ZONES = 512 * 6;

    public RenderContext RenderContext { get; init; } = new();
    internal RenderTarget? RenderTarget { get; private set; }
    public TriFill TriFill { get; init; }
    public FanFill FanFill { get; init; }
    public Stroke Stroke { get; init; }
    public FullScreenPass FSP { get; init; }

    public SharpDX.Matrix ViewProj { get; private set; }
    public SharpDX.Vector2 ViewportSize { get; private set; }

    private readonly TriFill.Data _triFillDynamicData;
    private TriFill.Data.Builder? _triFillDynamicBuilder;

    private readonly FanFill.Data _fanFillDynamicData;
    private FanFill.Data.Builder? _fanFillDynamicBuilder;

    private readonly Stroke.Data _strokeDynamicData;
    private Stroke.Data.Builder? _strokeDynamicBuilder;

    public bool FanDegraded { get; private set; }

    public bool StrokeDegraded { get; private set; }

    public DXRenderer()
    {
        try
        {
            // uncomment to test linux fanfill fallback renderer
            // throw new Exception("test exception please ignore");
            FanFill = new(RenderContext);
        }
        catch (Exception)
        {
            PictoService.Log.Error("[Pictomancy] Failed to compile fan shader; starting in degraded mode.");
            FanDegraded = true;
        }
        _fanFillDynamicData = new(RenderContext, FanDegraded ? 1 : MAX_FANS, true);
        try
        {
            // uncomment to test linux imgui fallback renderer
            // throw new Exception("test exception please ignore");
            Stroke = new(RenderContext);
        }
        catch (Exception)
        {
            PictoService.Log.Error("[Pictomancy] Failed to compile stroke shader; starting in degraded mode.");
            StrokeDegraded = true;
        }
        _strokeDynamicData = new(RenderContext, StrokeDegraded ? 1 : MAX_STROKE_SEGMENTS, true);

        TriFill = new(RenderContext);
        _triFillDynamicData = new(RenderContext, MAX_TRIS + (FanDegraded ? MAX_FANS * 360 : 0), true);

        FSP = new(RenderContext);
    }

    public void Dispose()
    {
        RenderTarget?.Dispose();
        _triFillDynamicBuilder?.Dispose();
        _triFillDynamicData?.Dispose();
        _fanFillDynamicBuilder?.Dispose();
        _fanFillDynamicData?.Dispose();
        _strokeDynamicBuilder?.Dispose();
        _strokeDynamicData?.Dispose();
        if (!FanDegraded)
        {
            FanFill.Dispose();
        }
        if (!StrokeDegraded)
        {
            Stroke.Dispose();
        }
        FSP.Dispose();
        RenderContext.Dispose();
    }

    internal unsafe void BeginFrame()
    {
        var device = Device.Instance();
        ViewportSize = new(device->Width, device->Height);
        ViewProj = *(SharpDX.Matrix*)&Control.Instance()->ViewProjectionMatrix;

        TriFill.UpdateConstants(RenderContext, new() { ViewProj = ViewProj });
        if (!FanDegraded)
        {
            FanFill.UpdateConstants(RenderContext, new() { ViewProj = ViewProj });
        }
        if (!StrokeDegraded)
        {
            Stroke.UpdateConstants(RenderContext, new() { ViewProj = ViewProj, RenderTargetSize = new(ViewportSize.X, ViewportSize.Y) });
        }
        FSP.UpdateConstants(RenderContext, new() { MaxAlpha = PictoService.Hints.MaxAlphaFraction });

        if (RenderTarget == null || RenderTarget.Size != ViewportSize)
        {
            RenderTarget?.Dispose();
            RenderTarget = new(RenderContext, (int)ViewportSize.X, (int)ViewportSize.Y, PictoService.Hints.AlphaBlendMode);
        }
        RenderTarget.Bind(RenderContext);
    }

    internal unsafe RenderTarget EndFrame()
    {
        if (_triFillDynamicBuilder != null)
        {
            _triFillDynamicBuilder.Dispose();
            _triFillDynamicBuilder = null;
            TriFill.Draw(RenderContext, _triFillDynamicData);
        }
        if (!FanDegraded && _fanFillDynamicBuilder != null)
        {
            _fanFillDynamicBuilder.Dispose();
            _fanFillDynamicBuilder = null;
            FanFill.Draw(RenderContext, _fanFillDynamicData);
        }
        if (!StrokeDegraded && _strokeDynamicBuilder != null)
        {
            _strokeDynamicBuilder.Dispose();
            _strokeDynamicBuilder = null;
            Stroke.Draw(RenderContext, _strokeDynamicData);
        }

        var device = Device.Instance();
        if (device != null &&
            device->SwapChain != null &&
            device->SwapChain->BackBuffer != null &&
            device->SwapChain->BackBuffer->D3D11Texture2D != null)
        {
            var backBuffer = new Texture2D((IntPtr)Device.Instance()->SwapChain->BackBuffer->D3D11Texture2D);
            RenderTarget!.ExecuteFSP(RenderContext, backBuffer, FSP);
        }
        else
        {
            PictoService.Log.Warning("[Pictomancy] DXRenderer.EndFrame: Device or BackBuffer is null; skipping combined pass.");
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
    {
        GetTriFills().Add(a, colorA.ToVector4());
        GetTriFills().Add(b, colorB.ToVector4());
        GetTriFills().Add(c, colorC.ToVector4());
    }
    private TriFill.Data.Builder GetTriFills() => _triFillDynamicBuilder ??= _triFillDynamicData.Map(RenderContext);

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
            GetFanFills().Add(
                center,
                innerRadius,
                outerRadius,
                minAngle,
                maxAngle,
                innerColor.ToVector4(),
                outerColor.ToVector4());
        }
        else
        {
            DrawTriangleFan(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor, numSegments);
        }
    }
    private FanFill.Data.Builder GetFanFills() => _fanFillDynamicBuilder ??= _fanFillDynamicData.Map(RenderContext);

    public void DrawStroke(IEnumerable<Vector3> world, float thickness, uint color, bool closed = false)
    {
        if (!StrokeDegraded)
        {
            GetStroke().Add(world.ToArray(), thickness, color.ToVector4(), closed);
        }
    }

    private Stroke.Data.Builder GetStroke() => _strokeDynamicBuilder ??= _strokeDynamicData.Map(RenderContext);

    private static unsafe SharpDX.Matrix ReadMatrix(IntPtr address)
    {
        var p = (float*)address;
        SharpDX.Matrix mtx = new();
        for (var i = 0; i < 16; i++)
            mtx[i] = *p++;
        return mtx;
    }
}
