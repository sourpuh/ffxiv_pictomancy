namespace Pictomancy;

/// <summary>
/// Rendering parameters for occlusion fade and distance fade.
/// </summary>
public record struct PctDxParams
{
    /// <summary>
    /// Alpha multiplier when fully behind scene geometry. 0 = invisible behind walls, 1 = fully visible.
    /// </summary>
    public float OccludedAlpha { get; init; } = 1f;

    /// <summary>
    /// World-space distance tolerance before occlusion kicks in. A pixel that's only this far behind the
    /// scene (or less) still draws at full alpha. Useful for ignoring z-fighting on uneven floors.
    /// 0 = strict occlusion. Typical values: [0.3, 1] for ground-level shapes on uneven terrain.
    /// </summary>
    public float OcclusionTolerance { get; init; } = 0.02f;

    /// <summary>
    /// World-space distance from camera at which the shape begins fading.
    /// Pixels closer than this draw at full alpha. Default = infinity (no distance fade).
    /// </summary>
    public float FadeStart { get; init; } = float.PositiveInfinity;

    /// <summary>
    /// World-space distance from camera at which the shape reaches alpha 0.
    /// Pixels farther than this are invisible. Default = infinity (no distance fade).
    /// Must be greater than FadeStart for the fade to have effect.
    /// </summary>
    public float FadeStop { get; init; } = float.PositiveInfinity;

    /// <summary>
    /// World-space half-height of the projection volume around the shape origin.
    /// Supported for Fan and Triangle based shapes, not for Strokes.
    /// When non-zero, supported shapes are projected onto scene geometry within this Y range instead of drawn as flat geometry.
    /// Pixels that don't project onto geometry are not drawn.
    /// Default = 0 (no projection).
    /// </summary>
    public float ProjectionHeight { get; init; } = 0f;

    public PctDxParams() { }
}
