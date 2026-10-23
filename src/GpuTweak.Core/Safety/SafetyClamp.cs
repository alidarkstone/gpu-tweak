using GpuTweak.Core.Models;
using GpuTweak.Core.Localization;

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
            if (clamped != value) notes.Add(L.F("{0} {1}{2} → {3}{4} (izin verilen aralık {5}…{6}{7})", label, value, unit, clamped, unit, min, max, unit));
            return clamped;
        }

        var core = Fit(requested.CoreOffsetMHz, limits.CoreOffsetMinMHz, limits.CoreOffsetMaxMHz, L.T("Çekirdek offset"), " MHz");
        var memory = Fit(requested.MemoryOffsetMHz, limits.MemoryOffsetMinMHz, limits.MemoryOffsetMaxMHz, L.T("Bellek offset"), " MHz");

        int? power = requested.PowerLimitW is { } p ? Fit(p, limits.PowerMinW, limits.PowerMaxW, L.T("Güç limiti"), " W") : null;
        if (power == limits.PowerDefaultW) power = null;

        int? maxClock = requested.MaxCoreClockMHz is { } c
            ? Fit(c, limits.CoreClockMinMHz, limits.CoreClockMaxMHz, L.T("Max clock"), " MHz")
            : null;

        int? fan = null;
        if (requested.FanPercent is { } f)
        {
            if (limits.SupportsFanControl)
                fan = Fit(f, limits.FanMinPercent, limits.FanMaxPercent, "Fan", "%");
            else
                notes.Add(L.T("Bu kart fan kontrolünü desteklemiyor; fan otomatikte bırakıldı"));
        }

        return new ClampResult(new TuningSettings(core, memory, power, maxClock, fan), notes);
    }
}
