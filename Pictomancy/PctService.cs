global using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;
using KamiToolKit;
using KamiToolKit.Overlay.UiOverlay;
using Pictomancy.DXDraw;
using Pictomancy.VfxDraw;

namespace Pictomancy;

public class PctService
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; }
    [PluginService] internal static IGameGui GameGui { get; private set; }
    [PluginService] internal static ICondition Condition { get; private set; }
    [PluginService] internal static IPluginLog Log { get; private set; }
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;

    private static DXRenderer? _dxRenderer;
    private static SceneDepth? _sceneDepth;

    private static VfxRenderer? _vfxRenderer;
    private static OverlayController? _overlayController;
    private static PctOverlayNode? _overlayNode;
    public static VfxRenderer VfxRenderer => _vfxRenderer;
    internal static PctOverlayNode OverlayNode => _overlayNode;

    internal static PctDrawList DrawList;
    internal static PctDrawHints Hints;

    private static bool _initialized;
    private static bool _kamiInitialized;
    private static bool _vfxFrameworkSubscribed;

    /// <summary>
    /// Initialize Pictomancy. Returns a disposable handle; hold it for your plugin's lifetime and dispose it on plugin shutdown.
    /// Throws if called while a previous initialization is still alive (dispose the prior handle first).
    /// </summary>
    public static PctContext Initialize(IDalamudPluginInterface pluginInterface, PctOptions? options = null)
    {
        if (_initialized)
            throw new InvalidOperationException("Pictomancy is already initialized. Dispose the existing PctContext before calling Initialize again.");

        options ??= new PctOptions();
        pluginInterface.Create<PctService>();
        _initialized = true;

        if (options.EnableDxRenderer)
        {
            KamiToolKitLibrary.Initialize(pluginInterface, "PctOverlay");
            _kamiInitialized = true;

            _dxRenderer = new(options);
            _sceneDepth = new();
            Framework.RunOnFrameworkThread(() =>
            {
                _overlayController = new();
                _overlayNode = new();
                _overlayController.AddNode(_overlayNode);
            });
        }

        if (options.EnableVfxRenderer)
        {
            _vfxRenderer = new();
            VfxFunctions.Initialize();
            Framework.Update += Update;
            _vfxFrameworkSubscribed = true;
        }

        return new PctContext();
    }

    /// <summary>
    /// Backwards-compatible static dispose. Prefer disposing the handle returned by <see cref="Initialize"/>.
    /// </summary>
    public static void Dispose() => DisposeInternal();

    internal static void DisposeInternal()
    {
        if (!_initialized) return;
        _initialized = false;

        _overlayNode?.Dispose();
        _overlayNode = null;
        _overlayController?.Dispose();
        _overlayController = null;
        if (_kamiInitialized)
        {
            KamiToolKitLibrary.Dispose();
            _kamiInitialized = false;
        }
        _dxRenderer?.Dispose();
        _dxRenderer = null;
        _sceneDepth?.Dispose();
        _sceneDepth = null;
        _vfxRenderer?.Dispose();
        _vfxRenderer = null;
        if (_vfxFrameworkSubscribed)
        {
            Framework.Update -= Update;
            _vfxFrameworkSubscribed = false;
        }
    }

    internal static void Update(IFramework framework)
    {
        _vfxRenderer?.Update();
    }

    /// <summary>
    /// Helper for getting the current drawlist so it does not need to be passed around.
    /// Must be called within the scope of Draw().
    /// </summary>
    /// <returns>Current drawlist.</returns>
    /// <exception cref="InvalidOperationException">Throws when called before Draw()</exception>
    public static PctDrawList GetDrawList()
    {
        if (DrawList == null)
        {
            throw new InvalidOperationException("GetDrawList called without Draw");
        }
        return DrawList;
    }

    /// <summary>
    /// Begin drawing. Returns nullptr if drawing in unavailable.
    /// </summary>
    /// <param name="imguidrawlist">Optional ImGui drawlist. If unspecified, the background drawlist is used.</param>
    /// <param name="hints">Optional rendering hints. If unspecified, default hints are used.</param>
    /// <returns>Returns nullptr if drawing in unavailable.</returns>
    public static PctDrawList? Draw(ImDrawListPtr? imguidrawlist = null, PctDrawHints? hints = null)
    {
        if (_dxRenderer == null) return null;
        Hints = hints ?? new();
        if (!Hints.DrawInCutscene && IsInCutscene()) return null;
        if (!Hints.DrawWhenFaded && IsFaded()) return null;

        return DrawList = new PctDrawList(
            imguidrawlist,
            _dxRenderer,
            _sceneDepth
        );
    }

    private static bool IsInCutscene()
    {
        return Condition[ConditionFlag.OccupiedInCutSceneEvent] || Condition[ConditionFlag.WatchingCutscene78];
    }

    private unsafe static bool IsFaded()
    {
        var fadeMiddleWidget = (AtkUnitBase*)GameGui.GetAddonByName("FadeMiddle").Address;
        var fadeBlackWidget = (AtkUnitBase*)GameGui.GetAddonByName("FadeBlack").Address;
        return fadeMiddleWidget != null && fadeMiddleWidget->IsVisible ||
            fadeBlackWidget != null && fadeBlackWidget->IsVisible;
    }
}
