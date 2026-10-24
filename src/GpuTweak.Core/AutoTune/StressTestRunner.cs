using GpuTweak.Core.Backends;
using GpuTweak.Core.Localization;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.AutoTune;

public sealed record StressTestOptions
{
    /// <summary>Steady load with the settings as they are: shows heat soak and throughput drops.</summary>
    public TimeSpan Sustained { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>Short bursts with idle gaps: clocks and current jump like in game menus and loading screens.</summary>
    public TimeSpan Transient { get; init; } = TimeSpan.FromMinutes(8);
    /// <summary>One row in the results table per segment.</summary>
    public TimeSpan Segment { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>
    /// Clock points below the top that are each held for <see cref="SweepSegment"/>. A core offset lowers the voltage
    /// at every point of the curve, but a full load only ever runs the top one; these check the rest.
    /// </summary>
    public int SweepPoints { get; init; } = 6;
    public int SweepStartMHz { get; init; } = 1200;
    public TimeSpan SweepSegment { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>On a calculation error or driver reset the core offset is lowered by this much and the segment repeated.</summary>
    public int StepDownMHz { get; init; } = 30;
    public int MaxStepDowns { get; init; } = 4;
    public double AbortTemperatureC { get; init; } = 85;
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan Total => Sustained + Transient + SweepSegment * SweepPoints;
}

/// <summary>One part of the test. <see cref="ClockCapMHz"/> holds the card at a lower point of its curve.</summary>
public sealed record StressSegment(string Label, StressPattern Pattern, TimeSpan Duration, int? ClockCapMHz = null);

public sealed record StressTestResult(
    bool Passed,
    bool Cancelled,
    string? Failure,
    TimeSpan Elapsed,
    double MaxTemperatureC,
    double AvgPowerW,
    int AvgCoreClockMHz,
    /// <summary>Lowest full-load segment throughput relative to the first one; a drop means clock stretching or throttling.</summary>
    double LowestRelativeScore,
    /// <summary>Settings the test ended with; lower than the starting ones when it had to step down.</summary>
    TuningSettings? FinalSettings = null,
    int StepDowns = 0);

/// <summary>
/// Verification test with the settings that are on the card now, like FurMark, but every result is checked.
/// Runs a steady load, then load jumps, then a sweep over lower clock points so the whole V/F curve is covered.
/// On an error it lowers the core offset a step and repeats that part, so the user ends up with settings that passed.
/// </summary>
public sealed class StressTestRunner(IGpuBackend backend, IStressTest stress, StressTestOptions? options = null,
    IProgress<TuneProgress>? progress = null, IProgress<TuneStep>? steps = null)
{
    private readonly StressTestOptions _options = options ?? new StressTestOptions();

    public static IReadOnlyList<StressSegment> BuildPlan(TuningSettings settings, TuningLimits limits, StressTestOptions options)
    {
        var plan = new List<StressSegment>();
        AddSlices(L.T("Sürekli yük"), StressPattern.Mixed, options.Sustained);
        AddSlices(L.T("Ani yük"), StressPattern.Transient, options.Transient);

        // Evenly spaced points from the start clock up to (not including) the top the card runs at anyway.
        var top = settings.MaxCoreClockMHz ?? Math.Min(limits.CoreClockMaxMHz, 3000);
        var start = Math.Max(options.SweepStartMHz, limits.CoreClockMinMHz);
        if (options.SweepPoints > 0 && top - start >= 100)
        {
            var step = (top - start) / (double)options.SweepPoints;
            for (var i = 0; i < options.SweepPoints; i++)
            {
                var clock = (int)Math.Round((start + i * step) / 15) * 15;  // driver clock bins are 15 MHz
                plan.Add(new StressSegment(L.F("Eğri {0} MHz", clock), StressPattern.Heavy, options.SweepSegment, clock));
            }
        }
        return plan;

        void AddSlices(string label, StressPattern pattern, TimeSpan total)
        {
            var count = (int)Math.Ceiling(total / options.Segment);
            for (var i = 0; i < count; i++)
            {
                var length = i < count - 1 ? options.Segment : total - options.Segment * (count - 1);
                plan.Add(new StressSegment($"{label} {i + 1}/{count}", pattern, length));
            }
        }
    }

    public async Task<StressTestResult> RunAsync(TuningSettings settings, CancellationToken ct)
    {
        var samples = new List<GpuTelemetry>();
        var elapsed = TimeSpan.Zero;
        double? firstScore = null;
        var lowest = 1.0;
        string? failure = null;
        var cancelled = false;
        var current = settings;
        var stepDowns = 0;

        // The reference answers must come from stock settings, or an unstable setting could poison them.
        backend.ResetToStock();
        try
        {
            progress?.Report(new TuneProgress(TunePhase.Preparing, L.T("Test hazırlanıyor (stok ayarlarda referans hesaplanıyor)…"), 0));
            var prepare = await stress.PrepareAsync(ct);
            if (!prepare.Passed) return Result(false, false, prepare.Failure, TimeSpan.Zero, samples, 1, current, 0);
        }
        finally
        {
            if (!settings.IsStock) backend.Apply(settings);
        }

        var plan = BuildPlan(settings, backend.Limits, _options);
        var total = plan.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);

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
            while (index < plan.Count)
            {
                var segment = plan[index];
                var segmentSettings = segment.ClockCapMHz is { } cap
                    ? current with { MaxCoreClockMHz = Math.Min(cap, current.MaxCoreClockMHz ?? int.MaxValue) }
                    : current;
                if (segment.ClockCapMHz is not null || index > 0 && plan[index - 1].ClockCapMHz is not null)
                    backend.Apply(segmentSettings);

                progress?.Report(new TuneProgress(TunePhase.Validating,
                    L.F("Doğrulama: {0} ({1}/{2})", segment.Label, index + 1, plan.Count), elapsed / total));

                var before = SampleCount();
                var result = await stress.RunAsync(segment.Pattern, segment.Duration, sampling.Token);
                elapsed += segment.Duration;

                // Throughput is only comparable between full-clock segments.
                var relative = 0.0;
                if (segment.ClockCapMHz is null && result.Passed && result.Score > 0)  // load-spike parts report no throughput
                {
                    firstScore ??= result.Score;
                    relative = firstScore > 0 ? result.Score / firstScore.Value : 0;
                    lowest = Math.Min(lowest, relative);
                }
                var part = Snapshot().Skip(before).ToList();
                steps?.Report(new TuneStep(segment.Label, result.Passed, result.Failure, relative,
                    part.Select(s => s.PowerW ?? 0).DefaultIfEmpty(0).Average(),
                    part.Select(s => s.TemperatureC ?? 0).DefaultIfEmpty(0).Max()));

                if (result.Passed)
                {
                    index++;
                    continue;
                }

                // Too little voltage for this offset somewhere on the curve: give it a bit more and try the same part again.
                if (stepDowns < _options.MaxStepDowns && current.CoreOffsetMHz > 0)
                {
                    stepDowns++;
                    var lowered = Math.Max(0, current.CoreOffsetMHz - _options.StepDownMHz);
                    steps?.Report(new TuneStep(L.F("Çekirdek +{0} → +{1} MHz", current.CoreOffsetMHz, lowered), true,
                        L.T("hata sonrası bir adım düşürüldü"), 0, 0, 0));
                    current = current with { CoreOffsetMHz = lowered };
                    backend.Apply(current);
                    continue;
                }

                failure = result.Failure ?? L.T("hata");
                break;
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
            // Leave the card on the (possibly lowered) settings without a sweep clock cap. After a failure the
            // caller decides what to do with custom settings.
            if (current.IsStock) backend.ResetToStock();
            else if (failure is null) backend.Apply(current);
        }

        return Result(failure is null && !cancelled, cancelled, failure, elapsed, Snapshot(), lowest, current, stepDowns);

        int SampleCount() { lock (samples) return samples.Count; }
        List<GpuTelemetry> Snapshot() { lock (samples) return [.. samples]; }
    }

    private static StressTestResult Result(bool passed, bool cancelled, string? failure, TimeSpan elapsed, List<GpuTelemetry> samples,
        double lowest, TuningSettings final, int stepDowns)
    {
        var loaded = samples.Where(s => s.LoadPercent is > 20).ToList();
        return new StressTestResult(passed, cancelled, failure, elapsed,
            samples.Select(s => s.TemperatureC ?? 0).DefaultIfEmpty(0).Max(),
            loaded.Select(s => s.PowerW ?? 0).DefaultIfEmpty(0).Average(),
            (int)loaded.Select(s => (double)(s.CoreClockMHz ?? 0)).DefaultIfEmpty(0).Average(),
            lowest, final, stepDowns);
    }
}
