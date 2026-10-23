using GpuTweak.Core.AutoTune;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Tests;

public sealed class AutoTunerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gputweak-tune").FullName;
    private readonly FakeBackend _backend = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Simulated chip: fails above a given offset, optionally loses throughput above another.</summary>
    private sealed class FakeStress(FakeBackend backend, int failAbove, int stretchAbove = int.MaxValue) : IStressTest
    {
        public List<(StressPattern Pattern, TuningSettings Settings)> Runs { get; } = [];

        public Task<StressResult> PrepareAsync(CancellationToken ct) => Task.FromResult(new StressResult(true, 100, null));

        public Task<StressResult> RunAsync(StressPattern pattern, TimeSpan duration, CancellationToken ct)
        {
            var s = backend.Current;
            Runs.Add((pattern, s));
            if (s.CoreOffsetMHz > failAbove) return Task.FromResult(StressResult.Fail("hesap hatası"));
            var score = s.CoreOffsetMHz > stretchAbove ? 95 : 100;
            return Task.FromResult(new StressResult(true, score, null));
        }

        public void Dispose() { }
    }

    private static TunerOptions Fast => new()
    {
        BoostProbe = TimeSpan.Zero, Baseline = TimeSpan.Zero, Step = TimeSpan.Zero, Validation = TimeSpan.Zero,
        SampleInterval = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task FindsHighestPassingOffsetMinusMargin()
    {
        var stress = new FakeStress(_backend, failAbove: 200);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        // Steps of 30: 180 passes, 210 fails. 180 - 45 = 135.
        Assert.Equal(135, result.Settings!.CoreOffsetMHz);
        Assert.Equal(2700, result.Settings.MaxCoreClockMHz); // FakeBackend reports 2700 MHz at >50% load
        Assert.Null(TuneJournal.Load(_dir));
    }

    [Fact]
    public async Task PerformanceGoalRaisesOffsetWithoutClockCap()
    {
        var stress = new FakeStress(_backend, failAbove: 200);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(null, CancellationToken.None, TuneGoal.Performance);

        Assert.True(result.Success, result.Message);
        Assert.Equal(135, result.Settings!.CoreOffsetMHz);
        Assert.Null(result.Settings.MaxCoreClockMHz);
        Assert.Equal(_backend.Limits.PowerMaxW, result.Settings.PowerLimitW);
    }

    [Fact]
    public async Task EfficientGoalCapsBelowStockBoost()
    {
        var stress = new FakeStress(_backend, failAbove: 200);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(null, CancellationToken.None, TuneGoal.Efficient);

        Assert.True(result.Success, result.Message);
        Assert.Equal(2535, result.Settings!.MaxCoreClockMHz); // 2700 * 0.94 = 2538, rounded down to 15 MHz
    }

    [Fact]
    public async Task ThroughputLossCountsAsFailure()
    {
        var stress = new FakeStress(_backend, failAbove: 400, stretchAbove: 150);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(null, CancellationToken.None);
        Assert.Equal(105, result.Settings!.CoreOffsetMHz); // 150 last good, minus 45
    }

    [Fact]
    public async Task NoHeadroomEndsAtStock()
    {
        var stress = new FakeStress(_backend, failAbove: 20);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(null, CancellationToken.None);
        Assert.False(result.Success);
        Assert.True(_backend.Current.IsStock);
    }

    [Fact]
    public async Task CardReturnsToStockBetweenSteps()
    {
        var stress = new FakeStress(_backend, failAbove: 100);
        await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(null, CancellationToken.None);
        // Every applied tuning step is followed by a reset; stock is tested before anything else.
        Assert.True(stress.Runs[0].Settings.IsStock);
        Assert.True(_backend.ResetCount >= stress.Runs.Count - 2);
    }

    [Fact]
    public async Task ResumeAfterCrashTreatsTestedOffsetAsCeiling()
    {
        var journal = new TuneJournal(2700, 100, 300, 70, LastStableOffsetMHz: 120, TestingOffsetMHz: 150, DateTimeOffset.Now);
        var stress = new FakeStress(_backend, failAbove: 1000);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(journal, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(stress.Runs, r => r.Settings.CoreOffsetMHz >= 150);
        Assert.Equal(75, result.Settings!.CoreOffsetMHz); // 120 - 45
    }

    [Fact]
    public async Task CancelResetsToStock()
    {
        using var cts = new CancellationTokenSource();
        var stress = new FakeStress(_backend, failAbove: 1000);
        var tuner = new AutoTuner(_backend, stress, _dir, Fast, new SyncProgress(p => { if (p.OffsetMHz >= 90) cts.Cancel(); }));
        var result = await tuner.RunAsync(null, cts.Token);
        Assert.False(result.Success);
        Assert.True(_backend.Current.IsStock);
    }

    private sealed class SyncProgress(Action<TuneProgress> action) : IProgress<TuneProgress>
    {
        public void Report(TuneProgress value) => action(value);
    }
}
