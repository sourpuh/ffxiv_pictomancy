using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Pictomancy;

namespace PictomancyDemo;

public sealed class DemoWindow : Window, IDisposable
{
    // Master switch; when false, the plugin makes no Pictomancy calls.
    public bool WorldDrawEnabled = false;

    // Drawing toggles
    private bool _drawCircle = true;
    private bool _drawCircleFilled = true;
    private bool _drawDonut;
    private bool _drawCone;
    private bool _drawArc;
    private bool _drawLineToTarget;
    private bool _drawTextLabel;
    private bool _drawDot;
    private bool _drawTriangle;
    private bool _drawClipZone;
    private Vector4 _clipZoneRect = new(200, 200, 300, 200); // viewport-relative

    // Sizes / params
    private float _circleRadius = 5f;
    private float _circleThickness = 2f;
    private float _donutInner = 3f;
    private float _donutOuter = 8f;
    private float _coneRadius = 10f;
    private float _coneAngleDeg = 90f;
    private float _arcAngleDeg = 270f;
    private float _lineHalfWidth = 0.5f;
    private float _dotRadiusPx = 6f;
    private string _label = "The joy of Pictomancy!";

    // Colors (ABGR for ImGui packing)
    private Vector4 _fillColor = new(1f, 0.4f, 0.2f, 0.35f);
    private Vector4 _outlineColor = new(1f, 0.6f, 0.2f, 1f);
    private Vector4 _accentColor = new(0.3f, 0.9f, 1f, 1f);

    // Hints
    private int _autoDraw = (int)AutoDraw.ImGuiOverlay;
    private bool _drawInCutscene;
    private bool _drawWhenFaded;
    private int _maxAlpha = 255;
    private int _blendMode = (int)AlphaBlendMode.Add;
    private int _uiMask = (int)UIMask.BackbufferAlpha;
    private float _occludedAlpha = 0f;
    private float _occlusionTolerance = 0f;
    private float _fadeStart = float.PositiveInfinity;
    private float _fadeStop = float.PositiveInfinity;

