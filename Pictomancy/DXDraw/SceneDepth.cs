using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Format = SharpDX.DXGI.Format;

namespace Pictomancy.DXDraw;

// Provides the current scene depth to the shape pass as an SRV.
internal class SceneDepth : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();

    private Texture2D? _copy;
    private ShaderResourceView? _copySRV;

    internal ShaderResourceView? SRV => _copySRV;
    public SharpDX.Vector2 UvScale { get; private set; } = new(1, 1);

    public void Dispose()
    {
        _copy?.Dispose();
        _copySRV?.Dispose();
        RenderContext.Dispose();
    }

    internal unsafe void Update()
    {
        var rtm = RenderTargetManager.Instance();
        var unk70 = rtm != null ? *(Texture**)((byte*)rtm + 0x70) : null;
        if (unk70 == null || unk70->D3D11Texture2D == null)
        {
            PctService.Log.Warning("[Pictomancy] SceneDepth.Update: scene depth source unavailable.");
            return;
        }

        var depthStencil = new Texture2D((IntPtr)unk70->D3D11Texture2D);
        EnsureCopy(depthStencil.Description);
        RenderContext.Device.ImmediateContext.CopyResource(depthStencil, _copy);

        // Unk70 is allocated at the max scaled resolution; only the (ActualWidth, ActualHeight)
        // sub-rect contains live scene depth. Scale our [0,1] sample uvs accordingly.
        UvScale = new SharpDX.Vector2(
            unk70->AllocatedWidth > 0 ? (float)unk70->ActualWidth / unk70->AllocatedWidth : 1f,
            unk70->AllocatedHeight > 0 ? (float)unk70->ActualHeight / unk70->AllocatedHeight : 1f);
    }

    private void EnsureCopy(Texture2DDescription desc)
    {
        if (_copy != null &&
            _copy.Description.Width == desc.Width &&
            _copy.Description.Height == desc.Height)
        {
            return;
        }

        _copy?.Dispose();
        _copySRV?.Dispose();

        var copyDesc = desc;
        copyDesc.Format = Format.R24G8_Typeless;
        copyDesc.BindFlags = BindFlags.ShaderResource;
        _copy = new(RenderContext.Device, copyDesc);

        var srvDesc = new ShaderResourceViewDescription
        {
            Format = Format.R24_UNorm_X8_Typeless,
            Dimension = ShaderResourceViewDimension.Texture2D,
        };
        srvDesc.Texture2D.MostDetailedMip = 0;
        srvDesc.Texture2D.MipLevels = 1;
        _copySRV = new(RenderContext.Device, _copy, srvDesc);
    }
}
