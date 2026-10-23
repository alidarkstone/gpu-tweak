using GpuTweak.Core.Backends;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Tests;

internal sealed class FakeBackend : IGpuBackend
{
    public TuningSettings Current { get; set; } = TuningSettings.Stock;
    public List<TuningSettings> Applied { get; } = [];
    public int ResetCount { get; private set; }
    public Func<TuningSettings, bool>? FailWhen { get; set; }

    public GpuInfo Info { get; } = new(GpuVendor.Nvidia, "NVIDIA GeForce RTX 5080", "1.0", 16L << 30);

    public TuningLimits Limits { get; } = new(-500, 1000, -1000, 3000, 150, 400, 360, 210, 3100, 30, 100, true);

    public GpuTelemetry ReadTelemetry() => new(DateTimeOffset.Now, 60, 2700, 15000, 250, 360, 1, 2, 50, 90);
    public TuningSettings ReadCurrentSettings() => Current;

    public void Apply(TuningSettings settings)
    {
        if (FailWhen?.Invoke(settings) == true) throw new GpuBackendException("boom");
        Applied.Add(settings);
        Current = settings;
    }

    public void ResetToStock()
    {
        ResetCount++;
        Current = TuningSettings.Stock;
    }

    public void Dispose() { }
}