    public DemoWindow()
        : base("Pictomancy Demo", ImGuiWindowFlags.AlwaysAutoResize)
    {
        IsOpen = false;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Checkbox("Draw enabled (master switch)", ref WorldDrawEnabled);
        ImGui.Separator();

        if (DemoPlugin.Objects.LocalPlayer is null)
        {
            ImGui.TextUnformatted("Log into the game to draw.");
            return;
        }

        if (ImGui.CollapsingHeader("Hints", ImGuiTreeNodeFlags.DefaultOpen))
        {
            string[] autoDrawNames = Enum.GetNames(typeof(AutoDraw));
            ImGui.Combo("Auto draw", ref _autoDraw, autoDrawNames, autoDrawNames.Length);

            ImGui.Checkbox("Draw in cutscene", ref _drawInCutscene);
            ImGui.SameLine();
            ImGui.Checkbox("Draw when faded", ref _drawWhenFaded);

            ImGui.SliderInt("Max alpha", ref _maxAlpha, 0, 255);
            string[] modes = Enum.GetNames(typeof(AlphaBlendMode));
            ImGui.Combo("Blend mode", ref _blendMode, modes, modes.Length);
            string[] uiMaskNames = Enum.GetNames(typeof(UIMask));
            ImGui.Combo("UI mask", ref _uiMask, uiMaskNames, uiMaskNames.Length);

            ImGui.SliderFloat("Occluded alpha", ref _occludedAlpha, 0f, 1f);
            ImGui.SliderFloat("Occlusion tolerance (m)", ref _occlusionTolerance, 0f, 5f);
            ImGui.SliderFloat("Fade start (m)", ref _fadeStart, 0f, 200f);
            ImGui.SliderFloat("Fade stop (m)", ref _fadeStop, 0f, 200f);
        }

        if (ImGui.CollapsingHeader("Shapes", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("Circle (outline)", ref _drawCircle);
            ImGui.Checkbox("Circle (filled)", ref _drawCircleFilled);
            ImGui.Checkbox("Donut", ref _drawDonut);
            ImGui.Checkbox("Cone (forward)", ref _drawCone);
            ImGui.Checkbox("Arc", ref _drawArc);
            ImGui.Checkbox("Line to target", ref _drawLineToTarget);
            ImGui.Checkbox("Triangle", ref _drawTriangle);
            ImGui.Checkbox("Dot at player", ref _drawDot);
            ImGui.Checkbox("Text label above player", ref _drawTextLabel);
            ImGui.Checkbox("Clip zone (rect)", ref _drawClipZone);
        }

        if (ImGui.CollapsingHeader("Parameters"))
        {
            ImGui.SliderFloat("Circle radius", ref _circleRadius, 0.5f, 30f);
            ImGui.SliderFloat("Circle thickness", ref _circleThickness, 0.5f, 10f);
            ImGui.SliderFloat("Donut inner", ref _donutInner, 0f, 20f);
            ImGui.SliderFloat("Donut outer", ref _donutOuter, 1f, 30f);
            ImGui.SliderFloat("Cone radius", ref _coneRadius, 1f, 30f);
            ImGui.SliderFloat("Cone angle (deg)", ref _coneAngleDeg, 5f, 360f);
            ImGui.SliderFloat("Arc angle (deg)", ref _arcAngleDeg, 5f, 360f);
            ImGui.SliderFloat("Line half-width", ref _lineHalfWidth, 0.05f, 5f);
            ImGui.SliderFloat("Dot radius (px)", ref _dotRadiusPx, 1f, 30f);
            ImGui.InputText("Label", ref _label, 64);
            ImGui.SliderFloat4("Clip zone (x,y,w,h)", ref _clipZoneRect, 0f, 2000f);
        }

        if (ImGui.CollapsingHeader("Colors"))
        {
            ImGui.ColorEdit4("Fill", ref _fillColor);
            ImGui.ColorEdit4("Outline", ref _outlineColor);
            ImGui.ColorEdit4("Accent", ref _accentColor);
        }
    }

    public void DrawWorld()
    {
        var player = DemoPlugin.Objects.LocalPlayer;
        if (player is null) return;

        var hints = new PctDrawHints
        {
            DrawInCutscene = _drawInCutscene,
            DrawWhenFaded = _drawWhenFaded,
            AutoDraw = (AutoDraw)_autoDraw,
            MaxAlpha = (byte)_maxAlpha,
            AlphaBlendMode = (AlphaBlendMode)_blendMode,
            UIMask = (UIMask)_uiMask,
            DefaultParams = new PctDxParams
            {
                OccludedAlpha = _occludedAlpha,
                OcclusionTolerance = _occlusionTolerance,
                FadeStart = _fadeStart,
                FadeStop = _fadeStop,
            },
        };

        using var draw = PctService.Draw(hints: hints);
        if (draw is null) return;

        var origin = player.Position;
        var forward = new Vector3(MathF.Sin(player.Rotation), 0, MathF.Cos(player.Rotation));

        var fill = ImGui.ColorConvertFloat4ToU32(_fillColor);
        var outline = ImGui.ColorConvertFloat4ToU32(_outlineColor);
        var accent = ImGui.ColorConvertFloat4ToU32(_accentColor);

        if (_drawCircleFilled)
            draw.AddCircleFilled(origin, _circleRadius, fill);
        if (_drawCircle)
            draw.AddCircle(origin, _circleRadius, outline, thickness: _circleThickness);

        if (_drawDonut)
        {
            draw.AddFanFilled(origin, _donutInner, _donutOuter, 0, 2 * MathF.PI, fill);
            draw.AddFan(origin, _donutInner, _donutOuter, 0, 2 * MathF.PI, outline, thickness: _circleThickness);
        }

        if (_drawCone)
        {
            float half = MathF.PI * _coneAngleDeg / 360f;
            draw.AddConeFilled(origin, _coneRadius, player.Rotation, MathF.PI * _coneAngleDeg / 180f, fill);
            draw.AddFan(origin, 0, _coneRadius, player.Rotation - half, player.Rotation + half, outline, thickness: _circleThickness);
        }

        if (_drawArc)
        {
            float half = MathF.PI * _arcAngleDeg / 360f;
            draw.AddArc(origin, _circleRadius, -half, half, accent, thickness: _circleThickness);
        }

        if (_drawLineToTarget && DemoPlugin.TargetManager.Target is { } target)
        {
            draw.AddLineFilled(origin, target.Position, _lineHalfWidth, fill, accent);
        }

        if (_drawTriangle)
        {
            var a = origin + forward * 6f;
            var b = origin + forward * 3f + Vector3.Cross(forward, Vector3.UnitY) * 3f;
            var c = origin + forward * 3f - Vector3.Cross(forward, Vector3.UnitY) * 3f;
            draw.AddTriangleFilled(a, b, c, accent);
        }

        if (_drawDot)
            draw.AddDot(origin, _dotRadiusPx, accent);

        if (_drawTextLabel)
            draw.AddText(origin + new Vector3(0, 2.5f, 0), accent, _label);

        if (_drawClipZone)
        {
            var min = new Vector2(_clipZoneRect.X, _clipZoneRect.Y);
            var max = min + new Vector2(_clipZoneRect.Z, _clipZoneRect.W);
            draw.AddClipZone(min, max);
        }
    }
}
