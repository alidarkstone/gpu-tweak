using GpuTweak.Core.Models;
using GpuTweak.Core.Safety;

namespace GpuTweak.Core.Tests;

public class SafetyClampTests
{
    private readonly TuningLimits _limits = new FakeBackend().Limits;

    [Fact]
    public void ValuesInsideLimitsAreKept()
    {
        var requested = new TuningSettings(150, 1000, 300, 2700, 60);
        var result = SafetyClamp.Clamp(requested, _limits);
        Assert.Equal(requested, result.Settings);
        Assert.False(result.WasAdjusted);
    }

    [Fact]
    public void ValuesOutsideLimitsAreClampedAndReported()
    {
        var result = SafetyClamp.Clamp(new TuningSettings(5000, -9000, 999, 9999, 5), _limits);
        Assert.Equal(new TuningSettings(1000, -1000, 400, 3100, 30), result.Settings);
        Assert.Equal(5, result.Adjustments.Count);
    }

    [Fact]
    public void PowerLimitEqualToDefaultBecomesNull()
    {
        var result = SafetyClamp.Clamp(new TuningSettings(PowerLimitW: 360), _limits);
        Assert.Null(result.Settings.PowerLimitW);
    }

    [Fact]
    public void FanIsDroppedWhenCardHasNoFanControl()
    {
        var limits = _limits with { SupportsFanControl = false };
        var result = SafetyClamp.Clamp(new TuningSettings(FanPercent: 70), limits);
        Assert.Null(result.Settings.FanPercent);
        Assert.True(result.WasAdjusted);
    }
}
