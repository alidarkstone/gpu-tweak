using GpuTweak.Core.Models;
using GpuTweak.Core.Localization;

namespace GpuTweak.Core.Backends;

/// <summary>
/// NVIDIA backend on the official NVML API. Writing settings requires the process to run as administrator.
/// Undervolting works by capping the max core clock (locked clocks) and raising the core offset,
/// so the card reaches the same frequency at a lower point of its voltage curve.
/// </summary>
public sealed class NvmlBackend : IGpuBackend
{
    private readonly IntPtr _device;
    private readonly uint _fanCount;
    private int? _lockedMaxClockMHz;
    private int? _manualFanPercent;
    private bool _disposed;

    private NvmlBackend(IntPtr device)
    {
        _device = device;
        Nvml.DeviceGetMemoryInfo(device, out var memory);
        Info = new GpuInfo(GpuVendor.Nvidia, Nvml.GetName(device), Nvml.GetDriverVersion(), (long)memory.Total);
        _fanCount = Nvml.DeviceGetNumFans(device, out var fans) == Nvml.Return.Success ? fans : 0;
        Limits = ReadLimits();
    }

    public GpuInfo Info { get; }
    public TuningLimits Limits { get; }

    /// <summary>Returns null when no NVIDIA driver/GPU is present.</summary>
    public static NvmlBackend? TryCreate(int index = 0)
    {
        try
        {
            if (Nvml.Init() != Nvml.Return.Success) return null;
            if (Nvml.DeviceGetCount(out var count) != Nvml.Return.Success || count <= index ||
                Nvml.DeviceGetHandleByIndex((uint)index, out var device) != Nvml.Return.Success)
            {
                Nvml.Shutdown();
                return null;
            }
            return new NvmlBackend(device);
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    private TuningLimits ReadLimits()
    {
        var hasCore = TryGetOffsetRange(Nvml.ClockType.Graphics, out var coreMin, out var coreMax);
        var hasMem = TryGetOffsetRange(Nvml.ClockType.Mem, out var memMin, out var memMax);
        Nvml.DeviceGetPowerManagementLimitConstraints(_device, out var powerMin, out var powerMax);
        Nvml.DeviceGetPowerManagementDefaultLimit(_device, out var powerDefault);
        Nvml.DeviceGetMaxClockInfo(_device, Nvml.ClockType.Graphics, out var maxClock);
        uint fanMin = 0, fanMax = 100;
        var hasFan = _fanCount > 0 && Nvml.DeviceGetMinMaxFanSpeed(_device, out fanMin, out fanMax) == Nvml.Return.Success;
        if (!hasFan) { fanMin = 0; fanMax = 100; }

        return new TuningLimits(
            CoreOffsetMinMHz: hasCore ? coreMin : 0,
            CoreOffsetMaxMHz: hasCore ? coreMax : 0,
            MemoryOffsetMinMHz: hasMem ? memMin : 0,
            MemoryOffsetMaxMHz: hasMem ? memMax : 0,
            PowerMinW: (int)(powerMin / 1000),
            PowerMaxW: (int)(powerMax / 1000),
            PowerDefaultW: (int)(powerDefault / 1000),
            CoreClockMinMHz: 210,
            CoreClockMaxMHz: maxClock > 0 ? (int)maxClock : 3000,
            FanMinPercent: (int)fanMin,
            FanMaxPercent: (int)fanMax,
            SupportsFanControl: hasFan);
    }

    public GpuTelemetry ReadTelemetry()
    {
        double? temp = Nvml.DeviceGetTemperature(_device, 0, out var t) == Nvml.Return.Success ? t : null;
        int? core = Nvml.DeviceGetClockInfo(_device, Nvml.ClockType.Graphics, out var c) == Nvml.Return.Success ? (int)c : null;
        int? mem = Nvml.DeviceGetClockInfo(_device, Nvml.ClockType.Mem, out var m) == Nvml.Return.Success ? (int)m : null;
        double? power = Nvml.DeviceGetPowerUsage(_device, out var p) == Nvml.Return.Success ? p / 1000.0 : null;
        double? limit = Nvml.DeviceGetPowerManagementLimit(_device, out var l) == Nvml.Return.Success ? l / 1000.0 : null;
        var hasMemory = Nvml.DeviceGetMemoryInfo(_device, out var memory) == Nvml.Return.Success;
        int? fan = Nvml.DeviceGetFanSpeed(_device, out var f) == Nvml.Return.Success ? (int)f : null;
        int? load = Nvml.DeviceGetUtilizationRates(_device, out var u) == Nvml.Return.Success ? (int)u.Gpu : null;

        return new GpuTelemetry(
            DateTimeOffset.Now, temp, core, mem, power, limit,
            hasMemory ? (long)memory.Used : null,
            hasMemory ? (long)memory.Total : null,
            fan, load, ReadMemoryTemperature());
    }

    private bool _memoryTemperatureUnsupported;
    private DateTime _memoryTemperatureRetryAt;
    /// <summary>Some cards answer "not supported" while idle and report the value under load, so ask again now and then.</summary>
    private static readonly TimeSpan MemoryTemperatureRetry = TimeSpan.FromSeconds(30);
    private double? _lastMemoryTemperature;
    private DateTime _lastMemoryTemperatureAt;
    /// <summary>The sensor sometimes gives no answer when the card idles; bridge short gaps with the last reading.</summary>
    private static readonly TimeSpan MemoryTemperatureHold = TimeSpan.FromSeconds(15);
    /// <summary>VRAM temperature, when the card reports it through NVML (GeForce cards usually answer "N/A").</summary>
    private double? ReadMemoryTemperature()
    {
        if (_memoryTemperatureUnsupported && DateTime.UtcNow < _memoryTemperatureRetryAt) return null;
        try
        {
            var values = new[] { new Nvml.FieldValue { FieldId = Nvml.FieldMemoryTemperature } };
            var result = Nvml.DeviceGetFieldValues(_device, 1, values);
            if (result == Nvml.Return.Success && values[0].Result == Nvml.Return.Success)
            {
                var celsius = (uint)values[0].Value;
                if (celsius is > 0 and < 150)
                {
                    _lastMemoryTemperature = celsius;
                    _lastMemoryTemperatureAt = DateTime.UtcNow;
                    return celsius;
                }
            }
            _memoryTemperatureUnsupported = result is Nvml.Return.NotSupported || values[0].Result is Nvml.Return.NotSupported;
            _memoryTemperatureRetryAt = DateTime.UtcNow + MemoryTemperatureRetry;
        }
        catch (EntryPointNotFoundException)
        {
            // Old driver without the call: never ask again.
            _memoryTemperatureUnsupported = true;
            _memoryTemperatureRetryAt = DateTime.MaxValue;
        }
        return DateTime.UtcNow - _lastMemoryTemperatureAt < MemoryTemperatureHold ? _lastMemoryTemperature : null;
    }

    public TuningSettings ReadCurrentSettings()
    {
        var core = GetOffset(Nvml.ClockType.Graphics);
        var mem = GetOffset(Nvml.ClockType.Mem);
        int? power = null;
        if (Nvml.DeviceGetPowerManagementLimit(_device, out var mw) == Nvml.Return.Success)
        {
            var watts = (int)Math.Round(mw / 1000.0);
            if (watts != Limits.PowerDefaultW) power = watts;
        }
        // NVML has no getter for locked clocks or manual fan state, so those come from what this process applied.
        return new TuningSettings(core, mem, power, _lockedMaxClockMHz, _manualFanPercent);
    }

    public void Apply(TuningSettings settings)
    {
        if (Limits.PowerMaxW > 0)
            Check(Nvml.DeviceSetPowerManagementLimit(_device, (uint)((settings.PowerLimitW ?? Limits.PowerDefaultW) * 1000)), L.T("güç limiti"));
        if (Limits.CoreOffsetMaxMHz > Limits.CoreOffsetMinMHz)
            Check(SetOffset(Nvml.ClockType.Graphics, settings.CoreOffsetMHz), L.T("çekirdek offset"));
        if (Limits.MemoryOffsetMaxMHz > Limits.MemoryOffsetMinMHz)
            Check(SetOffset(Nvml.ClockType.Mem, settings.MemoryOffsetMHz), L.T("bellek offset"));

        if (settings.MaxCoreClockMHz is { } maxClock)
            Check(Nvml.DeviceSetGpuLockedClocks(_device, (uint)Limits.CoreClockMinMHz, (uint)maxClock), L.T("max clock kilidi"));
        else if (_lockedMaxClockMHz is not null)
            Check(Nvml.DeviceResetGpuLockedClocks(_device), L.T("max clock kilidi sıfırlama"));
        _lockedMaxClockMHz = settings.MaxCoreClockMHz;

        if (Limits.SupportsFanControl)
        {
            for (uint fan = 0; fan < _fanCount; fan++)
            {
                if (settings.FanPercent is { } percent)
                    Check(Nvml.DeviceSetFanSpeed(_device, fan, (uint)percent), L.T("fan hızı"));
                else if (_manualFanPercent is not null)
                    Check(Nvml.DeviceSetDefaultFanSpeed(_device, fan), L.T("otomatik fan"));
            }
            _manualFanPercent = settings.FanPercent;
        }
    }

    public void ResetToStock()
    {
        // Best effort: try every reset even if one fails, then report the first failure.
        var errors = new List<string>();
        // "Not supported" means that control does not exist on this card, so there is nothing to reset.
        void Try(Nvml.Return result, string what) { if (result is not (Nvml.Return.Success or Nvml.Return.NotSupported)) errors.Add($"{what}: {Nvml.ErrorString(result)}"); }

        Try(SetOffset(Nvml.ClockType.Graphics, 0), L.T("çekirdek offset"));
        Try(SetOffset(Nvml.ClockType.Mem, 0), L.T("bellek offset"));
        if (Limits.PowerMaxW > 0) Try(Nvml.DeviceSetPowerManagementLimit(_device, (uint)(Limits.PowerDefaultW * 1000)), L.T("güç limiti"));
        Try(Nvml.DeviceResetGpuLockedClocks(_device), L.T("max clock kilidi"));
        for (uint fan = 0; fan < _fanCount; fan++) Try(Nvml.DeviceSetDefaultFanSpeed(_device, fan), L.T("otomatik fan"));
        _lockedMaxClockMHz = null;
        _manualFanPercent = null;

        if (errors.Count > 0) throw new GpuBackendException(L.T("Varsayılana dönülemedi: ") + string.Join("; ", errors));
    }

    // ---- Clock offsets: the v1 struct API (R555+, required on RTX 50) with the older per-clock calls as fallback ----

    private bool? _offsetsV1;

    private bool UseOffsetsV1()
    {
        if (_offsetsV1 is { } known) return known;
        try
        {
            var probe = Nvml.ClockOffset.For(Nvml.ClockType.Graphics);
            _offsetsV1 = Nvml.DeviceGetClockOffsets(_device, ref probe) == Nvml.Return.Success;
        }
        catch (EntryPointNotFoundException)
        {
            _offsetsV1 = false;
        }
        return _offsetsV1.Value;
    }

    private bool TryGetOffsetRange(Nvml.ClockType type, out int min, out int max)
    {
        min = max = 0;
        if (UseOffsetsV1())
        {
            var info = Nvml.ClockOffset.For(type);
            if (Nvml.DeviceGetClockOffsets(_device, ref info) != Nvml.Return.Success) return false;
            (min, max) = (info.MinOffsetMHz, info.MaxOffsetMHz);
            return max > min;
        }
        try
        {
            var result = type == Nvml.ClockType.Graphics
                ? Nvml.DeviceGetGpcClkMinMaxVfOffset(_device, out min, out max)
                : Nvml.DeviceGetMemClkMinMaxVfOffset(_device, out min, out max);
            return result == Nvml.Return.Success && max > min;
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    private int GetOffset(Nvml.ClockType type)
    {
        if (UseOffsetsV1())
        {
            var info = Nvml.ClockOffset.For(type);
            return Nvml.DeviceGetClockOffsets(_device, ref info) == Nvml.Return.Success ? info.OffsetMHz : 0;
        }
        try
        {
            int value;
            var result = type == Nvml.ClockType.Graphics
                ? Nvml.DeviceGetGpcClkVfOffset(_device, out value)
                : Nvml.DeviceGetMemClkVfOffset(_device, out value);
            return result == Nvml.Return.Success ? value : 0;
        }
        catch (EntryPointNotFoundException) { return 0; }
    }

    private Nvml.Return SetOffset(Nvml.ClockType type, int offsetMHz)
    {
        if (UseOffsetsV1())
        {
            var info = Nvml.ClockOffset.For(type);
            info.OffsetMHz = offsetMHz;
            return Nvml.DeviceSetClockOffsets(_device, ref info);
        }
        try
        {
            return type == Nvml.ClockType.Graphics
                ? Nvml.DeviceSetGpcClkVfOffset(_device, offsetMHz)
                : Nvml.DeviceSetMemClkVfOffset(_device, offsetMHz);
        }
        catch (EntryPointNotFoundException) { return Nvml.Return.NotSupported; }
    }

    private static void Check(Nvml.Return result, string what)
    {
        if (result == Nvml.Return.Success) return;
        var hint = result == Nvml.Return.NoPermission ? L.T(" (uygulamayı yönetici olarak çalıştırın)") : "";
        throw new GpuBackendException(L.F("{0} ayarlanamadı: {1}{2}", what, Nvml.ErrorString(result), hint));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Nvml.Shutdown();
    }
}
