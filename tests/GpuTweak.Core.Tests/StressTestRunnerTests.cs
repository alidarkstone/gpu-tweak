using GpuTweak.Core.AutoTune;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Tests;

public sealed class StressTestRunnerTests
{
    private readonly FakeBackend _backend = new();
    private static readonly TuningSettings Uv = new(CoreOffsetMHz: 135, MaxCoreClockMHz: 2700);

    private sealed class ScriptedStress(FakeBackend backend, int failOnRun = int.MaxValue) : IStressTest
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
            return Task.FromResult(SettingsSeen.Count == failOnRun ? StressResult.Fail("hesap hatası") : new StressResult(true, 100, null));
        }

        public void Dispose() { }
    }

    private static StressTestOptions Fast => new()
    {
        Duration = TimeSpan.FromMinutes(5), Segment = TimeSpan.FromMinutes(1), SampleInterval = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task PreparesAtStockAndRunsWithTheGivenSettings()
    {
        _backend.Apply(Uv);
        var stress = new ScriptedStress(_backend);
        var result = await new StressTestRunner(_backend, stress, Fast).RunAsync(Uv, CancellationToken.None);

        Assert.True(result.Passed);
        Assert.True(stress.PreparedAt!.IsStock);
        Assert.Equal(5, stress.SettingsSeen.Count);
        Assert.All(stress.SettingsSeen, s => Assert.Equal(Uv, s));
        Assert.Equal(TimeSpan.FromMinutes(5), result.Elapsed);
    }

    [Fact]
    public async Task StopsAtTheFirstError()
    {
        var stress = new ScriptedStress(_backend, failOnRun: 2);
        var steps = new List<TuneStep>();
        var result = await new StressTestRunner(_backend, stress, Fast, steps: new SyncProgress<TuneStep>(steps.Add)).RunAsync(Uv, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Equal("hesap hatası", result.Failure);
        Assert.Equal(2, stress.SettingsSeen.Count);
        Assert.False(steps[^1].Passed);
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
