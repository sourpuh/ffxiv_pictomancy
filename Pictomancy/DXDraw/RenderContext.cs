namespace Pictomancy.DXDraw;

// device + deferred context
internal class RenderContext : IDisposable
{
    public SharpDX.Direct3D11.Device Device { get; private set; }
    public SharpDX.Direct3D11.DeviceContext Context { get; private set; }

    public unsafe RenderContext()
    {
        Device = new((nint)FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance()->D3D11Forwarder);
        Context = new(Device);
    }

    public void Dispose()
    {
        Context.Dispose();
    }

    public void Execute()
    {
        using var cmds = Context.FinishCommandList(true);
        Device.ImmediateContext.ExecuteCommandList(cmds, true);
        Context.ClearState();
    }
}
