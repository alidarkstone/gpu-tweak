using GpuTweak.Core.Backends;
using GpuTweak.Core.Localization;
using GpuTweak.Core.Models;
using GpuTweak.Core.Safety;

namespace GpuTweak.Core.AutoTune;

public sealed record TunerOptions
{
    public TimeSpan BoostProbe { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan Baseline { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan Step { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan Validation { get; init; } = TimeSpan.FromMinutes(5);
    public int StepMHz { get; init; } = 30;
    /// <summary>Distance kept below the highest offset that passed, so games (which stress differently) stay stable.</summary>
    public int SafetyMarginMHz { get; init; } = 45;
    public int MaxSearchOffsetMHz { get; init; } = 450;
    /// <summary>Work-per-second must stay at least this fraction of stock, otherwise the step counts as a loss.</summary>
    public double MinScoreRatio { get; init; } = 0.99;
    /// <summary>For <see cref="TuneGoal.Efficient"/>: the clock cap as a fraction of the stock boost clock.</summary>
    public double EfficientClockRatio { get; init; } = 0.94;
    public double AbortTemperatureC { get; init; } = 85;
    public TimeSpan SampleInterval { get; init; } = TimeSpan.FromMilliseconds(500);
}

public enum TunePhase { Preparing, BoostProbe, Baseline, Searching, Validating, Done, Failed, Cancelled }

public sealed record TuneProgress(TunePhase Phase, string Message, double Fraction, int? OffsetMHz = null);

/// <summary>One finished measurement, for the live results table. Score is relative to stock (1.0 = same).</summary>
public sealed record TuneStep(string Label, bool Passed, string? Reason, double RelativeScore, double AvgPowerW, double MaxTemperatureC);

public sealed record TuneResult(
    bool Success,
    string Message,
    TuningSettings? Settings,
    double BaselineScore,
    double TunedScore,
    double BaselinePowerW,
    double TunedPowerW,
    double BaselineTemperatureC,
    double TunedTemperatureC,
    int HighestPassedOffsetMHz = 0,
    bool ReachedSearchLimit = false)
{
    public static TuneResult Failed(string message) => new(false, message, null, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Finds the sweet spot for a <see cref="TuneGoal"/>:
/// 1. measure the highest boost clock the card reaches at stock (light, bursty load),
/// 2. Cool: cap the core clock there, so no game runs slower than stock; Efficient: cap it a little lower;
///    Performance: no cap and the power limit at the vBIOS maximum,
/// 3. raise the core offset step by step (= lower voltage at the capped clock, or higher clocks without a cap)
///    while a self-checking workload verifies every result and that real throughput does not drop,
/// 4. step back by a safety margin from the last passing value and validate for several minutes.
/// </summary>
public sealed class AutoTuner
{
    private readonly IGpuBackend _backend;
    private readonly IStressTest _stress;
    private readonly TunerOptions _options;
    private readonly string _dataDirectory;
    private readonly IProgress<TuneProgress>? _progress;
    private readonly IProgress<TuneStep>? _steps;

    public AutoTuner(IGpuBackend backend, IStressTest stress, string dataDirectory, TunerOptions? options = null,
        IProgress<TuneProgress>? progress = null, IProgress<TuneStep>? steps = null)
    {
        _steps = steps;
        _backend = backend;
        _stress = stress;
        _dataDirectory = dataDirectory;
        _options = options ?? new TunerOptions();
        _progress = progress;
    }

    private sealed record Measurement(StressResult Result, double AvgPowerW, double MaxTemperatureC, double HighClockMHz, bool Overheated);

    public async Task<TuneResult> RunAsync(TuneJournal? resume, CancellationToken cancellationToken, TuneGoal goal = TuneGoal.Cool)
    {
        try
        {
            return await RunCoreAsync(resume, resume?.Goal ?? goal, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            SafeReset();
            TuneJournal.Delete(_dataDirectory);
            Report(TunePhase.Cancelled, L.T("Test durduruldu, varsayılan ayarlara dönüldü."), 0);
            return TuneResult.Failed("Test durduruldu.");
        }
        catch (Exception e)
        {
            SafeReset();
            Report(TunePhase.Failed, e.Message, 0);
            return TuneResult.Failed(e.Message);
        }
    }

    private async Task<TuneResult> RunCoreAsync(TuneJournal? resume, TuneGoal goal, CancellationToken ct)
    {
        if (_backend.Limits.CoreOffsetMaxMHz <= 0)
            return Fail(L.T("Sürücü bu kartta çekirdek offset ayarına izin vermiyor, bu yüzden undervolt yapılamıyor. Sürücüyü güncellemeyi dene."));

        _backend.ResetToStock();
        Report(TunePhase.Preparing, L.T("Test hazırlanıyor (stok ayarlarda referans hesaplanıyor)…"), 0.01);
        var prepare = await _stress.PrepareAsync(ct);
        if (!prepare.Passed)
            return Fail(L.F("Kart stok ayarlarda bile hatalı sonuç verdi ({0}). Ayar yapmadan önce sürücüyü/kartı kontrol etmek gerekir.", prepare.Failure));

        TuneJournal journal;
        if (resume is null)
        {
            Report(TunePhase.BoostProbe, L.T("En yüksek boost hızı ölçülüyor…"), 0.03);
            var probe = await MeasureAsync(StressPattern.Transient, _options.BoostProbe, ct);
            if (!probe.Result.Passed) return Fail(L.F("Stok ayarlarda hata: {0}", probe.Result.Failure));
            if (probe.Overheated) return Fail(L.T("Kart stok ayarlarda bile çok ısınıyor. Önce soğutmayı kontrol et."));

            Report(TunePhase.Baseline, L.T("Stok performans ölçülüyor…"), 0.08);
            var baseline = await MeasureAsync(StressPattern.Heavy, _options.Baseline, ct);
            if (!baseline.Result.Passed) return Fail(L.F("Stok ayarlarda hata: {0}", baseline.Result.Failure));

            _steps?.Report(new TuneStep(L.T("Stok"), true, null, 1, baseline.AvgPowerW, baseline.MaxTemperatureC));
            var boost = Math.Max(probe.HighClockMHz, baseline.HighClockMHz);
            if (boost <= 0) return Fail(L.T("Çekirdek hızı okunamadı."));
            var maxClock = RoundDown((int)(goal == TuneGoal.Efficient ? boost * _options.EfficientClockRatio : boost));
            journal = new TuneJournal(maxClock, baseline.Result.Score, baseline.AvgPowerW, baseline.MaxTemperatureC, 0, null, DateTimeOffset.Now, goal);
            journal.Save(_dataDirectory);
        }
        else
        {
            journal = resume;
            if (resume.TestingOffsetMHz is { } crashed)
                Report(TunePhase.Searching, L.F("Önceki test +{0} MHz'de çökmüştü; bu değer kararsız sayıldı.", crashed), 0.1);
        }

        var lastStable = journal.LastStableOffsetMHz;
        var limit = Math.Min(_backend.Limits.CoreOffsetMaxMHz, _options.MaxSearchOffsetMHz);
        // On resume after a crash, the offset that was being tested is the ceiling.
        if (journal.TestingOffsetMHz is { } ceiling) limit = Math.Min(limit, ceiling - _options.StepMHz);

        for (var offset = lastStable + _options.StepMHz; offset <= limit; offset += _options.StepMHz)
        {
            ct.ThrowIfCancellationRequested();
            var fraction = 0.1 + 0.65 * offset / Math.Max(1, limit);
            Report(TunePhase.Searching, L.F("+{0} MHz deneniyor (son sağlam: +{1})…", offset, lastStable), fraction, offset);

            journal = journal with { LastStableOffsetMHz = lastStable, TestingOffsetMHz = offset };
            journal.Save(_dataDirectory);

            var step = await TryOffsetAsync(journal, offset, StressPattern.Mixed, _options.Step, ct);
            if (step is not null)
            {
                Report(TunePhase.Searching, L.F("+{0} MHz geçemedi: {1}", offset, step), fraction, offset);
                break;
            }
            lastStable = offset;
        }

        journal = journal with { LastStableOffsetMHz = lastStable, TestingOffsetMHz = null };
        journal.Save(_dataDirectory);

        var candidate = RoundDown(lastStable - _options.SafetyMarginMHz);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (candidate <= 0)
                return Fail(L.T("Bu kartta güvenli bir undervolt payı bulunamadı. Sessiz modu (güç limiti) kullanmanı öneririm."));

            Report(TunePhase.Validating, L.F("+{0} MHz {1:0} dakika doğrulanıyor…", candidate, _options.Validation.TotalMinutes), 0.8 + 0.06 * attempt, candidate);
            journal = journal with { TestingOffsetMHz = candidate };
            journal.Save(_dataDirectory);

            var settings = journal.Settings(candidate, _backend.Limits.PowerMaxW);
            _backend.Apply(settings);
            var validation = await MeasureAsync(StressPattern.Mixed, _options.Validation, ct);
            var failure = Judge(validation, journal);
            ReportStep(L.F("+{0} MHz (doğrulama)", candidate), failure, validation, journal.BaselineScore);
            if (failure is null)
            {
                TuneJournal.Delete(_dataDirectory);
                var message = journal.Goal == TuneGoal.Performance
                    ? L.F("Tatlı nokta: çekirdek +{0} MHz, güç limiti {1} W.", candidate, settings.PowerLimitW ?? _backend.Limits.PowerDefaultW)
                    : L.F("Tatlı nokta: max {0} MHz, çekirdek +{1} MHz.", journal.MaxClockMHz, candidate);
                Report(TunePhase.Done, message, 1, candidate);
                return new TuneResult(true, message, settings,
                    journal.BaselineScore, validation.Result.Score,
                    journal.BaselinePowerW, validation.AvgPowerW,
                    journal.BaselineTemperatureC, validation.MaxTemperatureC,
                    lastStable, lastStable + _options.StepMHz > limit);
            }

            SafeReset();
            Report(TunePhase.Validating, L.F("Doğrulama geçemedi ({0}); bir adım geri gidiliyor.", failure), 0.8 + 0.06 * attempt, candidate);
            candidate -= _options.StepMHz;
        }

        return Fail(L.T("Doğrulama üç denemede de geçemedi. Varsayılan ayarlarda kalındı."));
    }

    /// <summary>Returns null when the offset passed, otherwise the reason it failed.</summary>
    private async Task<string?> TryOffsetAsync(TuneJournal journal, int offset, StressPattern pattern, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            _backend.Apply(journal.Settings(offset, _backend.Limits.PowerMaxW));
            var measurement = await MeasureAsync(pattern, duration, ct);
            var failure = Judge(measurement, journal);
            ReportStep($"+{offset} MHz", failure, measurement, journal.BaselineScore);
            return failure;
        }
        finally
        {
            // Between steps the card always goes back to stock, so a failed step never lingers.
            SafeReset();
        }
    }

    private string? Judge(Measurement m, TuneJournal journal)
    {
        var baselineScore = journal.BaselineScore;
        // The efficient goal gives up a little speed on purpose (the lower clock cap); anything beyond that is a loss.
        var minRatio = journal.Goal == TuneGoal.Efficient ? _options.MinScoreRatio * _options.EfficientClockRatio : _options.MinScoreRatio;
        if (!m.Result.Passed) return m.Result.Failure ?? L.T("hata");
        if (m.Overheated) return L.F("sıcaklık {0:0}°C'yi geçti", _options.AbortTemperatureC);
        if (m.Result.Score < baselineScore * minRatio)
            return L.F("performans stoka göre %{0:0.0} düştü (clock stretching)", 100 * (1 - m.Result.Score / baselineScore));
        return null;
    }

    private async Task<Measurement> MeasureAsync(StressPattern pattern, TimeSpan duration, CancellationToken ct)
    {
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var samples = new List<GpuTelemetry>();
        var overheated = false;

        void Sample()
        {
            try
            {
                var s = _backend.ReadTelemetry();
                lock (samples) samples.Add(s);
                if (s.TemperatureC >= _options.AbortTemperatureC)
                {
                    overheated = true;
                    sampling.Cancel();
                }
            }
            catch (Exception) { /* a missed sample is fine */ }
        }

        var sampler = Task.Run(async () =>
        {
            while (!sampling.IsCancellationRequested)
            {
                try { await Task.Delay(_options.SampleInterval, sampling.Token); }
                catch (OperationCanceledException) { break; }
                Sample();
            }
        }, CancellationToken.None);

        StressResult result;
        try
        {
            result = await _stress.RunAsync(pattern, duration, sampling.Token);
        }
        catch (OperationCanceledException) when (overheated && !ct.IsCancellationRequested)
        {
            result = StressResult.Fail(L.T("aşırı sıcaklık"));
        }
        finally
        {
            // One sample at the end while the card is still warm, so even very short runs are measured.
            if (!overheated) Sample();
            sampling.Cancel();
            await sampler;
        }

        List<GpuTelemetry> copy;
        lock (samples) copy = [.. samples];
        var power = copy.Where(s => s.PowerW is not null).Select(s => s.PowerW!.Value).DefaultIfEmpty(0).Average();
        var temp = copy.Where(s => s.TemperatureC is not null).Select(s => s.TemperatureC!.Value).DefaultIfEmpty(0).Max();
        var clocks = copy.Where(s => s.CoreClockMHz is not null && s.LoadPercent is > 20).Select(s => (double)s.CoreClockMHz!.Value).Order().ToList();
        // 95th percentile: the clock the card sustains at its best, ignoring one-off spikes.
        var high = clocks.Count == 0 ? 0 : clocks[(int)Math.Floor(0.95 * (clocks.Count - 1))];
        return new Measurement(result, power, temp, high, overheated);
    }

    private void ReportStep(string label, string? failure, Measurement m, double baselineScore) =>
        _steps?.Report(new TuneStep(label, failure is null, failure,
            baselineScore > 0 ? m.Result.Score / baselineScore : 0, m.AvgPowerW, m.MaxTemperatureC));

    private static int RoundDown(int mhz) => mhz <= 0 ? 0 : mhz / 15 * 15;

    private TuneResult Fail(string message)
    {
        SafeReset();
        TuneJournal.Delete(_dataDirectory);
        Report(TunePhase.Failed, message, 1);
        return TuneResult.Failed(message);
    }

    private void SafeReset()
    {
        try { _backend.ResetToStock(); } catch (Exception) { }
    }

    private void Report(TunePhase phase, string message, double fraction, int? offset = null) =>
        _progress?.Report(new TuneProgress(phase, message, Math.Clamp(fraction, 0, 1), offset));
}
