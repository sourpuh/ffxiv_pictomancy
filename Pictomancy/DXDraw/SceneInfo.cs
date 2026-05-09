using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using SharpDX.Direct3D11;

namespace Pictomancy.DXDraw;

// Borrow the game's scene info SRV on gbuffer[3]
// black character pixels & sky; magenta/red scene geometry; blue models
internal class SceneInfo : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();

    private IntPtr _cachedSrvPtr;
    private ShaderResourceView? _srv;

    internal ShaderResourceView? SRV => _srv;
    public SharpDX.Vector2 UvScale { get; private set; } = new(1, 1);

    public void Dispose()
    {
        DropSrv();
        RenderContext.Dispose();
    }

    // Zero the wrapper's NativePointer before disposal so we don't Release a ref we don't own.
    private void DropSrv()
    {
        if (_srv != null)
        {
            _srv.NativePointer = IntPtr.Zero;
            _srv.Dispose();
            _srv = null;
        }
        _cachedSrvPtr = IntPtr.Zero;
    }

    internal unsafe void Update()
    {
        var rtm = RenderTargetManager.Instance();
        if (rtm == null) return;

        var sceneTexPtr = *(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x38);
        if (sceneTexPtr == null || sceneTexPtr->D3D11ShaderResourceView == null)
        {
            DropSrv();
            PctService.Log.Warning("[Pictomancy] SceneInfo.Update: source unavailable.");
            return;
        }

        var srvPtr = (IntPtr)sceneTexPtr->D3D11ShaderResourceView;
        if (srvPtr != _cachedSrvPtr)
        {
            DropSrv();
            _cachedSrvPtr = srvPtr;
            _srv = new ShaderResourceView(srvPtr);
        }

        UvScale = new SharpDX.Vector2(
            sceneTexPtr->AllocatedWidth > 0 ? (float)sceneTexPtr->ActualWidth / sceneTexPtr->AllocatedWidth : 1f,
            sceneTexPtr->AllocatedHeight > 0 ? (float)sceneTexPtr->ActualHeight / sceneTexPtr->AllocatedHeight : 1f);
    }
}
