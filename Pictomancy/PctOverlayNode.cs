using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using KamiToolKit.Enums;
using KamiToolKit.Nodes;
using KamiToolKit.Overlay.UiOverlay;
using SharpDX.Direct3D11;

namespace Pictomancy;

internal unsafe class PctOverlayNode : OverlayNode
{
    public override OverlayLayer OverlayLayer => OverlayLayer.Background;
    private readonly TextureImageNode pctTextureNode;

    public Texture* current;

    public PctOverlayNode()
    {
        pctTextureNode = new();
        pctTextureNode.AttachNode(this);
    }

    public void UpdateTexture(Texture2D texture2D, ShaderResourceView shaderResourceView)
    {
        if (current != null)
        {
            if (current->D3D11Texture2D == (void*)texture2D.NativePointer)
            {
                return;
            }
            current->DecRef();
        }
        var desc = texture2D.Description;
        var width = desc.Width;
        var height = desc.Height;
        var mipLevel = (byte)desc.MipLevels;
        var format = desc.Format;
        var flags = TextureFlags.TextureType2D;
        if (desc.Usage == ResourceUsage.Immutable)
            flags |= TextureFlags.Immutable;
        if (desc.Usage == ResourceUsage.Dynamic)
            flags |= TextureFlags.ReadWrite;
        if ((desc.CpuAccessFlags & CpuAccessFlags.Read) != 0)
            flags |= TextureFlags.CpuRead;
        if ((desc.BindFlags & BindFlags.RenderTarget) != 0)
            flags |= TextureFlags.TextureRenderTarget;
        if ((desc.BindFlags & BindFlags.DepthStencil) != 0)
            flags |= TextureFlags.TextureDepthStencil;

        var texture = Texture.CreateTexture2D(width, height, mipLevel, TextureFormat.B8G8R8A8_UNORM, flags, 0);
        if (texture == null)
        {
            return;
        }

        texture->D3D11Texture2D = (void*)texture2D.NativePointer;
        texture->D3D11ShaderResourceView = (void*)shaderResourceView.NativePointer;
        texture->IncRef();
        pctTextureNode.TextureSize = new(width, height);
        pctTextureNode.Size = new(width, height);
        pctTextureNode.SetTexture(texture);
        Size = new(width, height);

        current = texture;
    }

    protected override void OnUpdate()
    {
    }
}
