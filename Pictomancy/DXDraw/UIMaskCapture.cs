using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using SharpDX.D3DCompiler;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using System.Runtime.InteropServices;
using Device = FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device;
using Format = SharpDX.DXGI.Format;

namespace Pictomancy.DXDraw;

internal unsafe class UIMaskCapture : IDisposable
{
    private const int OMSetRenderTargetsVTableIndex = 33;

    private delegate void OMSetRenderTargetsDelegate(nint deviceContext, uint numViews, nint* renderTargetViews, nint depthStencilView);

    private readonly RenderContext _ctx;
    private readonly Hook<OMSetRenderTargetsDelegate>? _hook;

    private readonly VertexShader _vs;
    private readonly PixelShader  _ps;
    private readonly SharpDX.Direct3D11.Buffer _constantBuffer;

    private Texture2D? _snapshot;
    private ShaderResourceView? _snapshotSRV;

    private Texture2D? _maskRT;
    private RenderTargetView? _maskRTV;
    private ShaderResourceView? _maskSRV;

    private Texture2D? _bbCopy;
    private ShaderResourceView? _bbCopySRV;

    public ShaderResourceView? MaskSRV => _maskSRV;
    public bool HasSnapshot => _snapshot != null;

    // Used to snapshot once we see a bind that includes a DSV.
    // The first DSV-bearing back-buffer bind in a frame is the start of nameplate & other UI.
    private bool _sawDsvBindThisFrame;

    public void BeginFrame()
    {
        _sawDsvBindThisFrame = false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Thresholds
    {
        public float Low;
        public float High;
        public float OpaqueAlphaCutoff;
        public float StrongRgbThreshold;
    }

    public UIMaskCapture(RenderContext ctx, IGameInteropProvider hookProvider)
    {
        _ctx = ctx;

        const string shaderSource = """
            Texture2D    backBuffer     : register(t0);
            Texture2D    backBufferNoUI : register(t1);
            SamplerState samplerState   : register(s0);

            cbuffer Thresholds : register(b0)
            {
                float thresholdLow;
                float thresholdHigh;
                float opaqueAlphaCutoff;
                float strongRgbThreshold;
            };

            struct VSOutput
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD;
            };

            VSOutput vs(uint id : SV_VertexID)
            {
                VSOutput output;
        	    float2 uv = float2((id << 1) & 2, id & 2);
        	    output.pos = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
                output.uv = uv;
                return output;
            }

            float4 ps(VSOutput input) : SV_TARGET
            {
                float4 withUI = backBuffer.Sample(samplerState, input.uv);
                float4 noUI = backBufferNoUI.Sample(samplerState, input.uv);

                if (withUI.a >= opaqueAlphaCutoff && noUI.a < opaqueAlphaCutoff)
                    return float4(0, 0, 0, 1);

                float3 rgbDiff = abs(withUI.rgb - noUI.rgb);
                float maxRgbDiff = max(rgbDiff.r, max(rgbDiff.g, rgbDiff.b));

                if (maxRgbDiff >= strongRgbThreshold)
                    return float4(0, 0, 0, 1);

                float alpha = smoothstep(thresholdLow, thresholdHigh, maxRgbDiff);
                return float4(0, 0, 0, alpha);
            }
        """;

        var vs = ShaderBytecode.Compile(shaderSource, "vs", "vs_5_0");
        PctService.Log.Debug($"UIMaskCapture VS compile: {vs.Message}");
        _vs = new(_ctx.Device, vs.Bytecode);

        var ps = ShaderBytecode.Compile(shaderSource, "ps", "ps_5_0");
        PctService.Log.Debug($"UIMaskCapture PS compile: {ps.Message}");
        _ps = new(_ctx.Device, ps.Bytecode);

        _constantBuffer = new(_ctx.Device, 16, ResourceUsage.Default, BindFlags.ConstantBuffer, CpuAccessFlags.None, ResourceOptionFlags.None, 0);

        // TY Glyceri for hooking info
        try
        {
            nint contextPtr = _ctx.Device.ImmediateContext.NativePointer;
            nint vtable = Marshal.ReadIntPtr(contextPtr);
            nint omSetPtr = Marshal.ReadIntPtr(vtable, 33 * nint.Size);
            _hook = hookProvider.HookFromAddress<OMSetRenderTargetsDelegate>(omSetPtr, OMSetRenderTargetsDetour);
            _hook.Enable();
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] UIMaskCapture: failed to install OMSetRenderTargets hook.");
        }
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _vs.Dispose();
        _ps.Dispose();
        _constantBuffer.Dispose();
        DisposeSnapshot();
        DisposeMask();
        DisposeBackBufferCopy();
    }

