using FFXIVClientStructs.FFXIV.Client.Game.Control;
using System.Drawing;
using System.Numerics;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Pictomancy.DXDraw;

internal class DXRenderer : IDisposable
{
    public const int MAX_FANS = 2048;
    public const int MAX_TRIS = 1024;
    public const int MAX_STROKE_SEGMENTS = MAX_FANS * Stroke.MAXIMUM_ARC_SEGMENTS;
    public const int MAX_CLIP_ZONES = 256 * 6;

    public RenderContext RenderContext { get; init; } = new();

    internal RenderTarget? RenderTarget { get; private set; }
    internal RenderTarget? FSPRenderTarget { get; private set; }
    public TriFill TriFill { get; init; }
    public FanFill FanFill { get; init; }
    public Stroke Stroke { get; init; }
    public ClipZone ClipZone { get; init; }
    public FullScreenPass FSP { get; init; }

    public SharpDX.Matrix ViewProj { get; private set; }
    public SharpDX.Vector2 ViewportSize { get; private set; }

    private readonly TriFill.Data _triFillDynamicData;
    private TriFill.Data.Builder? _triFillDynamicBuilder;

    private readonly FanFill.Data _fanFillDynamicData;
    private FanFill.Data.Builder? _fanFillDynamicBuilder;

    private readonly Stroke.Data _strokeDynamicData;
    private Stroke.Data.Builder? _strokeDynamicBuilder;

    private readonly ClipZone.Data _clipDynamicData;
    private ClipZone.Data.Builder? _clipDynamicBuilder;

    public bool StrokeDegraded { get; private set; }

    public DXRenderer()
    {
        TriFill = new(RenderContext);
        _triFillDynamicData = new(RenderContext, MAX_TRIS, true);
        FanFill = new(RenderContext);
        _fanFillDynamicData = new(RenderContext, MAX_FANS, true);
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
        _strokeDynamicData = new(RenderContext, MAX_STROKE_SEGMENTS, true);
        ClipZone = new(RenderContext);
        _clipDynamicData = new(RenderContext, MAX_CLIP_ZONES, true);
        FSP = new(RenderContext);
    }

    public void Dispose()
    {
        RenderTarget?.Dispose();
        _triFillDynamicBuilder?.Dispose();
        _triFillDynamicData?.Dispose();
        FSPRenderTarget?.Dispose();
        _fanFillDynamicBuilder?.Dispose();
        _fanFillDynamicData?.Dispose();
        _strokeDynamicBuilder?.Dispose();
        _strokeDynamicData?.Dispose();
        _clipDynamicBuilder?.Dispose();
        _clipDynamicData?.Dispose();
        FanFill.Dispose();
        if (!StrokeDegraded)
        {
            Stroke.Dispose();
        }
        ClipZone.Dispose();
        FSP.Dispose();
        RenderContext.Dispose();
    }

    internal unsafe void BeginFrame()
    {
        var device = Device.Instance();
        ViewportSize = new(device->Width, device->Height);
        ViewProj = *(SharpDX.Matrix*)&Control.Instance()->ViewProjectionMatrix;

        TriFill.UpdateConstants(RenderContext, new() { ViewProj = ViewProj });
        FanFill.UpdateConstants(RenderContext, new() { ViewProj = ViewProj });
        if (!StrokeDegraded)
        {
            Stroke.UpdateConstants(RenderContext, new() { ViewProj = ViewProj, RenderTargetSize = new(ViewportSize.X, ViewportSize.Y) });
        }
        ClipZone.UpdateConstants(RenderContext, new() { RenderTargetSize = new(ViewportSize.X, ViewportSize.Y) });
        FSP.UpdateConstants(RenderContext, new() { MaxAlpha = PictoService.Hints.MaxAlphaFraction });

        if (RenderTarget == null || RenderTarget.Size != ViewportSize)
        {
            RenderTarget?.Dispose();
            RenderTarget = new(RenderContext, (int)ViewportSize.X, (int)ViewportSize.Y, PictoService.Hints.AlphaBlendMode);
        }
        if (FSPRenderTarget == null || FSPRenderTarget.Size != ViewportSize)
        {
            FSPRenderTarget?.Dispose();
            FSPRenderTarget = new(RenderContext, (int)ViewportSize.X, (int)ViewportSize.Y, AlphaBlendMode.None);
        }
        RenderTarget.Bind(RenderContext);
    }

    internal RenderTarget EndFrame()
    {
        // Draw all shapes and and perform clipping for the main RenderTarget.
        if (_triFillDynamicBuilder != null)
        {
            _triFillDynamicBuilder.Dispose();
            _triFillDynamicBuilder = null;
            TriFill.Draw(RenderContext, _triFillDynamicData);
        }
        if (_fanFillDynamicBuilder != null)
        {
            _fanFillDynamicBuilder.Dispose();
            _fanFillDynamicBuilder = null;
            FanFill.Draw(RenderContext, _fanFillDynamicData);
        }
        if (!StrokeDegraded)
        {
            if (_strokeDynamicBuilder != null)
            {
                _strokeDynamicBuilder.Dispose();
                _strokeDynamicBuilder = null;
                Stroke.Draw(RenderContext, _strokeDynamicData);
            }
        }
        RenderTarget!.Clip(RenderContext);
        if (_clipDynamicBuilder != null)
        {
            _clipDynamicBuilder.Dispose();
            _clipDynamicBuilder = null;
            ClipZone.Draw(RenderContext, _clipDynamicData);
        }
        // Plumb the main RenderTarget to the full screen pass for alpha correction.
        FSPRenderTarget!.Bind(RenderContext, RenderTarget);
        FSP.Draw(RenderContext);

        RenderContext.Execute();
        return FSPRenderTarget;
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

    public void DrawFan(Vector3 center, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint innerColor, uint outerColor)
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
    private FanFill.Data.Builder GetFanFills() => _fanFillDynamicBuilder ??= _fanFillDynamicData.Map(RenderContext);

    public void DrawStroke(IEnumerable<Vector3> world, float thickness, uint color, bool closed = false)
    {
        if (!StrokeDegraded)
        {
            GetStroke().Add(world.ToArray(), thickness, color.ToVector4(), closed);
        }
    }

    private Stroke.Data.Builder GetStroke() => _strokeDynamicBuilder ??= _strokeDynamicData.Map(RenderContext);

    public void AddClipZone(Rectangle rect, float alpha = 0)
    {
        Vector2 upperleft = new(rect.X, rect.Y);
        Vector2 width = new(rect.Width, 0);
        Vector2 height = new(0, rect.Height);

        GetClipZones().Add(upperleft, alpha);
        GetClipZones().Add(upperleft + width, alpha);
        GetClipZones().Add(upperleft + width + height, alpha);

        GetClipZones().Add(upperleft, alpha);
        GetClipZones().Add(upperleft + width + height, alpha);
        GetClipZones().Add(upperleft + height, alpha);
    }
    private ClipZone.Data.Builder GetClipZones() => _clipDynamicBuilder ??= _clipDynamicData.Map(RenderContext);

    private static unsafe SharpDX.Matrix ReadMatrix(IntPtr address)
    {
        var p = (float*)address;
        SharpDX.Matrix mtx = new();
        for (var i = 0; i < 16; i++)
            mtx[i] = *p++;
        return mtx;
    }
}
