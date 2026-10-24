using GpuTweak.Core.Backends;
using GpuTweak.Core.Localization;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.AutoTune;

public sealed record MemoryTunerOptions
{
    public TimeSpan Step { get; init; } = TimeSpan.FromSeconds(45);
    /// <summary>Number of steps from 0 to the driver's maximum memory offset.</summary>
    public int Steps { get; init; } = 10;
    /// <summary>Bandwidth below the best so far by more than this means the chips started retrying transfers.</summary>
    public double DropTolerance { get; init; } = 0.005;
    /// <summary>Steps that add less than this much bandwidth count as "no gain"; two in a row end the search.</summary>
    public double MinGain { get; init; } = 0.002;
    public TimeSpan ValidateMemory { get; init; } = TimeSpan.FromMinutes(3);
    public TimeSpan ValidateMixed { get; init; } = TimeSpan.FromMinutes(2);
    public double AbortTemperatureC { get; init; } = 85;
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

public sealed record MemoryTuneResult(
    bool Success,
    string Message,
    TuningSettings? Settings,
    double BaselineGBps,
    double TunedGBps,
    /// <summary>Highest offset that passed without errors or a bandwidth drop.</summary>
    int HighestPassedOffsetMHz)
{
    public static MemoryTuneResult Failed(string message) => new(false, message, null, 0, 0, 0);
}

/// <summary>
/// Finds a safe memory overclock on top of the current (core) settings:
/// 1. measure bandwidth with a self-checking memory workload at offset 0,
/// 2. raise the memory offset step by step; stop at the first error, driver reset, or when bandwidth stops rising
///    or falls (GDDR6X/GDDR7 retry failed transfers, which costs bandwidth before anything visibly breaks),
/// 3. keep the offset with the highest bandwidth, step back one step as safety margin and validate it.
/// </summary>
public sealed class MemoryTuner(IGpuBackend backend, IStressTest stress, MemoryTunerOptions? options = null,
    IProgress<TuneProgress>? progress = null, IProgress<TuneStep>? steps = null)
{
    private readonly MemoryTunerOptions _options = options ?? new MemoryTunerOptions();

    /// <summary>Step size: an even split of the driver's range, in 50 MHz units (5 MHz for the small AMD ranges).</summary>
    public static int StepFor(TuningLimits limits, int steps)
    {
        var unit = limits.MemoryOffsetMaxMHz >= 1000 ? 50 : 5;
        return Math.Max(unit, (int)Math.Round(limits.MemoryOffsetMaxMHz / (double)Math.Max(1, steps) / unit) * unit);
    }

    public async Task<MemoryTuneResult> RunAsync(TuningSettings current, CancellationToken ct)
    {
        var limits = backend.Limits;
        if (limits.MemoryOffsetMaxMHz <= 0) return MemoryTuneResult.Failed(L.T("Bu kart bellek overclock'una izin vermiyor."));
        var step = StepFor(limits, _options.Steps);
        var core = current with { MemoryOffsetMHz = 0 };
        var success = false;

        try
        {
            backend.ResetToStock();
            progress?.Report(new TuneProgress(TunePhase.Preparing, L.T("Test hazırlanıyor (stok ayarlarda referans hesaplanıyor)…"), 0));
            var prepare = await stress.PrepareAsync(ct);
            if (!prepare.Passed) return MemoryTuneResult.Failed(L.F("Hazırlık geçemedi: {0}", prepare.Failure));

            Apply(core);
            progress?.Report(new TuneProgress(TunePhase.Baseline, L.T("Bellek hızı ölçülüyor (offset 0)…"), 0.05));
            var baseline = await Measure(core, L.T("Bellek +0 MHz"), BaselineRow, ct);
            if (!baseline.Passed)
                return MemoryTuneResult.Failed(L.F("Bellek testi mevcut ayarlarda bile hata verdi ({0}). Önce çekirdek ayarını doğrula.", baseline.Failure));

            var best = (Offset: 0, Score: baseline.Score);
            var noGain = 0;
            var highestPassed = 0;
            var maxSteps = (int)Math.Ceiling(limits.MemoryOffsetMaxMHz / (double)step);
            for (var i = 1; i <= maxSteps; i++)
            {
                var offset = Math.Min(i * step, limits.MemoryOffsetMaxMHz);
                progress?.Report(new TuneProgress(TunePhase.Searching, L.F("Bellek +{0} MHz deneniyor…", offset), 0.05 + 0.7 * i / maxSteps, offset));
                var settings = core with { MemoryOffsetMHz = offset };
                Apply(settings);
                var run = await Measure(settings, L.F("Bellek +{0} MHz", offset), baseline.Score, ct);
                if (!run.Passed) break;
                if (run.Score < best.Score * (1 - _options.DropTolerance))
                {
                    steps?.Report(new TuneStep(L.F("Bellek +{0} MHz", offset), true, L.T("bant genişliği düştü, durduruldu"), run.Score / baseline.Score, 0, 0));
                    break;
                }
                highestPassed = offset;
                if (run.Score > best.Score * (1 + _options.MinGain))
                {
                    best = (offset, run.Score);
                    noGain = 0;
                }
                else if (++noGain >= 2) break;
            }

            if (best.Offset == 0)
            {
                Apply(current);
                success = true;
                return new MemoryTuneResult(true, L.T("Bellek overclock'u bu kartta ölçülebilir bir hız kazandırmadı; bellek ayarı değiştirilmedi."),
                    current, baseline.Score, baseline.Score, highestPassed);
            }

            // One step below the best for headroom; validate, and give up one more step if needed.
            var chosen = Math.Max(0, best.Offset - step);
            while (chosen > 0)
            {
                ct.ThrowIfCancellationRequested();
                var settings = core with { MemoryOffsetMHz = chosen };
                Apply(settings);
                progress?.Report(new TuneProgress(TunePhase.Validating, L.F("Bellek +{0} MHz doğrulanıyor…", chosen), 0.8, chosen));
                var memory = await Measure(settings, L.F("Doğrulama +{0} MHz", chosen), baseline.Score, ct, _options.ValidateMemory);
                var mixed = memory.Passed ? await Measure(settings, L.F("Doğrulama +{0} MHz (karışık)", chosen), 0, ct, _options.ValidateMixed, StressPattern.Mixed) : memory;
                if (memory.Passed && mixed.Passed)
                {
                    success = true;
                    var gain = 100 * (memory.Score / baseline.Score - 1);
                    return new MemoryTuneResult(true,
                        L.F("Bellek +{0} MHz'de hatasız; bant genişliği %{1:0.0} arttı ({2:0} → {3:0} GB/s). En iyi ölçüm +{4} MHz'deydi, bir adım güvenlik payı bırakıldı.",
                            chosen, gain, baseline.Score, memory.Score, best.Offset),
                        settings, baseline.Score, memory.Score, highestPassed);
                }
                chosen = Math.Max(0, chosen - step);
            }

            Apply(current);
            success = true;
            return new MemoryTuneResult(true, L.T("Bellek overclock'u doğrulamayı geçemedi; bellek ayarı değiştirilmedi."), current, baseline.Score, baseline.Score, highestPassed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return MemoryTuneResult.Failed(L.T("Bellek testi durduruldu."));
        }
        finally
        {
            // Anything but a finished search goes back to what the user had.
            if (!success)
            {
                try
                {
                    if (current.IsStock) backend.ResetToStock();
                    else backend.Apply(current);
                }
                catch (GpuBackendException) { }
            }
        }
    }

    private void Apply(TuningSettings settings)
    {
        if (settings.IsStock) backend.ResetToStock();
        else backend.Apply(settings);
    }

    private sealed record Measurement(bool Passed, double Score, string? Failure);
    /// <summary>Passed as baseline score for the baseline row itself, which is 100% by definition.</summary>
    private const double BaselineRow = -1;

    /// <summary>Runs one step, reports it as a table row and watches temperature.</summary>
    private async Task<Measurement> Measure(TuningSettings settings, string label, double baselineScore, CancellationToken ct,
        TimeSpan? duration = null, StressPattern pattern = StressPattern.Memory)
    {
        var samples = new List<GpuTelemetry>();
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
                    if (s.TemperatureC >= _options.AbortTemperatureC) { overheated = true; sampling.Cancel(); }
                }
                catch (Exception) { /* a missed sample is fine */ }
            }
        }, CancellationToken.None);

        StressResult result;
        try
        {
            result = await stress.RunAsync(pattern, duration ?? _options.Step, sampling.Token);
        }
        catch (OperationCanceledException) when (overheated && !ct.IsCancellationRequested)
        {
            result = StressResult.Fail(L.F("sıcaklık {0:0}°C'yi geçti", _options.AbortTemperatureC));
        }
        finally
        {
            sampling.Cancel();
            await sampler;
        }

        List<GpuTelemetry> snapshot;
        lock (samples) snapshot = [.. samples];
        var relative = !result.Passed || pattern != StressPattern.Memory ? 0
            : baselineScore == BaselineRow ? 1
            : baselineScore > 0 ? result.Score / baselineScore : 0;
        steps?.Report(new TuneStep(label, result.Passed, result.Failure, relative,
            snapshot.Select(s => s.PowerW ?? 0).DefaultIfEmpty(0).Average(),
            snapshot.Select(s => s.TemperatureC ?? 0).DefaultIfEmpty(0).Max()));
        return new Measurement(result.Passed, result.Score, result.Failure);
    }
}
