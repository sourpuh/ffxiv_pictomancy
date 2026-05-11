using FFXIVClientStructs.FFXIV.Client.Game.Control;
using SharpDX.Direct3D11;
using System.Numerics;
using System.Runtime.CompilerServices;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;

namespace Pictomancy.DXDraw;

internal class DXRenderer : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();
    internal RenderTarget? RenderTarget { get; private set; }
    public TriFill TriFill { get; init; }
    public FanFill? FanFill { get; init; }
    public ProjectedFanFill? ProjectedFanFill { get; init; }
    public ProjectedTriFill? ProjectedTriFill { get; init; }
    public Stroke? Stroke { get; init; }
    public Mirror? Mirror { get; init; }
    public FullScreenPass FSP { get; init; }
    public ClipZone ClipZone { get; init; }

    private readonly DepthStencilState _clipZoneDSS;
    private readonly DepthStencilState _shapeDSS;

    // Runs of contiguous same-type projected Adds, in user submission order.
    // Used so projected objects of different types draw in the same order they are added.
    private enum ProjectionType
    {
        Default,
        Fan,
        Tri,
    }
    private readonly List<(int Count, ProjectionType Type)> _projectedRuns = new();

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
        catch (Exception e)
        {
            PctService.Log.Error("[Pictomancy] Failed to compile fan shader; starting in degraded mode.");
        }
        try
        {
            ProjectedFanFill = new(RenderContext, options.MaxFans);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile projected fan shader; projection-mode fans will fall back to flat draw.");
        }
        try
        {
            ProjectedTriFill = new(RenderContext, Math.Max(1, options.MaxTriangleVertices / 3));
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile projected triangle shader; projection-mode triangles will fall back to flat draw.");
        }
        try
        {
            // uncomment to test linux imgui fallback renderer
            //throw new Exception("test exception please ignore");
            Stroke = new(RenderContext, options.MaxStrokeSegments);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile stroke shader; starting in degraded mode.");
        }
        try
        {
            Mirror = new(RenderContext, options.MaxMirrors);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] Failed to compile mirror shader; mirror draws will be skipped.");
        }

        // TriFill's buffer doubles as the fan-fallback path's storage in degraded mode.
        TriFill = new(RenderContext, options.MaxTriangleVertices + (FanDegraded ? options.MaxFans * 360 : 0));

        FSP = new(RenderContext);
        ClipZone = new(RenderContext, options.MaxClipZones);

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

        // Shape pass: no depth test (PS handles occlusion), stencil-equal-zero to skip clip zones.
        var shapeDesc = DepthStencilStateDescription.Default();
        shapeDesc.IsDepthEnabled = false;
        shapeDesc.DepthWriteMask = DepthWriteMask.Zero;
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
        ProjectedFanFill?.Dispose();
        ProjectedTriFill?.Dispose();
        Stroke?.Dispose();
        Mirror?.Dispose();
        ClipZone.Dispose();
        FSP.Dispose();
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
        if (rtm != null && rtm->DepthStencil != null)
        {
            resolutionScaled = rtm->DepthStencil->ActualWidth != device->Width || rtm->DepthStencil->ActualHeight != device->Height;
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

    internal unsafe RenderTarget EndFrame(ShaderResourceView? sceneDepthSRV, SharpDX.Vector2 sceneDepthUvScale, ShaderResourceView? sceneInfoSRV, ShaderResourceView? sceneNormalSRV, ShaderResourceView? charaViewSRV)
    {
        var rtSize = new Vector2(ViewportSize.X, ViewportSize.Y);
        var pixelToUv = new Vector2(sceneDepthUvScale.X, sceneDepthUvScale.Y) / rtSize;
        TriFill.UpdateConstants(new() { ViewProj = ViewProj, PixelToUv = pixelToUv });
        FanFill?.UpdateConstants(new() { ViewProj = ViewProj, PixelToUv = pixelToUv });
        Stroke?.UpdateConstants(new() { ViewProj = ViewProj, RenderTargetSize = rtSize, PixelToUv = pixelToUv });
        if (Mirror?.HasPending == true)
        {
            Mirror.UpdateConstants(new()
            {
                ViewProj = ViewProj,
                PixelToUv = pixelToUv,
                HasTexture = charaViewSRV != null ? 1f : 0f,
            });
        }
        if (ProjectedFanFill?.HasPending == true || ProjectedTriFill?.HasPending == true)
        {
            var invViewProj = ViewProj;
            invViewProj.Invert();
            var sceneCamera = Control.Instance()->CameraManager.GetActiveCamera();
            var renderCam = sceneCamera != null ? sceneCamera->SceneCamera.RenderCamera : null;
            var cameraPos = renderCam != null ? renderCam->Origin : default;
            if (ProjectedFanFill?.HasPending == true)
            {
                ProjectedFanFill.UpdateConstants(new()
                {
                    ViewProj = ViewProj,
                    InvViewProj = invViewProj,
                    RenderTargetSize = rtSize,
                    PixelToUv = pixelToUv,
                    CameraPos = cameraPos,
                });
            }
            if (ProjectedTriFill?.HasPending == true)
            {
                ProjectedTriFill.UpdateConstants(new()
                {
                    ViewProj = ViewProj,
                    InvViewProj = invViewProj,
                    RenderTargetSize = rtSize,
                    PixelToUv = pixelToUv,
                    CameraPos = cameraPos,
                });
            }
        }

        bool hasShapes = TriFill.HasPending || FanFill?.HasPending == true || ProjectedFanFill?.HasPending == true || ProjectedTriFill?.HasPending == true || Stroke?.HasPending == true || Mirror?.HasPending == true;
        if (hasShapes && sceneDepthSRV != null)
        {
            // DSV-only target with cleared stencil for the clip-zone pass.
            RenderContext.Context.OutputMerger.SetTargets(RenderTarget!.ClipStencilDSV);
            RenderContext.Context.ClearDepthStencilView(RenderTarget.ClipStencilDSV, DepthStencilClearFlags.Stencil, 0f, 0);

            if (ClipZone.HasPending)
            {
                ClipZone.UpdateConstants(new() { ViewportSize = rtSize });
                RenderContext.Context.OutputMerger.SetDepthStencilState(_clipZoneDSS, 1);
                ClipZone.Flush();
            }

            // Bind RTV+DSV; shape pass uses stencil-equal-zero, no depth test.
            RenderContext.Context.OutputMerger.SetTargets(RenderTarget.ClipStencilDSV, RenderTarget.BaseRTV);
            RenderContext.Context.OutputMerger.SetDepthStencilState(_shapeDSS, 0);

            RenderContext.Context.PixelShader.SetShaderResource(0, sceneDepthSRV);
            RenderContext.Context.PixelShader.SetShaderResource(1, sceneInfoSRV);
            RenderContext.Context.PixelShader.SetShaderResource(2, sceneNormalSRV);
        }

        FlushProjectedInOrder();
        TriFill.Flush();
        FanFill?.Flush();
        if (Mirror?.HasPending == true)
        {
            Mirror.BindCharaTexture(charaViewSRV);
            Mirror.Flush();
            Mirror.BindCharaTexture(null);
        }
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

    private void FlushProjectedInOrder()
    {
        ProjectedFanFill?.EndBuilder();
        ProjectedTriFill?.EndBuilder();

        int fanStart = 0;
        int triStart = 0;
        foreach (var (count, type) in _projectedRuns)
        {
            switch (type)
            {
                case ProjectionType.Fan:
                    ProjectedFanFill!.FlushRange(fanStart, count);
                    fanStart += count;
                    break;
                case ProjectionType.Tri:
                    ProjectedTriFill!.FlushRange(triStart, count);
                    triStart += count;
                    break;
            }
        }
        _projectedRuns.Clear();
    }

    private void AppendProjectedRun(ProjectionType type)
    {
        var lastProjection = _projectedRuns.ElementAtOrDefault(_projectedRuns.Count - 1);
        if (lastProjection.Type == type)
        {
            _projectedRuns[^1] = (lastProjection.Count + 1, type);
        }
        else
        {
            _projectedRuns.Add((1, type));
        }
    }

    public void DrawTriangle(Vector3 a, Vector3 b, Vector3 c, uint colorA, uint colorB, uint colorC, Vector3 aaMask, PctDxParams p)
    {
        if (p.ProjectionHeight > 0 && ProjectedTriFill != null)
        {
            ProjectedTriFill.Add(a, b, c, colorA, colorB, colorC, aaMask, p);
            AppendProjectedRun(ProjectionType.Tri);
        }
        else
        {
            TriFill.Add(a, b, c, colorA, colorB, colorC, aaMask, p);
        }
    }

    public void AddClipZone(Vector2 min, Vector2 max) => ClipZone.Add(min, max);

    private void DrawTriangleFan(Vector3 center, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint innerColor, uint outerColor, uint numSegments, PctDxParams p)
    {
        float totalAngle = maxAngle - minAngle;
        if (numSegments == 0) numSegments = (uint)(MathF.Abs(totalAngle) * 8);

        float angleStep = totalAngle / numSegments;
        // For full-circle fans the minA/maxA radials coincide and shouldn't be AA'd; otherwise
        // only the first segment's "prev" radial and the last segment's "offset" radial are
        // external, all other radials are internal between adjacent segments.
        bool fullCircle = MathF.Abs(totalAngle) >= 2 * MathF.PI - 1e-3f;

        Vector3 prev = new();
        for (int step = 0; step <= numSegments; step++)
        {
            float angle = MathF.PI / 2 + minAngle + step * angleStep;
            Vector3 offset = new(MathF.Cos(angle), 0, MathF.Sin(angle));

            if (step > 0)
            {
                bool firstSeg = step == 1;
                bool lastSeg = step == numSegments;
                float prevRadialMask = (firstSeg && !fullCircle) ? 1f : 0f;
                float offsetRadialMask = (lastSeg && !fullCircle) ? 1f : 0f;
                if (innerRadius > 0)
                {
                    // Donut segment, two tris share the diagonal between innerR*prev and outerR*offset.
                    // Tri 1 (innerR*prev, outerR*prev, outerR*offset): edge BC = outer arc (ext),
                    // CA = diagonal (int), AB = prev radial.
                    var mask1 = new Vector3(1f, 0f, prevRadialMask);
                    DrawTriangle(center + innerRadius * prev, center + outerRadius * prev, center + outerRadius * offset, innerColor, outerColor, outerColor, mask1, p);
                    // Tri 2 (outerR*offset, innerR*offset, innerR*prev): edge BC = inner arc (ext),
                    // CA = diagonal (int), AB = offset radial.
                    var mask2 = new Vector3(1f, 0f, offsetRadialMask);
                    DrawTriangle(center + outerRadius * offset, center + innerRadius * offset, center + innerRadius * prev, outerColor, innerColor, innerColor, mask2, p);
                }
                else
                {
                    // Cone segment: single tri (center, outerR*prev, outerR*offset). Outer arc is BC,
                    // radials are CA (offset) and AB (prev).
                    var mask = new Vector3(1f, offsetRadialMask, prevRadialMask);
                    DrawTriangle(center, center + outerRadius * prev, center + outerRadius * offset, innerColor, outerColor, outerColor, mask, p);
                }
            }
            prev = offset;
        }
    }
    public void DrawFan(Vector3 center, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint innerColor, uint outerColor, uint numSegments, PctDxParams p)
    {
        bool project = p.ProjectionHeight > 0;
        if (project && ProjectedFanFill != null && numSegments == 0)
        {
            ProjectedFanFill.Add(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor, p);
            AppendProjectedRun(ProjectionType.Fan);
        }
        else if (!project && !FanDegraded && numSegments == 0)
        {
            FanFill!.Add(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor, p);
        }
        else
        {
            DrawTriangleFan(center, innerRadius, outerRadius, minAngle, maxAngle, innerColor, outerColor, numSegments, p);
        }
    }

    public void DrawStroke(IEnumerable<Vector3> world, float thickness, uint color, bool closed, PctDxParams p)
    {
        Stroke?.Add(world.ToArray(), thickness, color, closed, p);
    }

    public void DrawMirror(Vector3 center, Vector3 facing, float width, float aspect, uint color, PctDxParams p)
    {
        Mirror?.Add(center, facing, width, aspect, color, p);
    }

    private static unsafe SharpDX.Matrix ReadMatrix(IntPtr address)
    {
        var p = (float*)address;
        SharpDX.Matrix mtx = new();
        for (var i = 0; i < 16; i++)
            mtx[i] = *p++;
        return mtx;
    }

    internal static int AlignTo16<T>() where T : unmanaged => ((Unsafe.SizeOf<T>() + 15) & ~15);
}
