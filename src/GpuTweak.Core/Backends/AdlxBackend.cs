using GpuTweak.Core.Localization;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Backends;

/// <summary>
/// AMD backend on the official ADLX API (the same settings as Adrenalin's manual tuning page).
/// Mapping onto <see cref="TuningSettings"/>:
/// - core offset: max frequency relative to stock (RDNA4 takes it as an offset itself; RDNA2/3 take an absolute clock),
/// - voltage offset: millivolts below stock, never above (RDNA4 offset; RDNA2/3 absolute voltage),
/// - memory offset: max VRAM clock relative to stock,
/// - power limit: percent of stock (100 = stock), because the driver only knows percentages,
/// - fan: a flat fan curve at the chosen speed; back to automatic by resetting tuning to factory.
/// Writing settings requires the process to run as administrator, like on NVIDIA.
/// </summary>
public sealed class AdlxBackend : IGpuBackend
{
    /// <summary>Deeper undervolts than this are not offered; real chips crash in games long before.</summary>
    public const int MaxUndervoltMv = 200;

    private readonly object _sync = new();
    private readonly IntPtr _gpu;
    private IntPtr _tuning, _monitoring, _gfx, _vram, _power, _fan;
    private readonly bool _offsetMode;
    private int _stockMaxClockMHz, _stockVoltageMv, _stockVramMHz;
    private readonly bool _hasGfxDefaults, _hasVramDefaults;
    private readonly int _fanMaxRpm;
    private TuningSettings? _applied;
    private const int ResetNeeded = 18;
    private bool _disposed;

    private AdlxBackend(IntPtr gpu, IntPtr tuning, IntPtr monitoring)
    {
        _gpu = gpu;
        _tuning = tuning;
        _monitoring = monitoring;

        var name = Adlx.TryString(gpu, Adlx.Gpu.Name) ?? "AMD Radeon";
        if (!name.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase)) name = "AMD " + name;
        var gpu2 = Adlx.QueryInterface(gpu, "IADLXGPU2");
        var driver = Adlx.TryString(gpu2, 31) ?? "?";  // IADLXGPU2::AMDSoftwareVersion, e.g. "25.3.1"
        Adlx.Release(gpu2);
        var vramMb = Adlx.TryInt(gpu, Adlx.Gpu.TotalVRAM) ?? 0;
        Info = new GpuInfo(GpuVendor.Amd, name, driver, (long)vramMb * 1024 * 1024);

        if (Adlx.GpuIsSupported(tuning, Adlx.TuningServices.IsSupportedManualGFXTuning, _gpu))
            _gfx = GetFeature(Adlx.TuningServices.GetManualGFXTuning, Adlx.GfxTuning2.Iid2_1, Adlx.GfxTuning2.Iid, out _hasGfxDefaults);
        if (Adlx.GpuIsSupported(tuning, Adlx.TuningServices.IsSupportedManualVRAMTuning, _gpu))
            _vram = GetFeature(Adlx.TuningServices.GetManualVRAMTuning, Adlx.VramTuning2.Iid2_1, Adlx.VramTuning2.Iid, out _hasVramDefaults);
        if (Adlx.GpuIsSupported(tuning, Adlx.TuningServices.IsSupportedManualPowerTuning, _gpu))
            _power = GetFeature(Adlx.TuningServices.GetManualPowerTuning, Adlx.PowerTuning.Iid, null, out _);
        if (Adlx.GpuIsSupported(tuning, Adlx.TuningServices.IsSupportedManualFanTuning, _gpu))
            _fan = GetFeature(Adlx.TuningServices.GetManualFanTuning, Adlx.FanTuning.Iid, null, out _);

        // From RDNA4 on the driver reports max frequency and voltage as offsets (ranges around 0).
        var freqRange = Adlx.TryRange(_gfx, Adlx.GfxTuning2.GetMaxFrequencyRange);
        var voltRange = Adlx.TryRange(_gfx, Adlx.GfxTuning2.GetVoltageRange);
        _offsetMode = freqRange is { Min: < 0 } || voltRange is { Max: <= 0 };
        ReadStockValues(atFactory: IsAtFactory());

