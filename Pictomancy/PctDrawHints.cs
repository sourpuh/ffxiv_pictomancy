namespace Pictomancy;

/// <summary>
/// Hints for Pictomancy drawing. Use object-initializer syntax to set just what you need:
/// <code>
/// var hints = new PctDrawHints { AutoDraw = AutoDraw.NativeOverlay, DepthBias = 0.005f };
/// </code>
/// These are called "hints" instead of "settings" because they may not always be respected.
/// </summary>
public record struct PctDrawHints
{
    /// <summary>Draw during cutscenes.</summary>
    public bool DrawInCutscene { get; init; } = false;

    /// <summary>Draw when the game is faded, such as loading a new area.</summary>
    public bool DrawWhenFaded { get; init; } = false;

    /// <summary>
    /// How to display the rendered texture. Check AutoDraw file for descriptions.
    /// </summary>
    public AutoDraw AutoDraw { get; init; } = AutoDraw.ImGuiOverlay;

    /// <summary>Max alpha value 0-255.</summary>
    public byte MaxAlpha { get; init; } = 255;

    /// <summary>
    /// How alpha blending will be performed. Check AlphaBlendMode file for descriptions.
    /// </summary>
    public AlphaBlendMode AlphaBlendMode { get; init; } = AlphaBlendMode.Add;

    /// <summary>
    /// World-space bias added to the scene depth before occlusion testing.
    /// 0 is strict occlusion. Small values like [0.001, 0.1] keep things visible on non-planar surfaces.
    /// Larger values bias shapes "forward" so they appear through nearby geometry.
    /// Infinity disables occlusion entirely.
    /// </summary>
    public float DepthBias { get; init; } = float.PositiveInfinity;

    /// <summary>
    /// How to mask pictomancy output with the game's UI. Check UIMask file for descriptions.
    /// </summary>
    public UIMask UIMask { get; init; } = UIMask.BackbufferAlpha;

    public float MaxAlphaFraction => MaxAlpha / 255f;

    // Required so the property initializers actually run for `new PctDrawHints()`.
    // Without it, `default(PctDrawHints)` skips initializers and zeros all fields.
    public PctDrawHints() { }
}
