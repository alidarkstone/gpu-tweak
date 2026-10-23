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

public sealed record AppSettings
{
    public OverlaySettings Overlay { get; init; } = new();
    public double ThermalLimitC { get; init; } = 88;
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
