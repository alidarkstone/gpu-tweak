using System.Text.Json;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Stability;

/// <summary>Reads problems that happened since a given time (on Windows: the System and Application event logs).</summary>
public interface IIncidentSource
{
    IReadOnlyList<StabilityIncident> ReadSince(DateTimeOffset since, IReadOnlyCollection<string> knownGames);
}

public sealed record TrackerUpdate(StabilityIncident Incident, Adjustment? Adjustment);

/// <summary>
/// Ties incidents to the settings that were active and keeps the log. Every incident while custom
/// settings are active produces one step-down; incidents at stock are only logged.
/// </summary>
public sealed class StabilityTracker
{
    private const int MaxIncidents = 100, MaxSessions = 200;
    private readonly string _path;
    private readonly IIncidentSource _source;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private StabilityState _state;

    public StabilityTracker(string dataDirectory, IIncidentSource source, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(dataDirectory, "stability.json");
        _source = source;
        _now = now ?? (() => DateTimeOffset.Now);
        _state = Load();
    }

    public StabilityState State => _state;

    /// <summary>When off, incidents are only logged and the suggested step-down is returned without being tracked.</summary>
    public bool AutoAdjust { get; set; } = true;

    /// <summary>Ignores everything logged until now (used after an auto-tune run, whose crashes are expected).</summary>
    public void SkipToNow()
    {
        lock (_gate)
        {
            _state = _state with { LastCheckedAt = _now() };
            Save();
        }
    }

    /// <summary>
    /// Call whenever the card's settings change (apply, profile, reset). Going back to stock only pauses tracking,
    /// so a reboot without auto-apply does not throw away the clean time of the custom settings; switching to
    /// different custom settings starts the count again.
    /// </summary>
    public void SettingsChanged(TuningSettings settings)
    {
        lock (_gate)
        {
            if (settings.IsStock)
            {
                if (!_state.CustomActive) return;
                _state = _state with { CustomActive = false };
            }
            else if (settings == _state.TrackedSettings)
            {
                if (_state.CustomActive) return;
                _state = _state with { CustomActive = true };
            }
            else
            {
                _state = _state with { TrackedSettings = settings, CustomActive = true, SettingsSince = _now(), CleanGameTime = TimeSpan.Zero };
            }
            Save();
        }
    }

    /// <summary>
    /// Looks for new incidents since the last check. Returns what happened and how settings should change.
    /// <paramref name="currentGame"/> is the game running now, so its crash counts even before its first session is saved.
    /// </summary>
    public IReadOnlyList<TrackerUpdate> Check(string? currentGame = null)
    {
        lock (_gate)
        {
            var since = _state.LastCheckedAt;
            var now = _now();
            var games = _state.Sessions.Select(s => s.Game).Append(currentGame).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var incidents = _source.ReadSince(since, games).Where(i => i.Time > since).OrderBy(i => i.Time).ToList();
            _state = _state with { LastCheckedAt = now };

            var updates = new List<TrackerUpdate>();
            foreach (var incident in incidents) updates.Add(Record(incident));
            Save();
            return updates;
        }
    }

    public TrackerUpdate ReportArtifact(string? game)
    {
        lock (_gate) return Record(new StabilityIncident(_now(), IncidentKind.UserReportedArtifact, "Kullanıcı oyunda artifact gördü", game), save: true);
    }

    public void AddSession(GameSession session)
    {
        lock (_gate)
        {
            var sessions = new List<GameSession>(_state.Sessions) { session };
            if (sessions.Count > MaxSessions) sessions.RemoveRange(0, sessions.Count - MaxSessions);
            var clean = session.Incidents == 0 && session.Settings == _state.TrackedSettings
                ? _state.CleanGameTime + session.Duration
                : _state.CleanGameTime;
            _state = _state with { Sessions = sessions, CleanGameTime = clean };
            Save();
        }
    }

    private TrackerUpdate Record(StabilityIncident incident, bool save = false)
    {
        var incidents = new List<StabilityIncident>(_state.Incidents) { incident };
        if (incidents.Count > MaxIncidents) incidents.RemoveRange(0, incidents.Count - MaxIncidents);
        _state = _state with { Incidents = incidents };
        // A problem while the card ran at stock says nothing about the custom settings.
        if (!_state.CustomActive)
        {
            if (save) Save();
            return new TrackerUpdate(incident, null);
        }

        _state = _state with { CleanGameTime = TimeSpan.Zero };
        var adjustment = StabilityAdvisor.StepDown(_state.TrackedSettings, incident.Kind);
        if (adjustment is not null && AutoAdjust)
            _state = _state with { TrackedSettings = adjustment.Settings, SettingsSince = _now() };
        if (save) Save();
        return new TrackerUpdate(incident, adjustment);
    }

    private StabilityState Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<StabilityState>(File.ReadAllText(_path), JsonDefaults.Options) ?? new StabilityState { LastCheckedAt = _now() };
        }
        catch (JsonException) { }
        return new StabilityState { LastCheckedAt = _now(), SettingsSince = _now() };
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonDefaults.Options));
        File.Move(temp, _path, overwrite: true);
    }
}
