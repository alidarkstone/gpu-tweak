using GpuTweak.Core.Models;

namespace GpuTweak.Core.Safety;

/// <summary>
/// Watches temperature while custom settings are active. After <see cref="RequiredSamples"/> consecutive
/// samples at or above <see cref="LimitC"/>, it tells the caller to fall back to stock.
/// </summary>
public sealed class ThermalGuard(double limitC = 88, int requiredSamples = 3)
{
    private int _hotSamples;

    public double LimitC { get; } = limitC;
    public int RequiredSamples { get; } = requiredSamples;

    /// <summary>Returns true exactly once when the limit is crossed; the counter then resets.</summary>
    public bool ShouldRevert(GpuTelemetry sample, bool customActive)
    {
        if (!customActive || sample.TemperatureC is not { } temp || temp < LimitC)
        {
            _hotSamples = 0;
            return false;
        }
        if (++_hotSamples < RequiredSamples) return false;
        _hotSamples = 0;
        return true;
    }
}
