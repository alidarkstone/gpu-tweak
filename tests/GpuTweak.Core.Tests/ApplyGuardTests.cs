using GpuTweak.Core.Models;
using GpuTweak.Core.Safety;
using Microsoft.Extensions.Time.Testing;

namespace GpuTweak.Core.Tests;

public class ApplyGuardTests
{
    private readonly FakeBackend _backend = new();
    private readonly FakeTimeProvider _time = new();
    private readonly List<ApplyOutcome> _outcomes = [];
    private readonly ApplyGuard _guard;

    public ApplyGuardTests()
    {
        _guard = new ApplyGuard(_backend, _time);
        _guard.Completed += (outcome, _) => _outcomes.Add(outcome);
    }

    [Fact]
    public void RevertsToPreviousSettingsWhenNotConfirmed()
    {
        var previous = new TuningSettings(CoreOffsetMHz: 100);
        _backend.Current = previous;

        _guard.Apply(new TuningSettings(CoreOffsetMHz: 300));
        _time.Advance(TimeSpan.FromSeconds(14));
        Assert.True(_guard.IsPending);
        Assert.Equal(300, _backend.Current.CoreOffsetMHz);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(_guard.IsPending);
        Assert.Equal(previous, _backend.Current);
        Assert.Equal([ApplyOutcome.RevertedByTimeout], _outcomes);
    }

    [Fact]
    public void ConfirmKeepsSettings()
    {
        _guard.Apply(new TuningSettings(CoreOffsetMHz: 300));
        _guard.Confirm();
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(300, _backend.Current.CoreOffsetMHz);
        Assert.Equal([ApplyOutcome.Confirmed], _outcomes);
    }

    [Fact]
    public void RevertingToStockUsesReset()
    {
        _guard.Apply(new TuningSettings(CoreOffsetMHz: 300));
        _guard.Revert();
        Assert.Equal(1, _backend.ResetCount);
        Assert.True(_backend.Current.IsStock);
        Assert.Equal([ApplyOutcome.RevertedByUser], _outcomes);
    }

    [Fact]
    public void SecondApplyKeepsOriginalFallback()
    {
        var original = new TuningSettings(CoreOffsetMHz: 50);
        _backend.Current = original;
        _guard.Apply(new TuningSettings(CoreOffsetMHz: 200));
        _guard.Apply(new TuningSettings(CoreOffsetMHz: 400));
        _time.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(original, _backend.Current);
    }

    [Fact]
    public void RequestIsClampedBeforeReachingTheCard()
    {
        _guard.Apply(new TuningSettings(CoreOffsetMHz: 99999));
        Assert.Equal(1000, _backend.Applied.Single().CoreOffsetMHz);
    }

    [Fact]
    public void FailedApplyRestoresAndReports()
    {
        _backend.FailWhen = s => s.CoreOffsetMHz == 300;
        Assert.Throws<GpuTweak.Core.Backends.GpuBackendException>(() => _guard.Apply(new TuningSettings(CoreOffsetMHz: 300)));
        Assert.False(_guard.IsPending);
        Assert.True(_backend.Current.IsStock);
        Assert.Equal([ApplyOutcome.Failed], _outcomes);
    }

    [Fact]
    public void CountdownTicksDown()
    {
        var ticks = new List<int>();
        _guard.CountdownTick += ticks.Add;
        _guard.Apply(new TuningSettings(CoreOffsetMHz: 10));
        for (var i = 0; i < 3; i++) _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal([15, 14, 13, 12], ticks);
    }
}
