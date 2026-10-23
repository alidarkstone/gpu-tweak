using GpuTweak.Core.Models;

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
        var hasCore = Nvml.DeviceGetGpcClkMinMaxVfOffset(_device, out var coreMin, out var coreMax) == Nvml.Return.Success;
        var hasMem = Nvml.DeviceGetMemClkMinMaxVfOffset(_device, out var memMin, out var memMax) == Nvml.Return.Success;
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
            fan, load);
    }

    public TuningSettings ReadCurrentSettings()
    {
        Nvml.DeviceGetGpcClkVfOffset(_device, out var core);
        Nvml.DeviceGetMemClkVfOffset(_device, out var mem);
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
            Check(Nvml.DeviceSetPowerManagementLimit(_device, (uint)((settings.PowerLimitW ?? Limits.PowerDefaultW) * 1000)), "güç limiti");
        Check(Nvml.DeviceSetGpcClkVfOffset(_device, settings.CoreOffsetMHz), "çekirdek offset");
        Check(Nvml.DeviceSetMemClkVfOffset(_device, settings.MemoryOffsetMHz), "bellek offset");

        if (settings.MaxCoreClockMHz is { } maxClock)
            Check(Nvml.DeviceSetGpuLockedClocks(_device, (uint)Limits.CoreClockMinMHz, (uint)maxClock), "max clock kilidi");
        else if (_lockedMaxClockMHz is not null)
            Check(Nvml.DeviceResetGpuLockedClocks(_device), "max clock kilidi sıfırlama");
        _lockedMaxClockMHz = settings.MaxCoreClockMHz;

        if (Limits.SupportsFanControl)
        {
            for (uint fan = 0; fan < _fanCount; fan++)
            {
                if (settings.FanPercent is { } percent)
                    Check(Nvml.DeviceSetFanSpeed(_device, fan, (uint)percent), "fan hızı");
                else if (_manualFanPercent is not null)
                    Check(Nvml.DeviceSetDefaultFanSpeed(_device, fan), "otomatik fan");
            }
            _manualFanPercent = settings.FanPercent;
        }
    }

    public void ResetToStock()
    {
        // Best effort: try every reset even if one fails, then report the first failure.
        var errors = new List<string>();
        void Try(Nvml.Return result, string what) { if (result != Nvml.Return.Success) errors.Add($"{what}: {Nvml.ErrorString(result)}"); }

        Try(Nvml.DeviceSetGpcClkVfOffset(_device, 0), "çekirdek offset");
        Try(Nvml.DeviceSetMemClkVfOffset(_device, 0), "bellek offset");
        if (Limits.PowerMaxW > 0) Try(Nvml.DeviceSetPowerManagementLimit(_device, (uint)(Limits.PowerDefaultW * 1000)), "güç limiti");
        Try(Nvml.DeviceResetGpuLockedClocks(_device), "max clock kilidi");
        for (uint fan = 0; fan < _fanCount; fan++) Try(Nvml.DeviceSetDefaultFanSpeed(_device, fan), "otomatik fan");
        _lockedMaxClockMHz = null;
        _manualFanPercent = null;

        if (errors.Count > 0) throw new GpuBackendException("Varsayılana dönülemedi: " + string.Join("; ", errors));
    }

    private static void Check(Nvml.Return result, string what)
    {
        if (result == Nvml.Return.Success) return;
        var hint = result == Nvml.Return.NoPermission ? " (uygulamayı yönetici olarak çalıştırın)" : "";
        throw new GpuBackendException($"{what} ayarlanamadı: {Nvml.ErrorString(result)}{hint}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Nvml.Shutdown();
    }
}
