namespace GpuTweak.Core;

/// <summary>Min / max / average of a sensor since it was last reset.</summary>
public sealed class RunningStats
{
    private double _sum;

    public double? Min { get; private set; }
    public double? Max { get; private set; }
    public int Count { get; private set; }
    public double? Average => Count == 0 ? null : _sum / Count;

    public void Add(double? value)
    {
        if (value is not { } v || double.IsNaN(v)) return;
        Min = Min is { } min ? Math.Min(min, v) : v;
        Max = Max is { } max ? Math.Max(max, v) : v;
        _sum += v;
        Count++;
    }

    public void Reset()
    {
        Min = Max = null;
        _sum = 0;
        Count = 0;
    }
}
