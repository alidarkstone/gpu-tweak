using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GpuTweak.App.Stress;
using GpuTweak.Core.AutoTune;
using GpuTweak.Core.Models;

namespace GpuTweak.App.ViewModels;

public sealed record TuneStepRow(string Label, bool Passed, string Result, string Power, string Temperature, string Performance);

/// <summary>Live view of an auto-tune run: GPU result map, sensor history and per-step results.</summary>
public sealed class TuneMonitorViewModel : ObservableObject
{
    private const int MapPixels = D3D11StressTest.MapSize / 2;
    private const int HistoryLength = 120;
    public const double ChartWidth = 264, ChartHeight = 70;

    private readonly Queue<GpuTelemetry> _history = new();
    private D3D11StressTest? _stress;
    private StressSnapshot? _drawn;
    private DateTimeOffset _startedAt;

    public TuneMonitorViewModel()
    {
        ErrorMap = new WriteableBitmap(MapPixels, MapPixels, 96, 96, PixelFormats.Bgr32, null);
    }

    public WriteableBitmap ErrorMap { get; }
    public ObservableCollection<TuneStepRow> Steps { get; } = [];

    private string _errorsText = "Test çalışmıyor", _patternText = "", _elapsed = "0:00";
    private string _clock = "–", _temperature = "–", _power = "–", _load = "–";
    private bool _hasErrors;
    private PointCollection _clockPoints = [], _temperaturePoints = [], _powerPoints = [];
    private bool _hasMap;
    public bool HasMap { get => _hasMap; private set => Set(ref _hasMap, value); }
    public string ErrorsText { get => _errorsText; private set => Set(ref _errorsText, value); }
    public bool HasErrors { get => _hasErrors; private set => Set(ref _hasErrors, value); }
    public string PatternText { get => _patternText; private set => Set(ref _patternText, value); }
    public string Elapsed { get => _elapsed; private set => Set(ref _elapsed, value); }
    public string Clock { get => _clock; private set => Set(ref _clock, value); }
    public string Temperature { get => _temperature; private set => Set(ref _temperature, value); }
    public string Power { get => _power; private set => Set(ref _power, value); }
    public string Load { get => _load; private set => Set(ref _load, value); }
    public PointCollection ClockPoints { get => _clockPoints; private set => Set(ref _clockPoints, value); }
    public PointCollection TemperaturePoints { get => _temperaturePoints; private set => Set(ref _temperaturePoints, value); }
    public PointCollection PowerPoints { get => _powerPoints; private set => Set(ref _powerPoints, value); }

    public void Start(D3D11StressTest stress)
    {
        _stress = stress;
        _drawn = null;
        _startedAt = DateTimeOffset.Now;
        Steps.Clear();
        ErrorsText = "Hazırlanıyor…";
        HasMap = false;
        HasErrors = false;
        PatternText = "";
        Clear();
    }

    public void Stop() => _stress = null;

    public void AddStep(TuneStep step) => Steps.Add(new TuneStepRow(
        step.Label,
        step.Passed,
        step.Passed ? "Geçti" : step.Reason ?? "Geçemedi",
        step.AvgPowerW > 0 ? $"{step.AvgPowerW:0} W" : "–",
        step.MaxTemperatureC > 0 ? $"{step.MaxTemperatureC:0}°" : "–",
        step.RelativeScore > 0 ? $"%{100 * step.RelativeScore:0.0}" : "–"));

    /// <summary>Called on the UI thread once per telemetry sample.</summary>
    public void OnSample(GpuTelemetry s)
    {
        if (_stress is not null) Elapsed = (DateTimeOffset.Now - _startedAt).ToString(@"m\:ss");

        _history.Enqueue(s);
        while (_history.Count > HistoryLength) _history.Dequeue();
        Clock = s.CoreClockMHz is { } c ? $"{c} MHz" : "–";
        Temperature = s.TemperatureC is { } t ? $"{t:0}°C" : "–";
        Power = s.PowerW is { } p ? $"{p:0} W" : "–";
        Load = s.LoadPercent is { } l ? $"%{l}" : "–";
        ClockPoints = Chart(h => h.CoreClockMHz, 0, 3300);
        TemperaturePoints = Chart(h => h.TemperatureC, 20, 100);
        PowerPoints = Chart(h => h.PowerW, 0, Math.Max(100, s.PowerLimitW ?? 400));

        DrawMap();
    }

    private PointCollection Chart(Func<GpuTelemetry, double?> value, double min, double max)
    {
        var points = new PointCollection();
        var i = HistoryLength - _history.Count;
        foreach (var sample in _history)
        {
            if (value(sample) is { } v)
            {
                var y = ChartHeight - Math.Clamp((v - min) / (max - min), 0, 1) * ChartHeight;
                points.Add(new Point(i * ChartWidth / (HistoryLength - 1), y));
            }
            i++;
        }
        points.Freeze();
        return points;
    }

    private void DrawMap()
    {
        var snapshot = _stress?.Snapshot;
        if (snapshot is null || ReferenceEquals(snapshot, _drawn)) return;
        _drawn = snapshot;
        HasMap = true;

        PatternText = snapshot.Pattern == StressPattern.Heavy ? "Ağır yük" : snapshot.Pattern == StressPattern.Transient ? "Dalgalı yük" : "Karışık yük";
        HasErrors = snapshot.Errors > 0;
        ErrorsText = snapshot.Errors == 0
            ? $"Hatalı hesap yok · {snapshot.Checked:N0} sonuç kontrol edildi"
            : $"{snapshot.Errors:N0} hatalı hesap · {snapshot.Checked:N0} sonuç kontrol edildi";

        // Each pixel stands for 2×2 GPU threads. Correct results are drawn as blue noise derived from the value
        // itself; any thread that was ever wrong in this run is drawn red and stays red.
        var pixels = new int[MapPixels * MapPixels];
        const int size = D3D11StressTest.MapSize;
        for (var y = 0; y < MapPixels; y++)
        {
            for (var x = 0; x < MapPixels; x++)
            {
                var i = y * 2 * size + x * 2;
                var wrong = snapshot.ErrorMask[i] | snapshot.ErrorMask[i + 1] | snapshot.ErrorMask[i + size] | snapshot.ErrorMask[i + size + 1];
                if (wrong != 0)
                {
                    pixels[y * MapPixels + x] = 0xFF3B30;
                    continue;
                }
                var v = snapshot.Results[i];
                var r = 10 + (int)(v & 0x1F);
                var g = 30 + (int)((v >> 8) & 0x3F);
                var b = 70 + (int)((v >> 16) & 0x7F);
                pixels[y * MapPixels + x] = (r << 16) | (g << 8) | b;
            }
        }
        ErrorMap.WritePixels(new Int32Rect(0, 0, MapPixels, MapPixels), pixels, MapPixels * 4, 0);
    }

    private void Clear()
    {
        var pixels = new int[MapPixels * MapPixels];
        Array.Fill(pixels, 0x1C1C1E);
        ErrorMap.WritePixels(new Int32Rect(0, 0, MapPixels, MapPixels), pixels, MapPixels * 4, 0);
    }
}
