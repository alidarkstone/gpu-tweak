using System.Text.Json;

namespace GpuTweak.Core.AutoTune;

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
    DateTimeOffset StartedAt)
{
    private const string FileName = "autotune.json";

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
