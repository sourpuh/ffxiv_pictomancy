using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Format = SharpDX.DXGI.Format;

namespace Pictomancy.DXDraw;

// Wrap the game's typeless depth stencil texture in a typed SRV
internal class SceneDepth : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();

    private IntPtr _cachedTexPtr;
    private ShaderResourceView? _srv;

    internal ShaderResourceView? SRV => _srv;
    public SharpDX.Vector2 UvScale { get; private set; } = new(1, 1);

    public void Dispose()
    {
        _srv?.Dispose();
        _srv = null;
        _cachedTexPtr = IntPtr.Zero;
        RenderContext.Dispose();
    }

    internal unsafe void Update()
    {
        var rtm = RenderTargetManager.Instance();
        var depthStencil = rtm != null ? rtm->DepthStencil : null;
        if (depthStencil == null || depthStencil->D3D11Texture2D == null)
        {
            PctService.Log.Warning("[Pictomancy] SceneDepth.Update: scene depth source unavailable.");
            return;
        }

        var texPtr = (IntPtr)depthStencil->D3D11Texture2D;
        if (texPtr != _cachedTexPtr)
        {
            _srv?.Dispose();
            _cachedTexPtr = texPtr;

            var tex = new Texture2D(texPtr);
            try
            {
                var srvDesc = new ShaderResourceViewDescription
                {
                    Format = Format.R24_UNorm_X8_Typeless,
                    Dimension = ShaderResourceViewDimension.Texture2D,
                };
                srvDesc.Texture2D.MostDetailedMip = 0;
                srvDesc.Texture2D.MipLevels = 1;
                _srv = new ShaderResourceView(RenderContext.Device, tex, srvDesc);
            }
            finally
            {
                // Zero the NativePointer before disposal so we don't Release a ref we don't own.
                tex.NativePointer = IntPtr.Zero;
            }
        }

        UvScale = new SharpDX.Vector2(
            depthStencil->AllocatedWidth > 0 ? (float)depthStencil->ActualWidth / depthStencil->AllocatedWidth : 1f,
            depthStencil->AllocatedHeight > 0 ? (float)depthStencil->ActualHeight / depthStencil->AllocatedHeight : 1f);
    }
}
