namespace GpuTweak.Core.Models;

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

/// <summary>Static facts about the card, read once at startup.</summary>
public sealed record GpuInfo(GpuVendor Vendor, string Name, string DriverVersion, long VramBytes)
{
    /// <summary>"NVIDIA GeForce RTX 5080" -> "GeForce RTX 5080"; used in the overlay and for profile matching.</summary>
    public string ShortName =>
        Name.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase) ? Name[7..] :
        Name.StartsWith("AMD ", StringComparison.OrdinalIgnoreCase) ? Name[4..] : Name;
}

/// <summary>One sample of live sensor data. Null means the sensor is not available on this card.</summary>
public sealed record GpuTelemetry(
    DateTimeOffset Timestamp,
    double? TemperatureC,
    int? CoreClockMHz,
    int? MemoryClockMHz,
    double? PowerW,
    double? PowerLimitW,
    long? VramUsedBytes,
    long? VramTotalBytes,
    int? FanPercent,
    int? LoadPercent,
    /// <summary>Memory (VRAM) temperature; most GeForce cards do not report it through NVML.</summary>
    double? MemoryTemperatureC = null);

/// <summary>
/// Everything the user can change. Null means "driver default" for that field.
/// Voltage is deliberately absent: the tool never raises voltage above factory.
/// </summary>
public sealed record TuningSettings(
    int CoreOffsetMHz = 0,
    int MemoryOffsetMHz = 0,
    int? PowerLimitW = null,
    int? MaxCoreClockMHz = null,
    int? FanPercent = null)
{
    public static TuningSettings Stock { get; } = new();

    public bool IsStock => this == Stock;
}

/// <summary>Hardware/vBIOS limits reported by the driver. Every setting is clamped into these.</summary>
public sealed record TuningLimits(
    int CoreOffsetMinMHz,
    int CoreOffsetMaxMHz,
    int MemoryOffsetMinMHz,
    int MemoryOffsetMaxMHz,
    int PowerMinW,
    int PowerMaxW,
    int PowerDefaultW,
    int CoreClockMinMHz,
    int CoreClockMaxMHz,
    int FanMinPercent,
    int FanMaxPercent,
    bool SupportsFanControl);