    private void DisposeSnapshot()
    {
        _snapshotSRV?.Dispose();
        _snapshot?.Dispose();
        _snapshotSRV = null;
        _snapshot = null;
    }

    private void DisposeMask()
    {
        _maskSRV?.Dispose();
        _maskRTV?.Dispose();
        _maskRT?.Dispose();
        _maskSRV = null;
        _maskRTV = null;
        _maskRT = null;
    }

    private void DisposeBackBufferCopy()
    {
        _bbCopySRV?.Dispose();
        _bbCopy?.Dispose();
        _bbCopySRV = null;
        _bbCopy = null;
    }

    private void OMSetRenderTargetsDetour(nint deviceContext, uint numViews, nint* rtvs, nint dsv)
    {
        try
        {
            MaybeCapturePreBind(numViews, rtvs, dsv);
        }
        catch (Exception e)
        {
            PctService.Log.Error(e, "[Pictomancy] UIMaskCapture: pre-bind capture failed");
        }

        _hook!.Original(deviceContext, numViews, rtvs, dsv);
    }

    private void MaybeCapturePreBind(uint numViews, nint* rtvs, nint dsv)
    {
        if (numViews == 0) return;

        var device = Device.Instance();
        if (device == null
            || device->SwapChain == null
            || device->SwapChain->BackBuffer == null
            || device->SwapChain->BackBuffer->D3D11Texture2D == null)
        {
            return;
        }

        nint targetD3D11 = (nint)device->SwapChain->BackBuffer->D3D11Texture2D;
        if (targetD3D11 == nint.Zero) return;

        bool deviceBackBufferBound = false;
        for (uint i = 0; i < numViews; i++)
        {
            nint rtv = rtvs[i];
            if (rtv == nint.Zero) continue;

            var view  = new RenderTargetView(rtv);
            var tex2d = view.Resource.QueryInterfaceOrNull<Texture2D>();
            if (tex2d == null) continue;
            if (tex2d.NativePointer == targetD3D11)
            {
                deviceBackBufferBound = true;
                break;
            }
        }

        if (!deviceBackBufferBound) return;
        if (_sawDsvBindThisFrame) return;
        if (dsv != nint.Zero)
        {
            _sawDsvBindThisFrame = true;
        }

        EnsureSnapshot(targetD3D11);

        var src = new Texture2D(targetD3D11);
        _ctx.Device.ImmediateContext.CopyResource(src, _snapshot);
    }

    private void EnsureSnapshot(nint deviceBackBufferD3D11)
    {
        var src  = new Texture2D(deviceBackBufferD3D11);
        var desc = src.Description;

        if (_snapshot != null
            && _snapshot.Description.Width  == desc.Width
            && _snapshot.Description.Height == desc.Height
            && _snapshot.Description.Format == desc.Format)
        {
            return;
        }

        DisposeSnapshot();

        var snapDesc = desc;
        snapDesc.BindFlags      = BindFlags.ShaderResource;
        snapDesc.CpuAccessFlags = CpuAccessFlags.None;
        snapDesc.OptionFlags    = ResourceOptionFlags.None;
        snapDesc.Usage          = ResourceUsage.Default;
        snapDesc.Format         = snapDesc.Format.ToUNorm();

        _snapshot = new(_ctx.Device, snapDesc);
        _snapshotSRV = new(_ctx.Device, _snapshot);
    }

