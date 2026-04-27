# FFXIV Pictomancy
Pictomancy is a library for drawing 3D world overlays and VFX in Dalamud plugins.
Pictomancy has an ImGui-like interface that operates in world space instead of a 2D canvas.
Pictomancy simplifies the hard parts of 3D overlays by correctly clipping objects behind the camera and clipping around the native UI.

# Pictomancy is still in development and does not have a stable API. Use at your own risk.

## Installation
Nuget package: https://www.nuget.org/packages/Pictomancy


Use as a git sub-module:
```bash
git submodule add https://github.com/sourpuh/ffxiv_pictomancy
```

## Use
See the included PictomancyDemo plugin for real example usage.

Library initialization:
```c#
PctContext pctCtx;

public MyPlugin(DalamudPluginInterface pluginInterface)
{
    pctCtx = PctService.Initialize(pluginInterface);

    ... Your Code Here ...
}

public void Dispose()
{
    pctCtx.Dispose();

    ... Your Code Here ...
}
```

`Initialize()` accepts an optional `PctOptions` object which can disable specific renderers or adjust DX buffer sizes.

### Drawing an ImGui overlay with DirectX Renderer
```c#
using (var drawList = PctService.Draw())
{
    if (drawList == null)
        return;
    // Draw a circle around a GameObject's hitbox
    Vector3 worldPosition = gameObject.Position;
    float radius = gameObject.HitboxRadius;
    drawList.AddCircleFilled(worldPosition, radius, fillColor);
    drawList.AddCircle(worldPosition, radius, outlineColor);
}
```

### Draw Hints
#### Depth Bias
Depth bias is used to occlude your drawings based on scene depth.

- 0 is strict occlusion.
- Small values like [0.001, 1] keep things visible on bumpy surfaces.
- Larger values bias shapes "forward" so they appear through nearby geometry.
- Infinity disables occlusion entirely.
<img src="ReadmeImages/depthBias.png">

#### AutoDraw & UI Masking
UI masking is used to hide the pictomancy overlay behind the native UI.
* Only BackbufferAlpha masking exists currently which does not work with 3D resolution scaling and is automatically disabled.
* AutoDraw supports a NativeOverlay option which draws behind the native UI but has two issues:
    1. It does not display over Nameplates.
    1. It lags behind by one frame which causes ghosting effects when moving.
<img src="ReadmeImages/autodrawAndMask.png">

### Drawing with in-game VFX
You must specify an ID for each element you draw. IDs should be consistent across frames and unique for each VFX with the same path.

VFX with the same ID and path are retained when drawn in consecutive frames.  If the ID is specified in consecutive frames, the VFX is updated to match the new parameters. If the ID is not specified in consecutive frames, the VFX is destroyed.
```c#
// Draw a circle omen VFX on a GameObject's hitbox
PctService.VfxRenderer.AddOmen($"{gameobject.EntityId}", "general01bf", gameObject.Position, gameObject.HitboxRadius);

// Draw a tankbuster lockon VFX on a GameObject
PctService.VfxRenderer.AddLockon($"{gameobject.EntityId}", "tank_lockon01i", gameobject);

// Draw a tether channeling VFX between two GameObjects
PctService.VfxRenderer.AddChanneling($"{gameobject.EntityId}", "chn_nomal01f", gameobject1, gameobject2);
```

If you want to draw basic Omen shapes, there are helpers provided to draw circles, lines, cones, and donuts. If the method returns void, it will always successfully draw. If the method returns a boolean, it will return false if it did not find an Omen to match your desired shape.
```c#
// Draw a circle around a GameObject's hitbox using the AddCircle helper
PctService.VfxRenderer.AddCircle($"{gameobject.EntityId}", gameObject.Position, gameObject.HitboxRadius);
```