        if (monitoring != IntPtr.Zero && Adlx.Succeeded(Adlx.GpuGetPtr(monitoring, Adlx.Monitoring.GetSupportedGPUMetrics, _gpu, out var support)) && support != IntPtr.Zero)
        {
            if (Adlx.TryMinMax(support, Adlx.MetricsSupport.GetGPUFanSpeedRange, out _, out var maxRpm) && maxRpm > 0) _fanMaxRpm = maxRpm;
            Adlx.Release(support);
        }

        Limits = ReadLimits(freqRange, voltRange);
    }

    public GpuInfo Info { get; }
    public TuningLimits Limits { get; }

    /// <summary>Returns null when there is no AMD driver or no discrete Radeon card.</summary>
    public static AdlxBackend? TryCreate()
    {
        IntPtr system;
        try
        {
            system = Adlx.Initialize();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
        if (system == IntPtr.Zero) return null;

        IntPtr list = IntPtr.Zero, gpu = IntPtr.Zero, tuning = IntPtr.Zero, monitoring = IntPtr.Zero;
        try
        {
            if (!Adlx.Succeeded(Adlx.GetPtr(system, Adlx.System.GetGPUs, out list)) || list == IntPtr.Zero) return Fail();
            // A Ryzen CPU can have its own small Radeon GPU; take the discrete card with the most memory.
            long bestVram = -1;
            for (uint i = 0, n = Adlx.ListSize(list); i < n; i++)
            {
                var candidate = Adlx.ListAt(list, i);
                if (candidate == IntPtr.Zero) continue;
                var discrete = Adlx.TryInt(candidate, Adlx.Gpu.Type) == Adlx.Gpu.TypeDiscrete;
                var vram = Adlx.TryInt(candidate, Adlx.Gpu.TotalVRAM) ?? 0;
                if (discrete && vram > bestVram)
                {
                    Adlx.Release(gpu);
                    gpu = candidate;
                    bestVram = vram;
                }
                else Adlx.Release(candidate);
            }
            Adlx.Release(ref list);
            if (gpu == IntPtr.Zero) return Fail();
            if (!Adlx.Succeeded(Adlx.GetPtr(system, Adlx.System.GetGPUTuningServices, out tuning)) || tuning == IntPtr.Zero) return Fail();
            Adlx.GetPtr(system, Adlx.System.GetPerformanceMonitoringServices, out monitoring);
            return new AdlxBackend(gpu, tuning, monitoring);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return Fail();
        }

        AdlxBackend? Fail()
        {
            Adlx.Release(list);
            Adlx.Release(gpu);
            Adlx.Release(tuning);
            Adlx.Release(monitoring);
            Adlx.Terminate();
            return null;
        }
    }

    /// <summary>Gets a tuning interface and asks for the newest version first; <paramref name="newer"/> tells whether that worked.</summary>
    private IntPtr GetFeature(int getter, string iid, string? fallbackIid, out bool newer)
    {
        newer = false;
        if (!Adlx.Succeeded(Adlx.GpuGetPtr(_tuning, getter, _gpu, out var feature)) || feature == IntPtr.Zero) return IntPtr.Zero;
        var result = Adlx.QueryInterface(feature, iid);
        if (result != IntPtr.Zero) newer = fallbackIid is not null;
        else if (fallbackIid is not null) result = Adlx.QueryInterface(feature, fallbackIid);
        Adlx.Release(feature);
        return result;
    }

    private bool IsAtFactory() => Adlx.GpuIsSupported(_tuning, Adlx.TuningServices.IsAtFactory, _gpu);

    /// <summary>
    /// The stock clocks and voltage everything is relative to. Newer drivers report them; with older ones the
    /// current values are only taken as stock when the card is at factory settings.
    /// </summary>
    private void ReadStockValues(bool atFactory)
    {
        if (_offsetMode)
        {
            _stockMaxClockMHz = 0;
            _stockVoltageMv = 0;
        }
        else
        {
            var freqRange = Adlx.TryRange(_gfx, Adlx.GfxTuning2.GetMaxFrequencyRange);
            var voltRange = Adlx.TryRange(_gfx, Adlx.GfxTuning2.GetVoltageRange);
            _stockMaxClockMHz = (_hasGfxDefaults ? Adlx.TryInt(_gfx, Adlx.GfxTuning2.GetMaxFrequencyDefault) : null)
                ?? (atFactory ? Adlx.TryInt(_gfx, Adlx.GfxTuning2.GetMaxFrequency) : null)
                ?? (_stockMaxClockMHz > 0 ? _stockMaxClockMHz : Adlx.TryInt(_gfx, Adlx.GfxTuning2.GetMaxFrequency) ?? freqRange?.Max ?? 0);
            // RDNA2/3 ship at the top of the voltage range.
            _stockVoltageMv = (_hasGfxDefaults ? Adlx.TryInt(_gfx, Adlx.GfxTuning2.GetVoltageDefault) : null) ?? voltRange?.Max ?? 0;
        }
        _stockVramMHz = (_hasVramDefaults ? Adlx.TryInt(_vram, Adlx.VramTuning2.GetMaxFrequencyDefault) : null)
            ?? (atFactory ? Adlx.TryInt(_vram, Adlx.VramTuning2.GetMaxFrequency) : null)
            ?? (_stockVramMHz > 0 ? _stockVramMHz : Adlx.TryInt(_vram, Adlx.VramTuning2.GetMaxFrequency) ?? 0);
    }

    private TuningLimits ReadLimits(Adlx.IntRange? freqRange, Adlx.IntRange? voltRange)
    {
        int coreMin = 0, coreMax = 0, voltMin = 0;
        if (freqRange is { } f && f.Max > f.Min)
        {
            coreMin = f.Min - _stockMaxClockMHz;
            coreMax = f.Max - _stockMaxClockMHz;
        }
        if (voltRange is { } v && v.Max > v.Min)
            voltMin = Math.Max(-MaxUndervoltMv, Math.Min(0, v.Min - _stockVoltageMv));

        int memMin = 0, memMax = 0;
        if (Adlx.TryRange(_vram, Adlx.VramTuning2.GetMaxFrequencyRange) is { } m && m.Max > m.Min && _stockVramMHz > 0)
        {
            memMin = Math.Max(-200, Math.Min(0, m.Min - _stockVramMHz));
            memMax = Math.Max(0, m.Max - _stockVramMHz);
        }

        int powerMin = 100, powerMax = 100;
        if (Adlx.TryRange(_power, Adlx.PowerTuning.GetPowerLimitRange) is { } p && p.Max > p.Min)
        {
            powerMin = 100 + p.Min;
            powerMax = 100 + p.Max;
        }

        int fanMin = 0, fanMax = 100;
        var hasFan = Adlx.TryRanges(_fan, Adlx.FanTuning.GetRanges, out var speed, out _) && speed.Max > speed.Min;
        if (hasFan)
        {
            // A fixed speed below ~30% can let the card heat up under load; keep a sane floor.
            fanMin = Math.Max(30, speed.Min);
            fanMax = Math.Min(100, speed.Max);
            hasFan = fanMax > fanMin;
        }

        return new TuningLimits(
            CoreOffsetMinMHz: coreMin,
            CoreOffsetMaxMHz: coreMax,
            MemoryOffsetMinMHz: memMin,
            MemoryOffsetMaxMHz: memMax,
            PowerMinW: powerMin,
            PowerMaxW: powerMax,
            PowerDefaultW: 100,
            CoreClockMinMHz: 0,
            CoreClockMaxMHz: 0,
            FanMinPercent: hasFan ? fanMin : 0,
            FanMaxPercent: hasFan ? fanMax : 100,
            SupportsFanControl: hasFan,
            VoltageOffsetMinMv: voltMin,
            PowerInPercent: true,
            SupportsClockCap: false);
    }

    public GpuTelemetry ReadTelemetry()
    {
        lock (_sync)
        {
            if (_disposed || _monitoring == IntPtr.Zero || !Adlx.Succeeded(Adlx.GpuGetPtr(_monitoring, Adlx.Monitoring.GetCurrentGPUMetrics, _gpu, out var m)) || m == IntPtr.Zero)
                return new GpuTelemetry(DateTimeOffset.Now, null, null, null, null, null, null, Info.VramBytes, null, null);
            try
            {
                var temp = Valid(Adlx.TryDouble(m, Adlx.Metrics.Temperature), 150);
                int? core = Adlx.TryInt(m, Adlx.Metrics.ClockSpeed) is > 0 and var c ? c : null;
                int? mem = Adlx.TryInt(m, Adlx.Metrics.VramClockSpeed) is > 0 and var mc ? mc : null;
                // Total board power is what the power limit acts on; RDNA2 only reports the chip power.
                var power = Valid(Adlx.TryDouble(m, Adlx.Metrics.TotalBoardPower), 1000) ?? Valid(Adlx.TryDouble(m, Adlx.Metrics.Power), 1000);
                var usage = Adlx.TryDouble(m, Adlx.Metrics.Usage) is { } u and >= 0 ? (int?)Math.Round(Math.Min(100, u)) : null;
                long? vramUsed = Adlx.TryInt(m, Adlx.Metrics.Vram) is >= 0 and var vu ? vu * 1024L * 1024 : null;
                int? fan = _fanMaxRpm > 0 && Adlx.TryInt(m, Adlx.Metrics.FanSpeed) is >= 0 and var rpm ? (int)Math.Clamp(Math.Round(100.0 * rpm / _fanMaxRpm), 0, 100) : null;

                double? memTemp = null;
                var m1 = Adlx.QueryInterface(m, Adlx.Metrics.Iid1);
                if (m1 != IntPtr.Zero)
                {
                    memTemp = Valid(Adlx.TryDouble(m1, Adlx.Metrics.MemoryTemperature), 150);
                    Adlx.Release(m1);
                }

                return new GpuTelemetry(DateTimeOffset.Now, temp is { } t ? Math.Round(t, 1) : null, core, mem,
                    power is { } pw ? Math.Round(pw, 1) : null, PowerLimitEstimateW(),
                    vramUsed, Info.VramBytes, fan, usage, memTemp);
            }
            finally
            {
                Adlx.Release(m);
            }
        }

        static double? Valid(double? value, double max) => value is > 0 and var v && v < max ? v : null;
    }

    /// <summary>
    /// The driver only knows the limit as a percentage. For the live power bar, the reference board power of common
    /// cards gives a usable watt figure; factory-overclocked models can draw a bit more.
    /// </summary>
    private double? PowerLimitEstimateW()
    {
        var name = Info.Name.ToUpperInvariant();
        (string Model, int Watts)[] reference =
        [
            ("9070 XT", 304), ("9070 GRE", 220), ("9070", 220), ("9060 XT", 160),
            ("7900 XTX", 355), ("7900 XT", 315), ("7900 GRE", 260), ("7800 XT", 263), ("7700 XT", 245), ("7600 XT", 190), ("7600", 165),
            ("6950 XT", 335), ("6900 XT", 300), ("6800 XT", 300), ("6800", 250), ("6750 XT", 250), ("6700 XT", 230), ("6700", 175),
            ("6650 XT", 180), ("6600 XT", 160), ("6600", 132),
        ];
        foreach (var (model, watts) in reference)
        {
            if (!name.Contains(" " + model, StringComparison.Ordinal)) continue;
            var percent = (_applied?.PowerLimitW ?? 100) / 100.0;
            return Math.Round(watts * percent);
        }
        return null;
    }

    public TuningSettings ReadCurrentSettings()
    {
        lock (_sync)
        {
            if (_applied is { } applied) return applied;
            // Fresh start: read what the driver has (it keeps Adrenalin's manual settings across reboots).
            var core = Adlx.TryInt(_gfx, Adlx.GfxTuning2.GetMaxFrequency) is { } f && Limits.CoreOffsetMaxMHz > Limits.CoreOffsetMinMHz ? f - _stockMaxClockMHz : 0;
            var volt = Adlx.TryInt(_gfx, Adlx.GfxTuning2.GetVoltage) is { } v && Limits.UndervoltsByVoltage ? v - _stockVoltageMv : 0;
            var mem = Adlx.TryInt(_vram, Adlx.VramTuning2.GetMaxFrequency) is { } mf && _stockVramMHz > 0 ? mf - _stockVramMHz : 0;
            int? power = Adlx.TryInt(_power, Adlx.PowerTuning.GetPowerLimit) is { } p && p != 0 ? 100 + p : null;
            return new TuningSettings(
                Math.Clamp(core, Limits.CoreOffsetMinMHz, Math.Max(Limits.CoreOffsetMinMHz, Limits.CoreOffsetMaxMHz)),
                Math.Clamp(mem, Limits.MemoryOffsetMinMHz, Math.Max(Limits.MemoryOffsetMinMHz, Limits.MemoryOffsetMaxMHz)),
                power,
                VoltageOffsetMv: Math.Clamp(Math.Min(0, volt), Limits.VoltageOffsetMinMv, 0));
        }
    }

    public void Apply(TuningSettings settings)
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            try
            {
                ApplyCore(settings);
            }
            catch (ResetNeededException)
            {
                // The driver wants its tuning reset once (for example after a driver timeout); then write everything again.
                ResetCore();
                ApplyCore(settings);
            }
        }
    }

    private sealed class ResetNeededException : Exception;

    private void ApplyCore(TuningSettings settings)
    {
        {
            // Going from a fixed fan back to automatic needs a factory reset (the driver has no "auto" switch for
            // a custom curve); everything else is written again right after.
            if (settings.FanPercent is null && _applied?.FanPercent is not null)
            {
                ResetCore();
            }

            if (_power != IntPtr.Zero && Limits.PowerMaxW > Limits.PowerMinW)
                SetIfChanged(_power, Adlx.PowerTuning.GetPowerLimitRange, Adlx.PowerTuning.GetPowerLimit, Adlx.PowerTuning.SetPowerLimit, (settings.PowerLimitW ?? 100) - 100, L.T("güç limiti"));
            if (_gfx != IntPtr.Zero && Limits.CoreOffsetMaxMHz > Limits.CoreOffsetMinMHz)
                SetIfChanged(_gfx, Adlx.GfxTuning2.GetMaxFrequencyRange, Adlx.GfxTuning2.GetMaxFrequency, Adlx.GfxTuning2.SetMaxFrequency, _stockMaxClockMHz + settings.CoreOffsetMHz, L.T("çekirdek offset"));
            if (_gfx != IntPtr.Zero && Limits.UndervoltsByVoltage)
                SetIfChanged(_gfx, Adlx.GfxTuning2.GetVoltageRange, Adlx.GfxTuning2.GetVoltage, Adlx.GfxTuning2.SetVoltage, _stockVoltageMv + Math.Min(0, settings.VoltageOffsetMv), L.T("voltaj offset"));
            if (_vram != IntPtr.Zero && Limits.MemoryOffsetMaxMHz > Limits.MemoryOffsetMinMHz)
                SetIfChanged(_vram, Adlx.VramTuning2.GetMaxFrequencyRange, Adlx.VramTuning2.GetMaxFrequency, Adlx.VramTuning2.SetMaxFrequency, _stockVramMHz + settings.MemoryOffsetMHz, L.T("bellek offset"));
            if (settings.FanPercent is { } percent && Limits.SupportsFanControl)
                SetFixedFan(percent);

            _applied = settings with { MaxCoreClockMHz = null, FanPercent = Limits.SupportsFanControl ? settings.FanPercent : null };
        }
    }

    public void ResetToStock()
    {
        lock (_sync)
        {
            ThrowIfDisposed();
            // The auto-tune resets between every step; skip the driver call when nothing is changed anyway.
            if (_applied is null && IsAtFactory()) return;
            ResetCore();
            _applied = null;
        }
    }

    private void ResetCore()
    {
        var result = Adlx.GpuCall(_tuning, Adlx.TuningServices.ResetToFactory, _gpu);
        if (!Adlx.Succeeded(result) && result != Adlx.NotSupported)
            throw new GpuBackendException(L.T("Varsayılana dönülemedi: ") + Adlx.ErrorString(result));
        // Without the driver's default getters the stock values are known for sure only now.
        if (!_hasGfxDefaults || !_hasVramDefaults) ReadStockValues(atFactory: true);
        _applied = null;
    }

    private static void SetIfChanged(IntPtr feature, int rangeGetter, int getter, int setter, int value, string what)
    {
        // Stay on the driver's grid and inside its range, or it refuses the value. Rounding goes down: lower is the safe side
        // for every one of these (clock, voltage, power, memory).
        if (Adlx.TryRange(feature, rangeGetter) is { } range && range.Max > range.Min)
        {
            var step = Math.Max(1, range.Step);
            value = Math.Clamp(range.Min + (int)Math.Floor((value - range.Min) / (double)step) * step, range.Min, range.Max);
        }
        if (Adlx.TryInt(feature, getter) == value) return;
        var result = Adlx.SetInt(feature, setter, value);
        if (result == ResetNeeded) throw new ResetNeededException();
        if (!Adlx.Succeeded(result))
            throw new GpuBackendException(L.F("{0} ayarlanamadı: {1}{2}", what, Adlx.ErrorString(result), ""));
    }

    /// <summary>A flat fan curve: the same speed at every temperature point, with zero-RPM mode off.</summary>
    private void SetFixedFan(int percent)
    {
        if (!Adlx.TryRanges(_fan, Adlx.FanTuning.GetRanges, out var speed, out var temperature))
            throw new GpuBackendException(L.F("{0} ayarlanamadı: {1}{2}", L.T("fan hızı"), "ranges", ""));
        var target = Math.Clamp(percent, speed.Min, speed.Max);
        if (Adlx.TryBool(_fan, Adlx.FanTuning.IsSupportedZeroRPM) == true) Adlx.SetBool(_fan, Adlx.FanTuning.SetZeroRPMState, false);

        if (!Adlx.Succeeded(Adlx.GetPtr(_fan, Adlx.FanTuning.GetEmptyStates, out var states)) || states == IntPtr.Zero)
            throw new GpuBackendException(L.F("{0} ayarlanamadı: {1}{2}", L.T("fan hızı"), "states", ""));
        try
        {
            var count = Adlx.ListSize(states);
            var span = Math.Max(1, temperature.Max - temperature.Min);
            for (uint i = 0; i < count; i++)
            {
                var state = Adlx.ListAt(states, i);
                if (state == IntPtr.Zero) continue;
                Adlx.SetInt(state, Adlx.FanTuning.StateSetTemperature, temperature.Min + (int)(span * (i + 1) / (count + 1.0)));
                // Some drivers want the curve to rise; one percent per point is invisible but keeps it valid.
                Adlx.SetInt(state, Adlx.FanTuning.StateSetFanSpeed, Math.Min(speed.Max, target + (int)i));
                Adlx.Release(state);
            }
            var valid = Adlx.CallWithPtrOutInt(_fan, Adlx.FanTuning.IsValidStates, states, out _);
            var result = Adlx.Succeeded(valid) ? Adlx.CallWithPtr(_fan, Adlx.FanTuning.SetStates, states) : valid;
            if (!Adlx.Succeeded(result))
                throw new GpuBackendException(L.F("{0} ayarlanamadı: {1}{2}", L.T("fan hızı"), Adlx.ErrorString(result), ""));
        }
        finally
        {
            Adlx.Release(states);
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new GpuBackendException(L.T("Ekran kartı bağlantısı kapandı."));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Adlx.Release(ref _gfx);
            Adlx.Release(ref _vram);
            Adlx.Release(ref _power);
            Adlx.Release(ref _fan);
            Adlx.Release(ref _monitoring);
            Adlx.Release(ref _tuning);
            Adlx.Release(_gpu);
            Adlx.Terminate();
        }
    }
}