    private void EnsureMask(int width, int height)
    {
        if (_maskRT != null
            && _maskRT.Description.Width  == width
            && _maskRT.Description.Height == height)
        {
            return;
        }

        DisposeMask();

        var desc = new Texture2DDescription
        {
            Width             = width,
            Height            = height,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Format.R8G8B8A8_UNorm,
            SampleDescription = new(1, 0),
            Usage             = ResourceUsage.Default,
            BindFlags         = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags    = CpuAccessFlags.None,
            OptionFlags       = ResourceOptionFlags.None,
        };

        _maskRT  = new(_ctx.Device, desc);
        _maskRTV = new(_ctx.Device, _maskRT);
        _maskSRV = new(_ctx.Device, _maskRT);
    }

    private void EnsureBackBufferCopy(Texture2DDescription srcDesc)
    {
        if (_bbCopy != null
            && _bbCopy.Description.Width == srcDesc.Width
            && _bbCopy.Description.Height == srcDesc.Height
            && _bbCopy.Description.Format == srcDesc.Format)
        {
            return;
        }

        DisposeBackBufferCopy();

        var copyDesc = srcDesc;
        copyDesc.BindFlags = BindFlags.ShaderResource;
        copyDesc.CpuAccessFlags = CpuAccessFlags.None;
        copyDesc.OptionFlags = ResourceOptionFlags.None;
        copyDesc.Usage = ResourceUsage.Default;
        copyDesc.Format = copyDesc.Format.ToUNorm();

        _bbCopy = new(_ctx.Device, copyDesc);
        _bbCopySRV = new(_ctx.Device, _bbCopy);
    }

    public void BuildMask(Texture2D currentBackBuffer, float thresholdLow = 0.002f, float thresholdHigh = 0.20f, float opaqueAlphaCutoff = 0.999f, float strongRgbThreshold = 0.30f)
    {
        if (_snapshot == null || _snapshotSRV == null)
        {
            return;
        }

        var bbDesc = currentBackBuffer.Description;
        if (_snapshot.Description.Width != bbDesc.Width || _snapshot.Description.Height != bbDesc.Height)
        {
            return;
        }

        EnsureBackBufferCopy(bbDesc);
        EnsureMask(bbDesc.Width, bbDesc.Height);
        _ctx.Context.CopyResource(currentBackBuffer, _bbCopy);

        var thresholds = new Thresholds
        {
            Low = thresholdLow,
            High = thresholdHigh,
            OpaqueAlphaCutoff = opaqueAlphaCutoff,
            StrongRgbThreshold = strongRgbThreshold,
        };
        _ctx.Context.UpdateSubresource(ref thresholds, _constantBuffer);

        _ctx.Context.ClearRenderTargetView(_maskRTV, new());
        _ctx.Context.OutputMerger.SetTargets(_maskRTV);
        _ctx.Context.Rasterizer.SetViewport(0, 0, bbDesc.Width, bbDesc.Height);
        _ctx.Context.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
        _ctx.Context.VertexShader.Set(_vs);
        _ctx.Context.PixelShader.Set(_ps);
        _ctx.Context.GeometryShader.Set(null);
        _ctx.Context.PixelShader.SetConstantBuffer(0, _constantBuffer);
        _ctx.Context.PixelShader.SetShaderResource(0, _bbCopySRV);
        _ctx.Context.PixelShader.SetShaderResource(1, _snapshotSRV);

        _ctx.Context.Draw(3, 0);

        _ctx.Context.PixelShader.SetShaderResource(0, null);
        _ctx.Context.PixelShader.SetShaderResource(1, null);
    }

}
