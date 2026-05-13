using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Pictomancy;

namespace PictomancyDemo;

public sealed class DemoWindow : Window, IDisposable
{
    // Master switch; when false, the plugin makes no Pictomancy calls.
    public bool WorldDrawEnabled = false;

    // Hints (global)
    private int _autoDraw = (int)AutoDraw.ImGuiOverlay;
    private bool _drawInCutscene;
    private bool _drawWhenFaded;
    private int _maxAlpha = 255;
    private int _blendMode = (int)AlphaBlendMode.Add;
    private int _uiMask = (int)UIMask.BackbufferSubtraction;
    private float _occludedAlpha = 0f;
    private float _occlusionTolerance = 0.02f;
    private float _fadeStart = float.PositiveInfinity;
    private float _fadeStop = float.PositiveInfinity;

    // Spawned demo objects.
    private readonly List<DemoObject> _objects = new();
    private int _nextId = 1;

    // Render-time graph: circular buffer of recent DrawWorld() durations in ms. The linear copy
    // is rebuilt each Draw() because the ImGui binding does not expose values_offset.
    private const int PlotSamples = 300;
    private readonly float[] _renderMs = new float[PlotSamples];
    private readonly float[] _renderMsLinear = new float[PlotSamples];
    private int _renderMsIdx;
    private readonly Stopwatch _renderSw = new();

    public DemoWindow() : base("Pictomancy Demo", ImGuiWindowFlags.AlwaysAutoResize)
    {
        IsOpen = false;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Checkbox("Draw enabled (master switch)", ref WorldDrawEnabled);

        float current = _renderMs[(_renderMsIdx - 1 + PlotSamples) % PlotSamples];
        float peak = 0f;
        for (int i = 0; i < PlotSamples; i++)
        {
            _renderMsLinear[i] = _renderMs[(_renderMsIdx + i) % PlotSamples];
            if (_renderMsLinear[i] > peak) peak = _renderMsLinear[i];
        }
        ImGui.TextUnformatted($"Render: {current:F2} ms (peak {peak:F2} ms)");
        ImGui.PlotLines("##rt", _renderMsLinear, PlotSamples, "", 0f, float.MaxValue, new Vector2(0, 60));

        ImGui.Separator();

        var player = DemoPlugin.Objects.LocalPlayer;
        if (player is null)
        {
            ImGui.TextUnformatted("Log into the game to draw.");
            return;
        }

        if (ImGui.CollapsingHeader("Hints"))
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

        ImGui.TextUnformatted("Spawn at player position:");
        var pos = player.Position;
        var rotation = player.Rotation;
        if (ImGui.Button("Fan")) Spawn(new FanObject { Position = pos, Rotation = rotation });
        ImGui.SameLine();
        if (ImGui.Button("Triangle")) Spawn(new TriangleObject { Position = pos, Rotation = rotation });
        ImGui.SameLine();
        if (ImGui.Button("Line to target")) Spawn(new LineObject { Position = pos });
        if (ImGui.Button("Text")) Spawn(new TextObject { Position = pos });
        ImGui.SameLine();
        if (ImGui.Button("Dot")) Spawn(new DotObject { Position = pos });
        ImGui.SameLine();
        if (ImGui.Button("Clip zone")) Spawn(new ClipZoneObject());
        ImGui.SameLine();
        if (ImGui.Button("Sphere")) Spawn(new SphereObject { Position = pos });
        ImGui.SameLine();
        if (ImGui.Button("Clear all")) _objects.Clear();

        ImGui.Separator();
        ImGui.TextUnformatted($"Spawned objects ({_objects.Count}):");

        int? toRemove = null;
        for (int i = 0; i < _objects.Count; i++)
        {
            var obj = _objects[i];
            ImGui.PushID(obj.Id);
            string header = $"#{obj.Id} {obj.TypeName} @ ({obj.Position.X:F1}, {obj.Position.Y:F1}, {obj.Position.Z:F1})###obj{obj.Id}";
            if (ImGui.CollapsingHeader(header))
            {
                obj.DrawUi();
                if (ImGui.Button("Move to player")) obj.Position = pos;
                ImGui.SameLine();
                if (ImGui.Button("Remove")) toRemove = i;
            }
            ImGui.PopID();
        }
        if (toRemove.HasValue) _objects.RemoveAt(toRemove.Value);
    }

