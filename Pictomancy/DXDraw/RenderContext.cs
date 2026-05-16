using SharpDX.Direct3D11;
using System.Runtime.InteropServices;

namespace Pictomancy.DXDraw;

// device + deferred context
internal class RenderContext : IDisposable
{
    public SharpDX.Direct3D11.Device Device { get; private set; }
    public SharpDX.Direct3D11.DeviceContext Context { get; private set; }

    private class CacheEntry
    {
        public ShaderResourceView Srv = null!;
        public int LastFrame;
    }
    private readonly Dictionary<IntPtr, CacheEntry> _userSrvCache = new();
    private int _frameCounter;
    private const int UserSrvTtlFrames = 120;

    public unsafe RenderContext()
    {
        Device = new((nint)FFXIVClientStructs.FFXIV.Client.Graphics.Kernel.Device.Instance()->D3D11Forwarder);
        Context = new(Device);
    }

    public void Dispose()
    {
        foreach (var entry in _userSrvCache.Values)
            entry.Srv.Dispose();
        _userSrvCache.Clear();
        Context.Dispose();
    }

    public void Execute()
    {
        using var cmds = Context.FinishCommandList(true);
        Device.ImmediateContext.ExecuteCommandList(cmds, true);
        Context.ClearState();
    }

    public void BeginFrame()
    {
        _frameCounter++;
        int cutoff = _frameCounter - UserSrvTtlFrames;
        List<IntPtr>? stale = null;
        foreach (var kvp in _userSrvCache)
        {
            if (kvp.Value.LastFrame < cutoff)
                (stale ??= new()).Add(kvp.Key);
        }
        if (stale != null)
        {
            foreach (var key in stale)
            {
                _userSrvCache[key].Srv.Dispose();
                _userSrvCache.Remove(key);
            }
        }
    }

    public ShaderResourceView GetUserSrv(IntPtr nativePtr)
    {
        if (!_userSrvCache.TryGetValue(nativePtr, out var entry))
        {
            Marshal.AddRef(nativePtr);
            entry = new CacheEntry { Srv = new ShaderResourceView(nativePtr) };
            _userSrvCache[nativePtr] = entry;
        }
        entry.LastFrame = _frameCounter;
        return entry.Srv;
    }
}
