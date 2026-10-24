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
/// Voltage can only be lowered (<see cref="VoltageOffsetMv"/> is never above 0): the tool never raises it above factory.
/// On AMD cards <see cref="CoreOffsetMHz"/> moves the max frequency relative to stock, and <see cref="PowerLimitW"/>
/// is a percentage of the stock limit (see <see cref="TuningLimits.PowerInPercent"/>).
/// </summary>
public sealed record TuningSettings(
    int CoreOffsetMHz = 0,
    int MemoryOffsetMHz = 0,
    int? PowerLimitW = null,
    int? MaxCoreClockMHz = null,
    int? FanPercent = null,
    /// <summary>AMD undervolt: millivolts below the stock voltage (0 or negative). NVIDIA undervolts through the clock cap instead.</summary>
    int VoltageOffsetMv = 0)
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
    bool SupportsFanControl,
    /// <summary>Lowest voltage offset allowed (negative). 0 when the card has no voltage control (NVIDIA).</summary>
    int VoltageOffsetMinMv = 0,
    /// <summary>AMD: the power limit fields are percent of stock (100 = stock) instead of watts.</summary>
    bool PowerInPercent = false,
    /// <summary>The max core clock can be locked (<see cref="TuningSettings.MaxCoreClockMHz"/>).</summary>
    bool SupportsClockCap = true)
{
    /// <summary>Undervolting works by lowering the voltage directly (AMD) instead of a clock cap plus core offset (NVIDIA).</summary>
    public bool UndervoltsByVoltage => VoltageOffsetMinMv < 0;

    public string PowerUnit => PowerInPercent ? "%" : "W";

    /// <summary>"250 W" or "%95".</summary>
    public string FormatPower(int value) => PowerInPercent ? $"%{value}" : $"{value} W";
}
