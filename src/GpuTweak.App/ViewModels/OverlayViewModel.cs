using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GpuTweak.Core.Models;
using GpuTweak.Core.Settings;
using GpuTweak.Core;
using GpuTweak.Core.Localization;

namespace GpuTweak.App.ViewModels;

public sealed record OverlayRow(string Label, string Value, string Unit);

public sealed class OverlayViewModel : ObservableObject
{
    private OverlaySettings _settings;
    private IReadOnlyList<OverlayRow> _rows = [];

    public OverlayViewModel(GpuInfo info, OverlaySettings settings)
    {
        Info = info;
        _settings = settings;
        CustomLogo = LoadCustomLogo(info.Vendor);
    }

    public event Action<OverlaySettings>? SettingsChanged;

    public GpuInfo Info { get; }
    public string Model => Info.ShortName;
    public string VendorName => Info.Vendor switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", GpuVendor.Intel => "intel", _ => "GPU" };
    public Brush VendorBrush => Info.Vendor switch
    {
        GpuVendor.Nvidia => new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)),
        GpuVendor.Amd => new SolidColorBrush(Color.FromRgb(0xED, 0x1C, 0x24)),
        GpuVendor.Intel => new SolidColorBrush(Color.FromRgb(0x00, 0x71, 0xC5)),
        _ => Brushes.White,
    };

    /// <summary>
    /// Brand logos are trademarks, so none ship inside the exe. A user can drop nvidia.png / amd.png
    /// into GpuTweakData\logos and it replaces the text badge.
    /// </summary>
    public ImageSource? CustomLogo { get; }
    public bool HasCustomLogo => CustomLogo is not null;

    public IReadOnlyList<OverlayRow> Rows { get => _rows; private set => Set(ref _rows, value); }

    public OverlaySettings Settings => _settings;
    public bool Enabled { get => _settings.Enabled; set => Change(_settings with { Enabled = value }); }
    public OverlayCorner Corner { get => _settings.Corner; set => Change(_settings with { Corner = value }); }
    public double Opacity { get => _settings.Opacity; set => Change(_settings with { Opacity = Math.Clamp(value, 0.2, 1) }); }
    public double Scale { get => _settings.Scale; set => Change(_settings with { Scale = Math.Clamp(value, 0.75, 1.5) }); }
    public bool ShowLogo { get => _settings.ShowLogo; set => Change(_settings with { ShowLogo = value }); }
    public bool ShowModel { get => _settings.ShowModel; set => Change(_settings with { ShowModel = value }); }
    public bool ShowTemperature { get => _settings.ShowTemperature; set => Change(_settings with { ShowTemperature = value }); }
    public bool ShowCoreClock { get => _settings.ShowCoreClock; set => Change(_settings with { ShowCoreClock = value }); }
    public bool ShowMemoryClock { get => _settings.ShowMemoryClock; set => Change(_settings with { ShowMemoryClock = value }); }
    public bool ShowPower { get => _settings.ShowPower; set => Change(_settings with { ShowPower = value }); }
    public bool ShowVram { get => _settings.ShowVram; set => Change(_settings with { ShowVram = value }); }
    public bool ShowFan { get => _settings.ShowFan; set => Change(_settings with { ShowFan = value }); }
    public bool ShowFps { get => _settings.ShowFps; set => Change(_settings with { ShowFps = value }); }
    public bool ShowLoad { get => _settings.ShowLoad; set => Change(_settings with { ShowLoad = value }); }

    public Brush Background => new SolidColorBrush(Color.FromArgb((byte)(_settings.Opacity * 255), 0x16, 0x16, 0x18));

    public void SetCorner(string corner)
    {
        if (Enum.TryParse<OverlayCorner>(corner, out var c)) Corner = c;
    }

    private GpuTelemetry? _last;
    /// <summary>Once the card has reported a memory temperature, the row stays (with "–" while there is no reading).</summary>
    private bool _hasMemoryTemperature;
    private FrameRate? _lastFps;

    public void Update(GpuTelemetry s, FrameRate? fps = null)
    {
        _last = s;
        _lastFps = fps;
        var rows = new List<OverlayRow>();
        if (ShowFps && fps is not null)
        {
            rows.Add(new("FPS", $"{fps.Current:0}", ""));
            rows.Add(new(L.T("Min · Ort · Maks"), $"{fps.Min:0} · {fps.Average:0} · {fps.Max:0}", ""));
        }
        if (ShowLoad && s.LoadPercent is { } l) rows.Add(new("GPU", $"{l}", "%"));
        if (ShowTemperature && s.TemperatureC is { } t) rows.Add(new(L.T("Sıcaklık"), $"{t:0}", "°C"));
        _hasMemoryTemperature |= s.MemoryTemperatureC is not null;
        if (ShowTemperature && _hasMemoryTemperature)
            rows.Add(new(L.T("Bellek sıc."), s.MemoryTemperatureC is { } mt ? $"{mt:0}" : "–", "°C"));
        if (ShowCoreClock && s.CoreClockMHz is { } c) rows.Add(new(L.T("Çekirdek"), $"{c}", "MHz"));
        if (ShowMemoryClock && s.MemoryClockMHz is { } m) rows.Add(new(L.T("Bellek"), $"{m}", "MHz"));
        if (ShowPower && s.PowerW is { } p) rows.Add(new(L.T("Güç"), $"{p:0}", "W"));
        if (ShowVram && s.VramUsedBytes is { } v) rows.Add(new("VRAM", $"{v / (1024.0 * 1024 * 1024):0.0}", "GB"));
        if (ShowFan && s.FanPercent is { } f) rows.Add(new("Fan", $"{f}", "%"));
        Rows = rows;
    }

    private void Change(OverlaySettings updated)
    {
        if (updated == _settings) return;
        _settings = updated;
        OnPropertyChanged(string.Empty); // refresh every binding
        if (_last is not null) Update(_last, _lastFps);
        SettingsChanged?.Invoke(updated);
    }

    private static ImageSource? LoadCustomLogo(GpuVendor vendor)
    {
        var file = vendor switch { GpuVendor.Nvidia => "nvidia.png", GpuVendor.Amd => "amd.png", GpuVendor.Intel => "intel.png", _ => null };
        if (file is null) return null;
        var path = Path.Combine(Core.DataPaths.Resolve(), "logos", file);
        if (!File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }
}
