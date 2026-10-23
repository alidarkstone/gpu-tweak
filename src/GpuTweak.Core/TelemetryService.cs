using GpuTweak.Core.Backends;
using GpuTweak.Core.Models;

namespace GpuTweak.Core;

/// <summary>Polls the backend on a background timer and publishes samples.</summary>
public sealed class TelemetryService : IDisposable
{
    private readonly IGpuBackend _backend;
    private readonly ITimer _timer;
    private int _busy;

    public TelemetryService(IGpuBackend backend, TimeSpan interval, TimeProvider? time = null)
    {
        _backend = backend;
        _timer = (time ?? TimeProvider.System).CreateTimer(_ => Poll(), null, TimeSpan.Zero, interval);
    }

    public event Action<GpuTelemetry>? Sample;
    public GpuTelemetry? Latest { get; private set; }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            Latest = _backend.ReadTelemetry();
            Sample?.Invoke(Latest);
        }
        catch (Exception)
        {
            // A missed sample is harmless; the next tick tries again.
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    public void Dispose() => _timer.Dispose();
}
