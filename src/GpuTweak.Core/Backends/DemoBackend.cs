using GpuTweak.Core.Models;

namespace GpuTweak.Core.Backends;

/// <summary>
/// Simulated card for UI work and for machines without a supported GPU (start with --demo).
/// Numbers are synthetic and only react loosely to the applied settings.
/// </summary>
public sealed class DemoBackend : IGpuBackend
{
    private readonly Random _random = new(42);
    private TuningSettings _current = TuningSettings.Stock;

    public GpuInfo Info { get; } = new(GpuVendor.Nvidia, "NVIDIA GeForce RTX 5080 (Demo)", "demo", 16L * 1024 * 1024 * 1024);

    public TuningLimits Limits { get; } = new(
        CoreOffsetMinMHz: -500, CoreOffsetMaxMHz: 1000,
        MemoryOffsetMinMHz: -1000, MemoryOffsetMaxMHz: 3000,
        PowerMinW: 150, PowerMaxW: 400, PowerDefaultW: 360,
        CoreClockMinMHz: 210, CoreClockMaxMHz: 3105,
        FanMinPercent: 30, FanMaxPercent: 100,
        SupportsFanControl: true);

    public GpuTelemetry ReadTelemetry()
    {
        var limitW = _current.PowerLimitW ?? Limits.PowerDefaultW;
        var maxClock = _current.MaxCoreClockMHz ?? 2900;
        var load = 85 + _random.Next(0, 15);
        var power = Math.Min(limitW, 250 + _random.Next(0, 60) + _current.CoreOffsetMHz / 10.0);
        var temp = 55 + power / 10.0 + _random.NextDouble();
        return new GpuTelemetry(
            DateTimeOffset.Now,
            TemperatureC: Math.Round(temp, 1),
            CoreClockMHz: Math.Min(maxClock, 2700 + _current.CoreOffsetMHz) - _random.Next(0, 30),
            MemoryClockMHz: 15000 + _current.MemoryOffsetMHz,
            PowerW: Math.Round(power, 1),
            PowerLimitW: limitW,
            VramUsedBytes: 6L * 1024 * 1024 * 1024 + _random.Next(0, 512) * 1024L * 1024,
            VramTotalBytes: Info.VramBytes,
            FanPercent: _current.FanPercent ?? (int)Math.Clamp(temp - 20, 30, 100),
            MemoryTemperatureC: Math.Round(temp + 8),
            LoadPercent: load);
    }

    public TuningSettings ReadCurrentSettings() => _current;

    public void Apply(TuningSettings settings) => _current = settings;

    public void ResetToStock() => _current = TuningSettings.Stock;

    public void Dispose() { }
}
