using GpuTweak.Core.Models;

namespace GpuTweak.Core.Safety;

public sealed record ClampResult(TuningSettings Settings, IReadOnlyList<string> Adjustments)
{
    public bool WasAdjusted => Adjustments.Count > 0;
}

/// <summary>Forces every requested value into the range the driver/vBIOS reports as allowed.</summary>
public static class SafetyClamp
{
    public static ClampResult Clamp(TuningSettings requested, TuningLimits limits)
    {
        var notes = new List<string>();

        int Fit(int value, int min, int max, string label, string unit)
        {
            var clamped = Math.Clamp(value, min, Math.Max(min, max));
            if (clamped != value) notes.Add($"{label} {value}{unit} → {clamped}{unit} (izin verilen aralık {min}…{max}{unit})");
            return clamped;
        }

        var core = Fit(requested.CoreOffsetMHz, limits.CoreOffsetMinMHz, limits.CoreOffsetMaxMHz, "Çekirdek offset", " MHz");
        var memory = Fit(requested.MemoryOffsetMHz, limits.MemoryOffsetMinMHz, limits.MemoryOffsetMaxMHz, "Bellek offset", " MHz");

        int? power = requested.PowerLimitW is { } p ? Fit(p, limits.PowerMinW, limits.PowerMaxW, "Güç limiti", " W") : null;
        if (power == limits.PowerDefaultW) power = null;

        int? maxClock = requested.MaxCoreClockMHz is { } c
            ? Fit(c, limits.CoreClockMinMHz, limits.CoreClockMaxMHz, "Max clock", " MHz")
            : null;

        int? fan = null;
        if (requested.FanPercent is { } f)
        {
            if (limits.SupportsFanControl)
                fan = Fit(f, limits.FanMinPercent, limits.FanMaxPercent, "Fan", "%");
            else
                notes.Add("Bu kart fan kontrolünü desteklemiyor; fan otomatikte bırakıldı");
        }

        return new ClampResult(new TuningSettings(core, memory, power, maxClock, fan), notes);
    }
}
