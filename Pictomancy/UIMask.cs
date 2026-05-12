namespace Pictomancy;

public enum UIMask
{
    /// <summary>No UI masking. Pictomancy renders over the game's UI.</summary>
    None = 0,
    /// <summary>
    /// Mask pixels where the game's backbuffer alpha indicates UI. Pictomancy renders "behind" the UI.
    /// BackbufferSubtractedAlpha masking is used instead if 3D resolution scaling is detected.\
    /// Automatically disabled if using AutoDraw.NativeOverlay.
    /// </summary>
    BackbufferAlpha = 1,
    /// <summary>
    /// Mask specifically designed for scaled resolutions that don't support BackbufferAlpha.
    /// Pictomancy subtracts the "Pre-UI" backbuffer from a "Post-UI" backbuffer to create an approximate UI mask.
    /// This does not work as well for semi-transparent UI elements as BackbufferAlpha.
    /// Automatically disabled if using AutoDraw.NativeOverlay.
    /// </summary>
    BackbufferSubtraction = 2,
}
