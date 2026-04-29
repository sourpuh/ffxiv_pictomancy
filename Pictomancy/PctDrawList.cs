using Dalamud.Interface.Utility;
using Pictomancy.DXDraw;
using Pictomancy.ImGuiDraw;
using System.Drawing;
using System.Numerics;

namespace Pictomancy;
public class PctDrawList : IDisposable
{
    internal readonly ImDrawListPtr _drawList;
    internal readonly ImDrawListPtr _textDrawList;
    internal readonly List<Vector3> _path;
    internal readonly DXRenderer _renderer;
    internal readonly SceneDepth _sceneDepth;
    internal readonly ImGuiRenderer _fallbackRenderer;
    internal readonly bool isMyWindow;
    private PctTexture? _texture;
    internal bool Finalized => _texture != null;

    /// <summary>Default rendering params applied to any shape that doesn't pass an explicit override.</summary>
    public PctDxParams DefaultParams { get; }

    internal PctDrawList(ImDrawListPtr? drawlist, DXRenderer renderer, SceneDepth sceneDepth, PctDxParams? defaultParams = null)
    {
        DefaultParams = defaultParams ?? new PctDxParams();
        if (drawlist != null)
        {
            _drawList = _textDrawList = (ImDrawListPtr)drawlist;
        }
        else
        {
            _drawList = ImGui.GetBackgroundDrawList();
            ImGuiHelpers.ForceNextWindowMainViewport();
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
            ImGuiHelpers.SetNextWindowPosRelativeMainViewport(Vector2.Zero);
            ImGui.SetNextWindowSize(ImGuiHelpers.MainViewport.Size);
            if (ImGui.Begin("PctWindow#" + PctService.PluginInterface.InternalName, ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysUseWindowPadding | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoFocusOnAppearing))
            {
                CImGui.igBringWindowToDisplayBack(CImGui.igGetCurrentWindow());
                _textDrawList = ImGui.GetWindowDrawList();
                isMyWindow = true;
            }
            else
            {
                _textDrawList = ImGui.GetBackgroundDrawList();
            }
            ImGui.PopStyleVar();
        }
        _path = new();
        _renderer = renderer;
        _sceneDepth = sceneDepth;
        _texture = null;
        _renderer.BeginFrame();
        _sceneDepth.Update();
        _fallbackRenderer = new(_drawList);
    }

    public PctTexture DrawToTexture()
    {
        if (_texture == null)
        {
            var target = _renderer.EndFrame(_sceneDepth.SRV, _sceneDepth.UvScale);
            _texture = target.Texture;
        }
        return _texture.Value;
    }

    public void Dispose()
    {
        if (PctService.DrawList == this) PctService.DrawList = null;

        PctTexture texture = DrawToTexture();
        switch (PctService.Hints.AutoDraw)
        {
            case AutoDraw.NativeOverlay:
                PctService.OverlayNode.IsVisible = true;
                PctService.OverlayNode.UpdateTexture(_renderer.RenderTarget?.ProcessedTexture, _renderer.RenderTarget?.ProcessedSRV);
                break;
            case AutoDraw.ImGuiOverlay:
                _drawList.AddImage(
                    texture.TextureId,
                    ImGuiHelpers.MainViewport.Pos,
                    ImGuiHelpers.MainViewport.Pos + texture.Size);
                goto default;
            default:
                PctService.OverlayNode.IsVisible = false;
                break;
        }

        if (isMyWindow)
            ImGui.End();
    }

    /// <summary>
    /// Add text to a position in world space using default font and size.
    /// Currently uses Imgui to draw thus is not clipped.
    /// </summary>
    /// <param name="position">Position in world space</param>
    /// <param name="color">Text color</param>
    /// <param name="text">Text to draw</param>
    /// <param name="scale">Scale to draw; looks bad when using high numbers; let me know if you actually want that fixed.</param>
    public void AddText(Vector3 position, uint color, string text, float scale = 1)
    {
        if (!PctService.GameGui.WorldToScreen(position, out var position2D))
        {
            return;
        }
        ImGui.SetWindowFontScale(scale);
        var textPosition = position2D - (ImGui.CalcTextSize(text) / 2f);
        _textDrawList.AddText(textPosition, color, text);
        ImGui.SetWindowFontScale(1f);
    }