    private void Spawn(DemoObject obj)
    {
        obj.Id = _nextId++;
        _objects.Add(obj);
    }

    public void DrawWorld()
    {
        _renderSw.Restart();
        try
        {
            DrawWorldInner();
        }
        finally
        {
            _renderSw.Stop();
            _renderMs[_renderMsIdx] = (float)_renderSw.Elapsed.TotalMilliseconds;
            _renderMsIdx = (_renderMsIdx + 1) % PlotSamples;
        }
    }

    private void DrawWorldInner()
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

        foreach (var obj in _objects)
            obj.DrawWorld(draw, hints.DefaultParams);
    }

    private abstract class DemoObject
    {
        public int Id;
        public Vector3 Position;
        public abstract string TypeName { get; }
        public abstract void DrawUi();
        public abstract void DrawWorld(PctDrawList draw, PctDxParams baseParams);
    }

    private static uint ToU32(Vector4 c) => ImGui.ColorConvertFloat4ToU32(c);

    private static void ProjectionHeightSlider(ref float h)
    {
        ImGui.SliderFloat("Projection height (m, 0=off)", ref h, 0f, 10f);
    }

    private struct FresnelControls
    {
        public float Spread;
        public float Intensity;
        public float Opacity;
        public FresnelControls() { Spread = 0.25f; Intensity = 0.5f; Opacity = 0.3f; }

        public void DrawUi()
        {
            ImGui.SliderFloat("Fresnel spread (0 = no rim)", ref Spread, 0f, 2f);
            ImGui.SliderFloat("Fresnel intensity (rgb)", ref Intensity, 0f, 2f);
            ImGui.SliderFloat("Fresnel opacity (alpha)", ref Opacity, 0f, 1f);
        }

        public readonly PctDxParams Apply(PctDxParams p) => p with
        {
            FresnelSpread = Spread,
            FresnelIntensity = Intensity,
            FresnelOpacity = Opacity,
        };
    }

    private class FanObject : DemoObject
    {
        public float Rotation;
        public float InnerRadius = 0f;
        public float OuterRadius = 5f;
        public float AngleDeg = 360f;
        public int NumSegments = 0;
        public float Thickness = 2f;
        public Vector4 FillColor = new(1f, 0.4f, 0.2f, 0.35f);
        public Vector4 FillOuterColor = new(1f, 0.4f, 0.2f, 0.35f);
        public Vector4 OutlineColor = new(1f, 0.6f, 0.2f, 1f);
        public bool DrawFill = true;
        public bool DrawOutline = true;
        public float ProjectionHeight = 0f;
        public FresnelControls Fresnel = new();
        public override string TypeName => "Fan";
        public override void DrawUi()
        {
            ImGui.Checkbox("Fill", ref DrawFill);
            ImGui.SameLine();
            ImGui.Checkbox("Outline", ref DrawOutline);
            ImGui.SliderFloat("Inner radius", ref InnerRadius, 0f, 20f);
            ImGui.SliderFloat("Outer radius", ref OuterRadius, 0.5f, 30f);
            ImGui.SliderFloat("Angle (deg)", ref AngleDeg, 1f, 360f);
            ImGui.SliderAngle("Rotation", ref Rotation);
            ImGui.SliderInt("Segments (0 = auto)", ref NumSegments, 0, 128);
            ImGui.SliderFloat("Thickness", ref Thickness, 0.5f, 10f);
            ImGui.ColorEdit4("Fill color (inner)", ref FillColor);
            ImGui.ColorEdit4("Fill color (outer)", ref FillOuterColor);
            ImGui.ColorEdit4("Outline color", ref OutlineColor);
            ProjectionHeightSlider(ref ProjectionHeight);
            Fresnel.DrawUi();
        }
        public override void DrawWorld(PctDrawList draw, PctDxParams baseParams)
        {
            var p = Fresnel.Apply(baseParams) with { ProjectionHeight = ProjectionHeight };
            float half = MathF.PI * AngleDeg / 360f;
            float minA = Rotation - half;
            float maxA = Rotation + half;
            uint segments = (uint)NumSegments;
            if (DrawFill)
                draw.AddFanFilled(Position, InnerRadius, OuterRadius, minA, maxA, ToU32(FillColor), ToU32(FillOuterColor), segments, p);
            if (DrawOutline)
                draw.AddFan(Position, InnerRadius, OuterRadius, minA, maxA, ToU32(OutlineColor), segments, Thickness, p);
        }
    }

    private class TriangleObject : DemoObject
    {
        public float Rotation;
        public float Reach = 6f;
        public float HalfBase = 3f;
        public float TiltA = 0f;
        public float TiltB = 0f;
        public float TiltC = 0f;
        public Vector4 ColorA = new(0.3f, 0.9f, 1f, 0.6f);
        public Vector4 ColorB = new(0.3f, 0.9f, 1f, 0.6f);
        public Vector4 ColorC = new(0.3f, 0.9f, 1f, 0.6f);
        public float ProjectionHeight = 0f;
        public FresnelControls Fresnel = new();
        public override string TypeName => "Triangle";
        public override void DrawUi()
        {
            ImGui.SliderFloat("Reach", ref Reach, 1f, 30f);
            ImGui.SliderFloat("Half base", ref HalfBase, 0.5f, 20f);
            ImGui.SliderAngle("Rotation", ref Rotation);
            ImGui.SliderFloat("Tilt A (tip)", ref TiltA, -5f, 5f);
            ImGui.SliderFloat("Tilt B (right)", ref TiltB, -5f, 5f);
            ImGui.SliderFloat("Tilt C (left)", ref TiltC, -5f, 5f);
            ImGui.ColorEdit4("Color A (tip)", ref ColorA);
            ImGui.ColorEdit4("Color B (right)", ref ColorB);
            ImGui.ColorEdit4("Color C (left)", ref ColorC);
            ProjectionHeightSlider(ref ProjectionHeight);
            Fresnel.DrawUi();
        }
        public override void DrawWorld(PctDrawList draw, PctDxParams baseParams)
        {
            var p = Fresnel.Apply(baseParams) with { ProjectionHeight = ProjectionHeight };
            var fwd = new Vector3(MathF.Sin(Rotation), 0, MathF.Cos(Rotation));
            var right = Vector3.Cross(fwd, Vector3.UnitY);
            var a = Position + fwd * Reach + Vector3.UnitY * TiltA;
            var b = Position + fwd * (Reach * 0.5f) + right * HalfBase + Vector3.UnitY * TiltB;
            var c = Position + fwd * (Reach * 0.5f) - right * HalfBase + Vector3.UnitY * TiltC;
            draw.AddTriangleFilled(a, b, c, ToU32(ColorA), ToU32(ColorB), ToU32(ColorC), p: p);
        }
    }

    private class LineObject : DemoObject
    {
        public float HalfWidth = 0.5f;
        public Vector4 Color = new(1f, 0.6f, 0.2f, 1f);
        public Vector4 OuterColor = new(1f, 0.6f, 0.2f, 1f);
        public Vector4 OutlineColor = new(1f, 0.9f, 0.4f, 1f);
        public float Thickness = 2f;
        public bool DrawFill = true;
        public bool DrawOutline = true;
        public float ProjectionHeight = 0f;
        public FresnelControls Fresnel = new();
        public override string TypeName => "Line to target";
        public override void DrawUi()
        {
            ImGui.Checkbox("Fill", ref DrawFill);
            ImGui.SameLine();
            ImGui.Checkbox("Outline", ref DrawOutline);
            ImGui.SliderFloat("Half width", ref HalfWidth, 0.05f, 5f);
            ImGui.SliderFloat("Thickness", ref Thickness, 0.5f, 10f);
            ImGui.ColorEdit4("Color (center)", ref Color);
            ImGui.ColorEdit4("Color (edge)", ref OuterColor);
            ImGui.ColorEdit4("Outline color", ref OutlineColor);
            ProjectionHeightSlider(ref ProjectionHeight);
            Fresnel.DrawUi();
            ImGui.TextUnformatted("Endpoint follows the current target.");
        }
        public override void DrawWorld(PctDrawList draw, PctDxParams baseParams)
        {
            if (DemoPlugin.TargetManager.Target is not { } target) return;
            var p = Fresnel.Apply(baseParams) with { ProjectionHeight = ProjectionHeight };
            if (DrawFill)
                draw.AddLineFilled(Position, target.Position, HalfWidth, ToU32(Color), ToU32(OuterColor), p: p);
            if (DrawOutline)
                draw.AddLine(Position, target.Position, HalfWidth, ToU32(OutlineColor), Thickness, p);
        }
    }

    private class TextObject : DemoObject
    {
        public string Label = "The joy of Pictomancy!";
        public float Scale = 1f;
        public float HeightOffset = 2.5f;
        public Vector4 Color = new(0.3f, 0.9f, 1f, 1f);
        public override string TypeName => "Text";
        public override void DrawUi()
        {
            ImGui.InputText("Label", ref Label, 64);
            ImGui.SliderFloat("Scale", ref Scale, 0.1f, 8f);
            ImGui.SliderFloat("Height offset", ref HeightOffset, 0f, 5f);
            ImGui.ColorEdit4("Color", ref Color);
        }
        public override void DrawWorld(PctDrawList draw, PctDxParams baseParams)
        {
            draw.AddText(Position + new Vector3(0, HeightOffset, 0), ToU32(Color), Label, Scale);
        }
    }

    private class DotObject : DemoObject
    {
        public float RadiusPx = 6f;
        public Vector4 Color = new(0.3f, 0.9f, 1f, 1f);
        public override string TypeName => "Dot";
        public override void DrawUi()
        {
            ImGui.SliderFloat("Radius (px)", ref RadiusPx, 1f, 30f);
            ImGui.ColorEdit4("Color", ref Color);
        }
        public override void DrawWorld(PctDrawList draw, PctDxParams baseParams)
        {
            draw.AddDot(Position, RadiusPx, ToU32(Color));
        }
    }

    // 3D sphere at the spawn position, lifted to player chest height by default.
    private class SphereObject : DemoObject
    {
        public float Radius = 2f;
        public float HeightOffset = 1f;
        public Vector4 Color = new(0.4f, 0.7f, 1f, 0.5f);
        public FresnelControls Fresnel = new() { Opacity = 0.6f };
        public override string TypeName => "Sphere";
        public override void DrawUi()
        {
            ImGui.SliderFloat("Radius (m)", ref Radius, 0.1f, 30f);
            ImGui.SliderFloat("Height offset (m)", ref HeightOffset, 0f, 10f);
            ImGui.ColorEdit4("Color", ref Color);
            Fresnel.DrawUi();
        }
        public override void DrawWorld(PctDrawList draw, PctDxParams baseParams)
        {
            draw.AddSphere(Position + new Vector3(0, HeightOffset, 0), Radius, ToU32(Color), Fresnel.Apply(baseParams));
        }
    }

    private class ClipZoneObject : DemoObject
    {
        public Vector2 Min = new(400, 300);
        public Vector2 Max = new(800, 500);
        public bool ShowOutline = true;
        public override string TypeName => "Clip zone";
        public override void DrawUi()
        {
            var vp = ImGuiHelpers.MainViewport.Size;
            float maxExtent = MathF.Max(vp.X, vp.Y);
            ImGui.SliderFloat2("Min (px)", ref Min, 0f, maxExtent);
            ImGui.SliderFloat2("Max (px)", ref Max, 0f, maxExtent);
            ImGui.Checkbox("Show outline", ref ShowOutline);
        }
        public override void DrawWorld(PctDrawList draw, PctDxParams baseParams)
        {
            draw.AddClipZone(Min, Max);
            if (ShowOutline)
            {
                var vpPos = ImGuiHelpers.MainViewport.Pos;
                ImGui.GetForegroundDrawList().AddRect(vpPos + Min, vpPos + Max, 0xFF00FFFF);
            }
        }
    }
}
