using System.IO;
using System.Diagnostics.Eventing.Reader;
using GpuTweak.Core.Stability;

namespace GpuTweak.App.Native;

/// <summary>
/// Reads GPU-related problems from the Windows event logs:
/// - System / Display 4101 and nvlddmkm 13, 14, 153: the display driver stopped responding and was reset (TDR)
/// - System / Kernel-Power 41 and WER BugCheck 1001: unexpected restart or blue screen
/// - Application / Application Error 1000, Application Hang 1002: crash or hang of a known game
/// </summary>
internal sealed class WindowsIncidentSource : IIncidentSource
{
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(30);

    public IReadOnlyList<StabilityIncident> ReadSince(DateTimeOffset since, IReadOnlyCollection<string> knownGames)
    {
        var incidents = new List<StabilityIncident>();
        var time = since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

        foreach (var record in Query("System", time, "Display", "nvlddmkm", "Microsoft-Windows-Kernel-Power", "Microsoft-Windows-WER-SystemErrorReporting"))
        {
            var at = (DateTimeOffset)(record.TimeCreated ?? DateTime.Now);
            switch (record.ProviderName, record.Id)
            {
                case ("Display", 4101):
                case ("nvlddmkm", 13 or 14 or 153):
                    incidents.Add(new(at, IncidentKind.DriverReset, "Ekran sürücüsü yanıt vermeyi kesti ve sıfırlandı"));
                    break;
                case ("Microsoft-Windows-Kernel-Power", 41):
                    incidents.Add(new(at, IncidentKind.SystemCrash, "Bilgisayar düzgün kapanmadan yeniden başladı"));
                    break;
                case ("Microsoft-Windows-WER-SystemErrorReporting", 1001):
                    incidents.Add(new(at, IncidentKind.SystemCrash, "Mavi ekran (BSOD)"));
                    break;
            }
        }

        var games = new HashSet<string>(knownGames, StringComparer.OrdinalIgnoreCase);
        foreach (var record in Query("Application", time, "Application Error", "Application Hang"))
        {
            if (record.Id is not (1000 or 1002)) continue;
            var app = record.Properties.Count > 0 ? Path.GetFileNameWithoutExtension(record.Properties[0].Value?.ToString() ?? "") : "";
            if (!games.Contains(app)) continue;
            var at = (DateTimeOffset)(record.TimeCreated ?? DateTime.Now);
            incidents.Add(new(at, IncidentKind.GameCrash, record.Id == 1000 ? $"{app} çöktü" : $"{app} yanıt vermedi", app));
        }

        return Merge(incidents);
    }

    /// <summary>One driver reset logs several events within seconds (and often takes the game down with it); count it once.</summary>
    private static List<StabilityIncident> Merge(List<StabilityIncident> incidents)
    {
        var merged = new List<StabilityIncident>();
        foreach (var incident in incidents.OrderBy(i => i.Time))
        {
            var last = merged.LastOrDefault();
            if (last is not null && incident.Time - last.Time < MergeWindow) continue;
            merged.Add(incident);
        }
        return merged;
    }

    private static IEnumerable<EventRecord> Query(string log, string sinceUtc, params string[] providers)
    {
        var providerFilter = string.Join(" or ", providers.Select(p => $"@Name='{p}'"));
        var xpath = $"*[System[Provider[{providerFilter}] and TimeCreated[@SystemTime>'{sinceUtc}']]]";
        EventLogReader reader;
        try
        {
            reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath));
        }
        catch (EventLogException)
        {
            yield break;
        }
        using (reader)
        {
            while (true)
            {
                EventRecord? record;
                try { record = reader.ReadEvent(); }
                catch (EventLogException) { yield break; }
                if (record is null) yield break;
                using (record) yield return record;
            }
        }
    }
}