    /// <summary>
    /// Add dot to a position in world space.
    /// Currently uses Imgui to draw thus is not clipped.
    /// </summary>
    /// <param name="position">Position in world space</param>
    /// <param name="radiusPixels">Dot radius in pixels</param>
    /// <param name="color">Dot color</param>
    /// <param name="numSegments">Number of segments used to draw dot</param>
    public void AddDot(Vector3 position, float radiusPixels, uint color, uint numSegments = 0)
    {
        if (!PctService.GameGui.WorldToScreen(position, out var position2D))
        {
            return;
        }
        _drawList.AddCircleFilled(position2D, radiusPixels, color, (int)numSegments);
    }

    /// <summary>
    /// Add a screen-space rectangle that excludes pictomancy shapes from being drawn inside it.
    /// Coordinates are in pixels relative to the main viewport (same convention as ImGui).
    /// </summary>
    public void AddClipZone(Rectangle rectangle)
    {
        _renderer.AddClipZone(new(rectangle.Left, rectangle.Top), new(rectangle.Right, rectangle.Bottom));
    }

    /// <summary>
    /// Add a screen-space rectangle that excludes pictomancy shapes from being drawn inside it.
    /// Coordinates are in pixels relative to the main viewport (same convention as ImGui).
    /// </summary>
    public void AddClipZone(Vector2 min, Vector2 max)
    {
        _renderer.AddClipZone(min, max);
    }

    public void PathLineTo(Vector3 point)
    {
        _path.Add(point);
    }

    public void PathArcTo(Vector3 point, float radius, float startAngle, float stopAngle, uint numSegments = 0)
    {
        float totalAngle = stopAngle - startAngle;
        if (numSegments == 0) numSegments = (uint)(MathF.Abs(totalAngle) * 16);

        float angleStep = totalAngle / numSegments;

        for (int step = 0; step <= numSegments; step++)
        {
            float angle = MathF.PI / 2 + startAngle + step * angleStep;
            Vector3 offset = new(MathF.Cos(angle), 0, MathF.Sin(angle));
            _path.Add(point + radius * offset);
        }
    }

    public void PathStroke(uint color, PctStrokeFlags flags = default, float thickness = 2f, PctDxParams? p = null)
    {
        if (_renderer.StrokeDegraded)
        {
            _fallbackRenderer.DrawStroke(_path, thickness, color, (flags & PctStrokeFlags.Closed) > 0);
        }
        else
        {
            _renderer.DrawStroke(_path, thickness, color, (flags & PctStrokeFlags.Closed) > 0, p ?? DefaultParams);
        }
        _path.Clear();
    }

    public void AddTriangleFilled(Vector3 a, Vector3 b, Vector3 c, uint color, PctDxParams? p = null)
    {
        AddTriangleFilled(a, b, c, color, color, color, p);
    }

    public void AddTriangleFilled(Vector3 a, Vector3 b, Vector3 c, uint colorA, uint colorB, uint colorC, PctDxParams? p = null)
    {
        _renderer.DrawTriangle(a, b, c, colorA, colorB, colorC, p ?? DefaultParams);
    }

    public void AddLine(Vector3 start, Vector3 stop, float halfWidth, uint color, float thickness = 2, PctDxParams? p = null)
    {
        Vector3 direction = stop - start;
        Vector3 perpendicular = halfWidth * Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        AddQuad(start - perpendicular, stop - perpendicular, stop + perpendicular, start + perpendicular, color, thickness, p);
    }

