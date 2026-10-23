using GpuTweak.Core.Backends;
using GpuTweak.Core.Localization;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.AutoTune;

public sealed record StressTestOptions
{
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>One row in the results table per segment.</summary>
    public TimeSpan Segment { get; init; } = TimeSpan.FromMinutes(1);
    public double AbortTemperatureC { get; init; } = 85;
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

public sealed record StressTestResult(
    bool Passed,
    bool Cancelled,
    string? Failure,
    TimeSpan Elapsed,
    double MaxTemperatureC,
    double AvgPowerW,
    int AvgCoreClockMHz,
    /// <summary>Lowest segment throughput relative to the first one; a drop means clock stretching or throttling.</summary>
    double LowestRelativeScore);

/// <summary>
/// Burn-in with the settings that are on the card now, like FurMark, but every result is verified.
/// Stops at the first calculation error, driver reset or the temperature limit.
/// </summary>
public sealed class StressTestRunner(IGpuBackend backend, IStressTest stress, StressTestOptions? options = null,
    IProgress<TuneProgress>? progress = null, IProgress<TuneStep>? steps = null)
{
    private readonly StressTestOptions _options = options ?? new StressTestOptions();

    public async Task<StressTestResult> RunAsync(TuningSettings settings, CancellationToken ct)
    {
        var samples = new List<GpuTelemetry>();
        var elapsed = TimeSpan.Zero;
        double? firstScore = null;
        var lowest = 1.0;
        string? failure = null;
        var cancelled = false;

        // The reference answers must come from stock settings, or an unstable setting could poison them.
        backend.ResetToStock();
        try
        {
            progress?.Report(new TuneProgress(TunePhase.Preparing, L.T("Test hazırlanıyor (stok ayarlarda referans hesaplanıyor)…"), 0));
            var prepare = await stress.PrepareAsync(ct);
            if (!prepare.Passed) return Result(false, false, prepare.Failure, TimeSpan.Zero, samples, 1);
        }
        finally
        {
            if (!settings.IsStock) backend.Apply(settings);
        }

        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var overheated = false;
        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                try { await Task.Delay(_options.SampleInterval, sampling.Token); }
                catch (OperationCanceledException) { break; }
                try
                {
                    var s = backend.ReadTelemetry();
                    lock (samples) samples.Add(s);
                    if (s.TemperatureC >= _options.AbortTemperatureC)
                    {
                        overheated = true;
                        sampling.Cancel();
                    }
                }
                catch (Exception) { /* a missed sample is fine */ }
            }
        }, CancellationToken.None);

        try
        {
            var index = 0;
            while (elapsed < _options.Duration)
            {
                index++;
                var length = _options.Segment < _options.Duration - elapsed ? _options.Segment : _options.Duration - elapsed;
                progress?.Report(new TuneProgress(TunePhase.Validating,
                    L.F("Stres testi: {0}. dakika / {1:0}", index, _options.Duration.TotalMinutes), elapsed / _options.Duration));

                var before = SampleCount();
                var result = await stress.RunAsync(StressPattern.Mixed, length, sampling.Token);
                elapsed += length;

                firstScore ??= result.Score;
                var relative = firstScore > 0 ? result.Score / firstScore.Value : 0;
                if (result.Passed) lowest = Math.Min(lowest, relative);
                var segment = Snapshot().Skip(before).ToList();
                steps?.Report(new TuneStep(L.F("{0}. dakika", index), result.Passed, result.Failure, relative,
                    segment.Select(s => s.PowerW ?? 0).DefaultIfEmpty(0).Average(),
                    segment.Select(s => s.TemperatureC ?? 0).DefaultIfEmpty(0).Max()));

                if (!result.Passed)
                {
                    failure = result.Failure ?? L.T("hata");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (overheated && !ct.IsCancellationRequested)
        {
            failure = L.F("sıcaklık {0:0}°C'yi geçti", _options.AbortTemperatureC);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            sampling.Cancel();
            await sampler;
        }

        return Result(failure is null && !cancelled, cancelled, failure, elapsed, Snapshot(), lowest);

        int SampleCount() { lock (samples) return samples.Count; }
        List<GpuTelemetry> Snapshot() { lock (samples) return [.. samples]; }
    }

    private static StressTestResult Result(bool passed, bool cancelled, string? failure, TimeSpan elapsed, List<GpuTelemetry> samples, double lowest)
    {
        var loaded = samples.Where(s => s.LoadPercent is > 20).ToList();
        return new StressTestResult(passed, cancelled, failure, elapsed,
            samples.Select(s => s.TemperatureC ?? 0).DefaultIfEmpty(0).Max(),
            loaded.Select(s => s.PowerW ?? 0).DefaultIfEmpty(0).Average(),
            (int)loaded.Select(s => (double)(s.CoreClockMHz ?? 0)).DefaultIfEmpty(0).Average(),
            lowest);
    }
}
