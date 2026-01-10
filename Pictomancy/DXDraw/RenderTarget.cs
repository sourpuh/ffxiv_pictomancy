using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using Format = SharpDX.DXGI.Format;

namespace Pictomancy.DXDraw;

internal unsafe class RenderTarget : IDisposable
{
    public SharpDX.Vector2 Size { get; private set; }
    public uint Width => (uint)Size.X;
    public uint Height => (uint)Size.Y;

    private readonly Texture2D _baseRT;
    private readonly RenderTargetView _baseRTV;
    private readonly ShaderResourceView _baseSRV;

    private readonly Texture2D _processedRT;
    private readonly RenderTargetView _processedRTV;
    private readonly ShaderResourceView _processedSRV;

    private Texture2D? _backBufferCopy;
    private ShaderResourceView? _backBufferSRV;

    private readonly BlendState _defaultBlendState;

    public nint ImguiHandle => _processedSRV.NativePointer;
    public PctTexture Texture => new(ImguiHandle, Width, Height);

    public RenderTarget(RenderContext ctx, int width, int height, AlphaBlendMode blendMode)
    {
        Size = new(width, height);
        var desc = new Texture2DDescription()
        {
            Width = width,
            Height = height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.R8G8B8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.None
        };

        _baseRT = new(ctx.Device, desc);
        _baseRTV = new(ctx.Device, _baseRT);
        _baseSRV = new(ctx.Device, _baseRT);

        _processedRT = new(ctx.Device, desc);
        _processedRTV = new(ctx.Device, _processedRT);
        _processedSRV = new(ctx.Device, _processedRT);

        var blendDescription = BlendStateDescription.Default();
        if (blendMode != AlphaBlendMode.None)
        {
            blendDescription.RenderTarget[0].IsBlendEnabled = true;
            blendDescription.RenderTarget[0].SourceBlend = BlendOption.SourceAlpha;
            blendDescription.RenderTarget[0].DestinationBlend = BlendOption.InverseSourceAlpha;
            blendDescription.RenderTarget[0].BlendOperation = BlendOperation.Add;
            blendDescription.RenderTarget[0].SourceAlphaBlend = BlendOption.One;
            blendDescription.RenderTarget[0].DestinationAlphaBlend = BlendOption.InverseSourceAlpha;

            if (blendMode == AlphaBlendMode.Add) blendDescription.RenderTarget[0].AlphaBlendOperation = BlendOperation.Add;
            else if (blendMode == AlphaBlendMode.Max) blendDescription.RenderTarget[0].AlphaBlendOperation = BlendOperation.Maximum;

            blendDescription.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
        }
        _defaultBlendState = new(ctx.Device, blendDescription);
    }

    public void Bind(RenderContext ctx)
    {
        ctx.Context.ClearRenderTargetView(_baseRTV, new());
        ctx.Context.Rasterizer.SetViewport(0, 0, Size.X, Size.Y);
        ctx.Context.OutputMerger.SetBlendState(_defaultBlendState);
        ctx.Context.OutputMerger.SetTargets(_baseRTV);
    }

    public void ExecuteFSP(RenderContext ctx, Texture2D backBuffer, FullScreenPass fsp)
    {
        ValidateBackBufferResources(ctx.Device, backBuffer.Description);
        ctx.Context.CopyResource(backBuffer, _backBufferCopy);

        using var localBlend = new BlendState(ctx.Device, BlendStateDescription.Default());
        ctx.Context.OutputMerger.SetBlendState(localBlend);

        ctx.Context.ClearRenderTargetView(_processedRTV, new());
        ctx.Context.OutputMerger.SetTargets(_processedRTV);

        fsp.Draw(ctx, _baseSRV, _backBufferSRV!);
    }

    private void ValidateBackBufferResources(Device device, Texture2DDescription backBufferDesc)
    {
        if (_backBufferCopy == null ||
            _backBufferCopy.Description.Width != backBufferDesc.Width ||
            _backBufferCopy.Description.Height != backBufferDesc.Height)
        {
            _backBufferCopy?.Dispose();
            _backBufferSRV?.Dispose();

            var copyDesc = backBufferDesc;
            copyDesc.BindFlags = BindFlags.ShaderResource;
            _backBufferCopy = new Texture2D(device, copyDesc);
            _backBufferSRV = new ShaderResourceView(device, _backBufferCopy);
        }
    }

    public void Dispose()
    {
        _baseRT.Dispose();
        _baseRTV.Dispose();
        _baseSRV.Dispose();

        _processedRT?.Dispose();
        _processedRTV?.Dispose();
        _processedSRV?.Dispose();

        _backBufferCopy?.Dispose();
        _backBufferSRV?.Dispose();

        _defaultBlendState.Dispose();
    }
}