    public void AddLineFilled(Vector3 start, Vector3 stop, float halfWidth, uint color, uint? outerColor = null, PctDxParams? p = null)
    {
        Vector3 direction = stop - start;
        Vector3 perpendicular = halfWidth * Vector3.Normalize(Vector3.Cross(direction, Vector3.UnitY));
        AddQuadFilled(start - perpendicular, stop - perpendicular, stop + perpendicular, start + perpendicular, color, outerColor ?? color, outerColor ?? color, color, p);
    }

    public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, uint color, float thickness = 2, PctDxParams? p = null)
    {
        PathLineTo(a);
        PathLineTo(b);
        PathLineTo(c);
        PathLineTo(d);
        PathStroke(color, PctStrokeFlags.Closed, thickness, p);
    }

    public void AddQuadFilled(Vector3 a, Vector3 b, Vector3 c, Vector3 d, uint color, PctDxParams? p = null)
    {
        AddQuadFilled(a, b, c, d, color, color, color, color, p);
    }

    public void AddQuadFilled(Vector3 a, Vector3 b, Vector3 c, Vector3 d, uint colorA, uint colorB, uint colorC, uint colorD, PctDxParams? p = null)
    {
        AddTriangleFilled(a, b, c, colorA, colorB, colorC, p);
        AddTriangleFilled(a, c, d, colorA, colorC, colorD, p);
    }

    public void AddCircle(Vector3 origin, float radius, uint color, uint numSegments = 0, float thickness = 2, PctDxParams? p = null)
    {
        PathArcTo(origin, radius, 0, 2 * MathF.PI, numSegments);
        PathStroke(color, PctStrokeFlags.Closed, thickness, p);
    }

    public void AddCircleFilled(Vector3 origin, float radius, uint color, uint? outerColor = null, uint numSegments = 0, PctDxParams? p = null)
    {
        AddFanFilled(origin, 0, radius, 0, 2 * MathF.PI, color, outerColor, numSegments, p);
    }

    public void AddArc(Vector3 origin, float radius, float minAngle, float maxAngle, uint color, uint numSegments = 0, float thickness = 2, PctDxParams? p = null)
    {
        PathArcTo(origin, radius, minAngle, maxAngle, numSegments);
        PathStroke(color, PctStrokeFlags.None, thickness, p);
    }

    public void AddArcFilled(Vector3 origin, float radius, float minAngle, float maxAngle, uint color, uint? outerColor = null, uint numSegments = 0, PctDxParams? p = null)
    {
        AddFanFilled(origin, 0, radius, minAngle, maxAngle, color, outerColor, numSegments, p);
    }

    public void AddConeFilled(Vector3 origin, float radius, float rotation, float angle, uint color, uint? outerColor = null, uint numSegments = 0, PctDxParams? p = null)
    {
        var halfAngle = angle / 2;
        AddFanFilled(origin, 0, radius, rotation - halfAngle, rotation + halfAngle, color, outerColor, numSegments, p);
    }

    public void AddFan(Vector3 origin, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint color, uint numSegments = 0, float thickness = 2, PctDxParams? p = null)
    {
        bool isCircle = maxAngle - minAngle >= 2 * MathF.PI - 0.000001;
        PathArcTo(origin, outerRadius, minAngle, maxAngle, numSegments);
        if (innerRadius > 0)
        {
            if (isCircle)
            {
                PathStroke(color, PctStrokeFlags.Closed, thickness, p);
            }
            PathArcTo(origin, innerRadius, maxAngle, minAngle, numSegments);
        }
        else if (!isCircle)
        {
            PathLineTo(origin);
        }
        PathStroke(color, PctStrokeFlags.Closed, thickness, p);
    }

    public void AddFanFilled(Vector3 origin, float innerRadius, float outerRadius, float minAngle, float maxAngle, uint color, uint? outerColor = null, uint numSegments = 0, PctDxParams? p = null)
    {
        _renderer.DrawFan(origin, innerRadius, outerRadius, minAngle, maxAngle, color, outerColor ?? color, numSegments, p ?? DefaultParams);
    }
}
