using System.Text.Json;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.AutoTune;

/// <summary>What the auto-tune optimizes for.</summary>
public enum TuneGoal
{
    /// <summary>Undervolt: same clocks as stock at lower voltage. Cooler and quieter, same FPS.</summary>
    Cool,
    /// <summary>Overclock: no clock cap, power limit at the vBIOS maximum, core offset as high as stays error-free.</summary>
    Performance,
    /// <summary>Clock capped a little below stock boost plus undervolt: much less power and heat for a small FPS cost.</summary>
    Efficient,
    /// <summary>Memory overclock on top of the current core settings, judged by verified bandwidth (<see cref="MemoryTuner"/>).</summary>
    Memory,
}

/// <summary>
/// Written before every tuning step. If the PC crashes mid-test, the next start knows which offset
/// was being tested (and treats it as unstable) and can resume from the last stable value.
/// </summary>
public sealed record TuneJournal(
    int MaxClockMHz,
    double BaselineScore,
    double BaselinePowerW,
    double BaselineTemperatureC,
    int LastStableOffsetMHz,
    int? TestingOffsetMHz,
    DateTimeOffset StartedAt,
    TuneGoal Goal = TuneGoal.Cool,
    /// <summary>
    /// AMD: the steps are millivolts of undervolt (the offset fields hold the depth, e.g. 50 = -50 mV), and
    /// <see cref="CapOffsetMHz"/> lowers the max clock for the efficient goal.
    /// </summary>
    bool VoltageMode = false,
    int CapOffsetMHz = 0)
{
    private const string FileName = "autotune.json";

    /// <summary>"+45 MHz" or "-50 mV", for messages.</summary>
    public string Label(int step) => VoltageMode ? $"-{step} mV" : $"+{step} MHz";

    /// <summary>The settings a step at <paramref name="offsetMHz"/> runs with, for this journal's goal.</summary>
    public TuningSettings Settings(int offsetMHz, int powerMaxW) => VoltageMode
        ? new TuningSettings(
            CoreOffsetMHz: CapOffsetMHz,
            // Power limits are percent on AMD; 100 is stock and needs no setting.
            PowerLimitW: Goal == TuneGoal.Performance && powerMaxW > 100 ? powerMaxW : null,
            VoltageOffsetMv: -offsetMHz)
        : Goal switch
    {
        TuneGoal.Performance => new TuningSettings(CoreOffsetMHz: offsetMHz, PowerLimitW: powerMaxW > 0 ? powerMaxW : null),
        _ => new TuningSettings(CoreOffsetMHz: offsetMHz, MaxCoreClockMHz: MaxClockMHz),
    };

    public static TuneJournal? Load(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<TuneJournal>(File.ReadAllText(path), JsonDefaults.Options); }
        catch (JsonException) { return null; }
    }

    public void Save(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonDefaults.Options));
        File.Move(temp, path, overwrite: true);
    }

    public static void Delete(string dataDirectory)
    {
        try { File.Delete(Path.Combine(dataDirectory, FileName)); } catch (IOException) { }
    }
}
