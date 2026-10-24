using GpuTweak.Core.AutoTune;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Tests;

public sealed class MemoryTunerTests
{
    private readonly FakeBackend _backend = new();  // memory offset range up to +3000 → 300 MHz steps
    private static readonly TuningSettings Uv = new(CoreOffsetMHz: 405, MaxCoreClockMHz: 2835);

    /// <summary>Bandwidth rises with the memory offset; errors above <paramref name="failAbove"/>, retries (lower bandwidth) above <paramref name="dropAbove"/>.</summary>
    private sealed class MemoryCard(FakeBackend backend, int failAbove = int.MaxValue, int dropAbove = int.MaxValue) : IStressTest
    {
        public List<(StressPattern Pattern, TuningSettings Settings)> Runs { get; } = [];

        public Task<StressResult> PrepareAsync(CancellationToken ct) => Task.FromResult(new StressResult(true, 0, null));

        public Task<StressResult> RunAsync(StressPattern pattern, TimeSpan duration, CancellationToken ct)
        {
            var offset = backend.Current.MemoryOffsetMHz;
            Runs.Add((pattern, backend.Current));
            if (offset > failAbove) return Task.FromResult(StressResult.Fail("bellek hatası"));
            var score = 900 * (1 + offset / 15000.0);
            if (offset > dropAbove) score *= 0.97;
            return Task.FromResult(new StressResult(true, score, null));
        }

        public void Dispose() { }
    }

    private static MemoryTunerOptions Fast => new() { SampleInterval = TimeSpan.FromMilliseconds(1) };

    [Fact]
    public async Task StopsAtTheFirstErrorAndKeepsOneStepOfMargin()
    {
        var card = new MemoryCard(_backend, failAbove: 1800);
        var result = await new MemoryTuner(_backend, card, Fast).RunAsync(Uv, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1800, result.HighestPassedOffsetMHz);
        Assert.Equal(Uv with { MemoryOffsetMHz = 1500 }, result.Settings);
        Assert.Equal(result.Settings, _backend.Current);
        Assert.True(result.TunedGBps > result.BaselineGBps);
        // The core settings stay as they were during the whole search.
        Assert.All(card.Runs, r => Assert.Equal(405, r.Settings.CoreOffsetMHz));
    }

    [Fact]
    public async Task ABandwidthDropCountsAsTheLimit()
    {
        var card = new MemoryCard(_backend, dropAbove: 2100);
        var result = await new MemoryTuner(_backend, card, Fast).RunAsync(Uv, CancellationToken.None);

        Assert.Equal(2100, result.HighestPassedOffsetMHz);
        Assert.Equal(1800, result.Settings!.MemoryOffsetMHz);
    }

    [Fact]
    public async Task ErrorsAtOffsetZeroLeaveTheSettingsAlone()
    {
        var card = new MemoryCard(_backend, failAbove: -1);
        _backend.Apply(Uv);
        var result = await new MemoryTuner(_backend, card, Fast).RunAsync(Uv, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(Uv, _backend.Current);
    }

    [Fact]
    public async Task CancellingRestoresTheOriginalSettings()
    {
        using var cts = new CancellationTokenSource();
        var card = new CancellingCard(_backend, cts);
        _backend.Apply(Uv);
        var result = await new MemoryTuner(_backend, card, Fast).RunAsync(Uv, cts.Token);

        Assert.False(result.Success);
        Assert.Equal(Uv, _backend.Current);
    }

    private sealed class CancellingCard(FakeBackend backend, CancellationTokenSource cts) : IStressTest
    {
        public Task<StressResult> PrepareAsync(CancellationToken ct) => Task.FromResult(new StressResult(true, 0, null));

        public Task<StressResult> RunAsync(StressPattern pattern, TimeSpan duration, CancellationToken ct)
        {
            if (backend.Current.MemoryOffsetMHz >= 600) { cts.Cancel(); ct.ThrowIfCancellationRequested(); }
            return Task.FromResult(new StressResult(true, 900 + backend.Current.MemoryOffsetMHz / 10.0, null));
        }

        public void Dispose() { }
    }
}
