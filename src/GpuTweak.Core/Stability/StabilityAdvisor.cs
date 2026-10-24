using GpuTweak.Core.Models;
using GpuTweak.Core.Localization;

namespace GpuTweak.Core.Stability;

public sealed record Adjustment(TuningSettings From, TuningSettings Settings, string Description);

/// <summary>Decides how to react to problems seen while gaming.</summary>
public static class StabilityAdvisor
{
    public const int CoreStepMHz = 30;
    public const int MemoryStepMHz = 250;
    public const int VoltageStepMv = 10;
    /// <summary>AMD memory clocks move in much smaller steps (the whole range is a few hundred MHz).</summary>
    public const int SmallMemoryStepMHz = 25;
    public static readonly TimeSpan StableAfter = TimeSpan.FromHours(10);

    /// <summary>
    /// One step towards safety. Visual artifacts most often come from memory overclocks, so a reported artifact
    /// lowers memory first; driver resets and crashes are typically core undervolt/overclock, so core goes first.
    /// Returns null when the settings are already as safe as they get (stock).
    /// </summary>
    public static Adjustment? StepDown(TuningSettings current, IncidentKind kind, int memoryStepMHz = MemoryStepMHz)
    {
        if (current.IsStock) return null;

        if (kind == IncidentKind.UserReportedArtifact && current.MemoryOffsetMHz > 0)
            return LowerMemory(current, memoryStepMHz);
        if (current.CoreOffsetMHz > 0)
        {
            var core = Math.Max(0, current.CoreOffsetMHz - CoreStepMHz);
            return new Adjustment(current, current with { CoreOffsetMHz = core }, L.F("çekirdek offset +{0} → +{1} MHz", current.CoreOffsetMHz, core));
        }
        if (current.VoltageOffsetMv < 0)
        {
            var voltage = Math.Min(0, current.VoltageOffsetMv + VoltageStepMv);
            return new Adjustment(current, current with { VoltageOffsetMv = voltage }, L.F("voltaj {0} → {1} mV", current.VoltageOffsetMv, voltage));
        }
        if (current.MemoryOffsetMHz > 0) return LowerMemory(current, memoryStepMHz);
        if (current.MaxCoreClockMHz is not null)
            return new Adjustment(current, current with { MaxCoreClockMHz = null }, L.T("max clock kilidi kaldırıldı"));
        if (current.PowerLimitW is not null)
            return new Adjustment(current, current with { PowerLimitW = null }, L.T("güç limiti fabrika değerine döndü"));
        return new Adjustment(current, TuningSettings.Stock, L.T("varsayılan ayarlara dönüldü"));
    }

    private static Adjustment LowerMemory(TuningSettings current, int step)
    {
        var memory = Math.Max(0, current.MemoryOffsetMHz - step);
        return new Adjustment(current, current with { MemoryOffsetMHz = memory }, L.F("bellek offset +{0} → +{1} MHz", current.MemoryOffsetMHz, memory));
    }

    /// <summary>True once the current custom settings have survived enough real gaming without a single incident.</summary>
    public static bool ShouldSuggestStable(StabilityState state) =>
        state.CustomActive && !state.TrackedSettings.IsStock && state.CleanGameTime >= StableAfter;
}
