using System.Text.Json;
using GpuTweak.Core.Localization;
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
    /// <summary>A restart counts only if the GPU was busy this close to the last moment Tweakstone was seen running.</summary>
    public static readonly TimeSpan BusyBeforeCrash = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan HeartbeatSaveInterval = TimeSpan.FromSeconds(15);
    private readonly string _path;
    private readonly IIncidentSource _source;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _gate = new();
    private StabilityState _state;
    /// <summary>What the previous run last saw; read once at start, before this run's heartbeats overwrite it.</summary>
    private readonly DateTimeOffset? _previousAlive, _previousBusy;
    private DateTimeOffset _lastHeartbeatSave;

    public StabilityTracker(string dataDirectory, IIncidentSource source, Func<DateTimeOffset>? now = null)
    {
        _path = Path.Combine(dataDirectory, "stability.json");
        _source = source;
        _now = now ?? (() => DateTimeOffset.Now);
        _state = Load();
        _previousAlive = _state.LastAliveAt;
        _previousBusy = _state.LastBusyAt;
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
    /// Call with every telemetry sample. Saved now and then, so after a sudden restart the next run knows whether
    /// the GPU was working (a game, a test) or idle on the desktop when it happened.
    /// </summary>
    public void Heartbeat(bool busy)
    {
        lock (_gate)
        {
            var now = _now();
            _state = _state with { LastAliveAt = now, LastBusyAt = busy ? now : _state.LastBusyAt };
            if (now - _lastHeartbeatSave < HeartbeatSaveInterval) return;
            _lastHeartbeatSave = now;
            Save();
        }
    }

    /// <summary>The last automatic step-down that can still be taken back, if any.</summary>
    public Adjustment? Undoable
    {
        get
        {
            lock (_gate)
                return _state.LastAdjustment is { } last && _state.Incidents.Any(i => !i.Ignored) ? last : null;
        }
    }

    /// <summary>
    /// Takes back the last automatic step-down (the user says the problem was not the card's): the incident stops
    /// counting and the tracked settings go back. Returns the step back to the earlier settings, or null when there is
    /// nothing to undo. When the user has changed the settings by hand since, only the incident is dropped
    /// (<see cref="Adjustment.From"/> and <see cref="Adjustment.Settings"/> are then the same).
    /// </summary>
    public Adjustment? UndoLastAdjustment()
    {
        lock (_gate)
        {
            if (_state.LastAdjustment is not { } last) return null;
            var incidents = new List<StabilityIncident>(_state.Incidents);
            var index = incidents.FindLastIndex(i => !i.Ignored);
            if (index < 0) return null;
            incidents[index] = incidents[index] with { Ignored = true };
            var untouched = _state.TrackedSettings == last.Settings;
            var back = untouched ? last.From : _state.TrackedSettings;
            _state = _state with { Incidents = incidents, TrackedSettings = back, LastAdjustment = null };
            Save();
            return new Adjustment(untouched ? last.Settings : back, back, last.Description);
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
            foreach (var incident in incidents)
                updates.Add(Record(incident.Kind == IncidentKind.SystemCrash && !WasBusyBeforeCrash(incident) ? incident with { Ignored = true } : incident));
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
        if (!_state.CustomActive || incident.Ignored)
        {
            if (save) Save();
            return new TrackerUpdate(incident, null);
        }

        _state = _state with { CleanGameTime = TimeSpan.Zero };
        var adjustment = StabilityAdvisor.StepDown(_state.TrackedSettings, incident.Kind);
        if (adjustment is not null && AutoAdjust)
            _state = _state with { TrackedSettings = adjustment.Settings, SettingsSince = _now(), LastAdjustment = adjustment };
        if (save) Save();
        return new TrackerUpdate(incident, adjustment);
    }

    /// <summary>
    /// Windows logs an unexpected restart at the next boot, so its time says nothing about what was running.
    /// What the previous run saw last does: a restart while the GPU sat idle on the desktop is not the settings' fault
    /// (an undervolt fails under load). Without that information (older version, Tweakstone not running) it counts.
    /// </summary>
    private bool WasBusyBeforeCrash(StabilityIncident incident)
    {
        if (_previousAlive is not { } alive || alive > incident.Time) return true;
        return _previousBusy is { } busy && alive - busy <= BusyBeforeCrash;
    }

    private StabilityState Load()
    {
        try
        {
            if (File.Exists(_path))
                return Migrate(JsonSerializer.Deserialize<StabilityState>(File.ReadAllText(_path), JsonDefaults.Options) ?? new StabilityState { LastCheckedAt = _now() });
        }
        catch (JsonException) { }
        return new StabilityState { LastCheckedAt = _now(), SettingsSince = _now() };
    }

    /// <summary>
    /// Versions before 0.12 did not remember their step-downs. If the last thing that happened was a core step-down
    /// after a crash, rebuild it so it can be undone.
    /// </summary>
    private static StabilityState Migrate(StabilityState state)
    {
        if (state.LastAliveAt is not null || state.LastAdjustment is not null || !state.CustomActive) return state;
        if (state.Incidents.LastOrDefault() is not { Ignored: false, Kind: not IncidentKind.UserReportedArtifact }) return state;
        var settings = state.TrackedSettings;
        if (settings.CoreOffsetMHz <= 0) return state;
        var from = settings with { CoreOffsetMHz = settings.CoreOffsetMHz + StabilityAdvisor.CoreStepMHz };
        return state with { LastAdjustment = new Adjustment(from, settings, L.F("çekirdek offset +{0} → +{1} MHz", from.CoreOffsetMHz, settings.CoreOffsetMHz)) };
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_state, JsonDefaults.Options));
        File.Move(temp, _path, overwrite: true);
    }
}
