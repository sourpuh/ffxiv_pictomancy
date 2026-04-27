namespace Pictomancy;

/// <summary>
/// Init options including buffer-size limits for Pictomancy. Pass to PctService.Initialize if your plugin
/// draws many shapes per frame and you have seen "buffer full" warnings in the log. All values are
/// per-frame caps; exceeding them silently drops further shapes (with a one-shot warning).
/// </summary>
public sealed class PctOptions
{
    /// <summary>Initialize the DX renderer.</summary>
    public bool EnableDxRenderer { get; init; } = true;

    /// <summary>Initialize the VFX renderer.</summary>
    public bool EnableVfxRenderer { get; init; } = true;

    /// <summary>Max fan instances (donuts, cones, filled circles, arcs) drawn per frame.</summary>
    public int MaxFans { get; init; } = 2048;

    /// <summary>Max triangle vertices (3 per filled triangle) drawn per frame.</summary>
    public int MaxTriangleVertices { get; init; } = 1024 * 3;

    /// <summary>Max stroke vertices drawn per frame (one per line endpoint in each stroke). The default is sized for the fan-fallback path.</summary>
    public int MaxStrokeSegments { get; init; } = 2048 * 240 / 2;

    /// <summary>Max clip-zone rectangles drawn per frame.</summary>
    public int MaxClipZones { get; init; } = 1024;
}
