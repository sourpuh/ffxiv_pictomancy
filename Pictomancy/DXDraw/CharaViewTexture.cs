using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using SharpDX.Direct3D11;

namespace Pictomancy.DXDraw;

internal class CharaViewTexture : IDisposable
{
    public RenderContext RenderContext { get; init; } = new();

    private IntPtr _cachedSrvPtr;
    private ShaderResourceView? _srv;

    internal ShaderResourceView? SRV => _srv;
    public uint Width { get; private set; }
    public uint Height { get; private set; }

    public void Dispose()
    {
        DropSrv();
        RenderContext.Dispose();
    }

    private void DropSrv()
    {
        if (_srv != null)
        {
            _srv.NativePointer = IntPtr.Zero;
            _srv.Dispose();
            _srv = null;
        }
        _cachedSrvPtr = IntPtr.Zero;
        Width = 0;
        Height = 0;
    }

    internal unsafe void Update()
    {
        var rtm = RenderTargetManager.Instance();
        if (rtm == null)
        {
            DropSrv();
            return;
        }

        var tex = *(FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Texture**)((byte*)rtm + 0x2C8);
        if (tex == null || tex->D3D11ShaderResourceView == null)
        {
            DropSrv();
            return;
        }

        var srvPtr = (IntPtr)tex->D3D11ShaderResourceView;
        if (srvPtr != _cachedSrvPtr)
        {
            DropSrv();
            _cachedSrvPtr = srvPtr;
            _srv = new ShaderResourceView(srvPtr);
        }

        Width = tex->ActualWidth;
        Height = tex->ActualHeight;
    }
}
