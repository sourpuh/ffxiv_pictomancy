namespace Pictomancy;

public enum UIMask
{
    /// <summary>No UI masking. Pictomancy renders over the game's UI.</summary>
    None = 0,
    /// <summary>
    /// Mask pixels where the game's backbuffer alpha indicates UI. Pictomancy renders "behind" the UI.
    /// BackBufferAlpha masking is automatically disabled if using AutoDraw.NativeOverlay or 3D resolution scaling.
    /// </summary>
    BackbufferAlpha = 1,
}
