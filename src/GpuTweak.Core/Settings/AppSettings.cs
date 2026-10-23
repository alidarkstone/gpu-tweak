using System.Text.Json;

namespace GpuTweak.Core.Settings;

public enum OverlayCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public sealed record OverlaySettings
{
    public bool Enabled { get; init; } = true;
    public OverlayCorner Corner { get; init; } = OverlayCorner.TopLeft;
    /// <summary>0.2 … 1.0, background opacity of the widget.</summary>
    public double Opacity { get; init; } = 0.75;
    public double Scale { get; init; } = 1.0;
    public bool ShowLogo { get; init; } = true;
    public bool ShowModel { get; init; } = true;
    public bool ShowFps { get; init; } = true;
    public bool ShowTemperature { get; init; } = true;
    public bool ShowCoreClock { get; init; } = true;
    public bool ShowMemoryClock { get; init; } = false;
    public bool ShowPower { get; init; } = true;
    public bool ShowVram { get; init; } = true;
    public bool ShowFan { get; init; } = false;
    public bool ShowLoad { get; init; } = true;
}

/// <summary>What the last successful auto-tune measured about this particular chip.</summary>
public sealed record SiliconResult(
    string GpuName,
    /// <summary>Highest core offset that passed the self-checking test at the stock boost clock.</summary>
    int HighestPassedOffsetMHz,
    /// <summary>The search stopped at its own limit, so the chip may take even more.</summary>
    bool ReachedSearchLimit,
    /// <summary>The offset that was applied (highest passed minus a safety margin).</summary>
    int AppliedOffsetMHz,
    DateTimeOffset TestedAt);

public sealed record AppSettings
{
    public SiliconResult? Silicon { get; init; }
    public AutoTune.TuneGoal TuneGoal { get; init; } = AutoTune.TuneGoal.Cool;
    /// <summary>"tr" or "en"; null follows the Windows display language.</summary>
    public string? Language { get; init; }
    public OverlaySettings Overlay { get; init; } = new();
    public double ThermalLimitC { get; init; } = 88;
    public bool ShowLivePanel { get; init; } = true;
    /// <summary>Step settings down automatically when a driver reset / crash is seen while gaming.</summary>
    public bool AutoFixStability { get; init; } = true;
    public int PollIntervalMs { get; init; } = 1000;
}

/// <summary>settings.json in the portable data folder.</summary>
public sealed class SettingsStore(string dataDirectory)
{
    private readonly string _path = Path.Combine(dataDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonDefaults.Options) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(_path, JsonSerializer.Serialize(settings, JsonDefaults.Options));
    }
}
