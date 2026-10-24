using GpuTweak.Core.AutoTune;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Tests;

public sealed class StressTestRunnerTests
{
    private readonly FakeBackend _backend = new();
    private static readonly TuningSettings Uv = new(CoreOffsetMHz: 135, MaxCoreClockMHz: 2700);

    private sealed class ScriptedStress(FakeBackend backend, Func<int, TuningSettings, bool>? fails = null) : IStressTest
    {
        public List<TuningSettings> SettingsSeen { get; } = [];
        public TuningSettings? PreparedAt { get; private set; }

        public Task<StressResult> PrepareAsync(CancellationToken ct)
        {
            PreparedAt = backend.Current;
            return Task.FromResult(new StressResult(true, 100, null));
        }

        public Task<StressResult> RunAsync(StressPattern pattern, TimeSpan duration, CancellationToken ct)
        {
            SettingsSeen.Add(backend.Current);
            var fail = fails?.Invoke(SettingsSeen.Count, backend.Current) == true;
            return Task.FromResult(fail ? StressResult.Fail("hesap hatası") : new StressResult(true, 100, null));
        }

        public void Dispose() { }
    }

    // 3 sustained + 2 transient + 3 sweep segments.
    private static StressTestOptions Fast => new()
    {
        Sustained = TimeSpan.FromMinutes(3), Transient = TimeSpan.FromMinutes(2), Segment = TimeSpan.FromMinutes(1),
        SweepPoints = 3, SweepStartMHz = 1200, SweepSegment = TimeSpan.FromMinutes(1),
        SampleInterval = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task PreparesAtStockThenRunsFullLoadAndASweepBelowTheTopClock()
    {
        _backend.Apply(Uv);
        var stress = new ScriptedStress(_backend);
        var result = await new StressTestRunner(_backend, stress, Fast).RunAsync(Uv, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.True(stress.PreparedAt!.IsStock);
        Assert.Equal(8, stress.SettingsSeen.Count);
        Assert.All(stress.SettingsSeen.Take(5), s => Assert.Equal(Uv, s));
        // Sweep: 1200, 1700, 2205 (rounded to 15 MHz bins), all with the same offset.
        Assert.Equal([1200, 1695, 2205], stress.SettingsSeen.Skip(5).Select(s => s.MaxCoreClockMHz!.Value));
        Assert.All(stress.SettingsSeen.Skip(5), s => Assert.Equal(135, s.CoreOffsetMHz));
        Assert.Equal(TimeSpan.FromMinutes(8), result.Elapsed);
        Assert.Equal(Uv, _backend.Current);  // sweep cap removed again
    }

    [Fact]
    public async Task StepsDownAndRepeatsThePartThatFailed()
    {
        // Fails at the lowest sweep clock until the offset is below +100.
        var stress = new ScriptedStress(_backend, (_, s) => s.MaxCoreClockMHz == 1200 && s.CoreOffsetMHz > 100);
        var steps = new List<TuneStep>();
        var result = await new StressTestRunner(_backend, stress, Fast, steps: new SyncProgress<TuneStep>(steps.Add)).RunAsync(Uv, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.Equal(2, result.StepDowns);
        Assert.Equal(75, result.FinalSettings!.CoreOffsetMHz);
        Assert.Equal(result.FinalSettings, _backend.Current);
        Assert.Contains(steps, s => s.Label.Contains("+135") && s.Label.Contains("+105"));
    }

    [Fact]
    public async Task FailsWhenSteppingDownDoesNotHelp()
    {
        var stress = new ScriptedStress(_backend, (run, _) => run >= 2);
        var options = Fast with { MaxStepDowns = 2 };
        var result = await new StressTestRunner(_backend, stress, options).RunAsync(Uv, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("hesap hatası", result.Failure);
        Assert.Equal(2, result.StepDowns);
        Assert.Equal(4, stress.SettingsSeen.Count);
    }

    [Fact]
    public async Task StockSettingsAreNotSteppedAndEndAtStock()
    {
        var stress = new ScriptedStress(_backend, (run, _) => run == 6);
        var result = await new StressTestRunner(_backend, stress, Fast).RunAsync(TuningSettings.Stock, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal(0, result.StepDowns);
        Assert.True(_backend.Current.IsStock);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
