namespace Pictomancy;

/// <summary>
/// Disposable handle returned by `PctService.Initialize`.
/// Hold this for the lifetime of your plugin and dispose it on plugin shutdown.
/// Dispose() is idempotent and safe to call from multiple paths.
/// </summary>
public sealed class PctContext : IDisposable
{
    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PctService.DisposeInternal();
    }
}
