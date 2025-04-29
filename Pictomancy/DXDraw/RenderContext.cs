using SharpDX.Direct3D11;
using System.Drawing;
using System.Drawing.Imaging;

namespace Pictomancy.DXDraw;

// device + deferred context
internal class RenderContext : IDisposable
{
    public SharpDX.Direct3D11.Device Device { get; private set; }
    //public SharpDX.Direct2D1.Device Device2 { get; private set; }

    public SharpDX.Direct3D11.DeviceContext Context { get; private set; }
    //public SharpDX.Direct2D1.DeviceContext Context2 { get; private set; }

    public ShaderResourceView Texture { get; private set; }

    public SamplerState SamplerState { get; private set; }

    public unsafe RenderContext()
    {
        Device = new((nint)FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance()->D3D11Forwarder);
        Context = new(Device);

        try
        {
            /*
            SharpDX.DXGI.Device dxgiDev = Device.QueryInterfaceOrNull<SharpDX.DXGI.Device>();
            if (dxgiDev != null)
            {
                Device2 = new(dxgiDev);
            }
            */
            //using (var dxgiDevice = Device.QueryInterface<SharpDX.DXGI.Device>())
            //{
            //    Device2 = new(dxgiDevice);
            //}
            // = new((nint)FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance()->D3D11Forwarder);
            //Context2 = new(Device2, DeviceContextOptions.None);
        }
        catch (Exception ex)
        {
            PictoService.Log.Error(ex, "Failed to create D2D device");
        }

        var bitmap = new Bitmap(1024, 256);
        var bitmap2 = new Bitmap(1024, 256);

        for (int i = 0; i < bitmap.Height; i++)
            for (int j = 0; j < bitmap.Width; j++)
            {
                var y = i / 64;
                var x = j / 64;

                var color = Color.White;
                var color2 = Color.Transparent;
                if (y % 2 == 0 && x % 2 == 1)
                {
                    color = Color.Blue;
                    color2 = Color.Green;
                }
                else if (y % 2 == 1 && x % 2 == 0)
                {
                    color = Color.Red;
                    color2 = Color.Magenta;
                }
                bitmap.SetPixel(j, i, color);
                bitmap2.SetPixel(j, i, color2);
            }
        Texture = RegisterRenderTexture(bitmap, bitmap2);
        SamplerState = new(Device, SamplerStateDescription.Default());
    }

    public void Dispose()
    {
        Context.Dispose();
        //Context2.Dispose();
    }

    public void Execute()
    {
        using var cmds = Context.FinishCommandList(true);
        Device.ImmediateContext.ExecuteCommandList(cmds, true);
        Context.ClearState();
    }

    public ShaderResourceView RegisterRenderTexture(Bitmap bitmap, Bitmap bitmap2)
    {
        BitmapData bitmapData = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        BitmapData bitmapData2 = bitmap2.LockBits(new Rectangle(0, 0, bitmap2.Width, bitmap2.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            Texture2DDescription texDesc = new Texture2DDescription();
            texDesc.Width = bitmap.Width;
            texDesc.Height = bitmap.Height;
            texDesc.MipLevels = 1;
            texDesc.ArraySize = 2;
            texDesc.Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm;
            texDesc.SampleDescription.Count = 1;
            texDesc.SampleDescription.Quality = 0;
            texDesc.Usage = ResourceUsage.Immutable;
            texDesc.BindFlags = BindFlags.ShaderResource;
            texDesc.CpuAccessFlags = CpuAccessFlags.None;
            texDesc.OptionFlags = ResourceOptionFlags.None;

            SharpDX.DataBox data = new(bitmapData.Scan0, bitmapData.Stride, 0);
            SharpDX.DataBox data2 = new(bitmapData2.Scan0, bitmapData2.Stride, 0);

            var tex = new Texture2D(Device, texDesc, [data, data2]);

            ShaderResourceViewDescription srvDesc = new ShaderResourceViewDescription();
            srvDesc.Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm;
            srvDesc.Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D;
            srvDesc.Texture2D.MipLevels = 1;
            srvDesc.Texture2D.MostDetailedMip = 0;

            var texSRV = new ShaderResourceView(Device, tex, srvDesc);
            return texSRV;
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            bitmap2.UnlockBits(bitmapData2);
        }
    }
}
