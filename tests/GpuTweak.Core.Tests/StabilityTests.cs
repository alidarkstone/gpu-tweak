using GpuTweak.Core.Models;
using GpuTweak.Core.Stability;

namespace GpuTweak.Core.Tests;

public sealed class StabilityTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gputweak-stab").FullName;
    private DateTimeOffset _now = new(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);
    private readonly FakeSource _source = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FakeSource : IIncidentSource
    {
        public List<StabilityIncident> Events { get; } = [];
        public IReadOnlyList<StabilityIncident> ReadSince(DateTimeOffset since, IReadOnlyCollection<string> knownGames) =>
            Events.Where(e => e.Time > since).ToList();
    }

    private StabilityTracker Tracker() => new(_dir, _source, () => _now);

    private static readonly TuningSettings Uv = new(CoreOffsetMHz: 135, MemoryOffsetMHz: 500, MaxCoreClockMHz: 2790);

    [Fact]
    public void DriverResetLowersCoreFirst()
    {
        var step = StabilityAdvisor.StepDown(Uv, IncidentKind.DriverReset)!;
        Assert.Equal(105, step.Settings.CoreOffsetMHz);
        Assert.Equal(500, step.Settings.MemoryOffsetMHz);
    }

    [Fact]
    public void ReportedArtifactLowersMemoryFirst()
    {
        var step = StabilityAdvisor.StepDown(Uv, IncidentKind.UserReportedArtifact)!;
        Assert.Equal(250, step.Settings.MemoryOffsetMHz);
        Assert.Equal(135, step.Settings.CoreOffsetMHz);
    }

    [Fact]
    public void StepsEventuallyReachStock()
    {
        var s = Uv;
        for (var i = 0; i < 20 && !s.IsStock; i++) s = StabilityAdvisor.StepDown(s, IncidentKind.DriverReset)!.Settings;
        Assert.True(s.IsStock);
        Assert.Null(StabilityAdvisor.StepDown(s, IncidentKind.DriverReset));
    }

    [Fact]
    public void IncidentWhileCustomStepsDownAndPersists()
    {
        var tracker = Tracker();
        tracker.SettingsChanged(Uv);
        _now = _now.AddMinutes(5);
        _source.Events.Add(new(_now.AddMinutes(-1), IncidentKind.DriverReset, "nvlddmkm", "Cyberpunk2077"));

        var updates = tracker.Check();
        Assert.Single(updates);
        Assert.Equal(105, updates[0].Adjustment!.Settings.CoreOffsetMHz);

        var reloaded = Tracker();
        Assert.Equal(105, reloaded.State.TrackedSettings.CoreOffsetMHz);
        Assert.Single(reloaded.State.Incidents);
        // Already-seen events are not counted twice.
        Assert.Empty(reloaded.Check());
    }

    [Fact]
    public void IncidentAtStockIsOnlyLogged()
    {
        var tracker = Tracker();
        _now = _now.AddMinutes(1);
        _source.Events.Add(new(_now.AddSeconds(-5), IncidentKind.GameCrash, "crash", "RDR2"));
        var update = Assert.Single(tracker.Check());
        Assert.Null(update.Adjustment);
    }

    [Fact]
    public void CleanGamingTimeLeadsToStableSuggestion()
    {
        var tracker = Tracker();
        tracker.SettingsChanged(Uv);
        for (var i = 0; i < 5; i++)
            tracker.AddSession(new GameSession("RDR2", _now, _now.AddHours(2), 70, 250, 2700, Uv, 0));
        Assert.True(StabilityAdvisor.ShouldSuggestStable(tracker.State));

        tracker.ReportArtifact("RDR2");
        Assert.False(StabilityAdvisor.ShouldSuggestStable(tracker.State));
    }

    [Fact]
    public void WithoutAutoAdjustIncidentIsLoggedButSettingsKept()
    {
        var tracker = Tracker();
        tracker.AutoAdjust = false;
        tracker.SettingsChanged(Uv);
        _now = _now.AddMinutes(1);
        _source.Events.Add(new(_now.AddSeconds(-5), IncidentKind.DriverReset, "nvlddmkm"));
        var update = Assert.Single(tracker.Check());
        Assert.NotNull(update.Adjustment);
        Assert.Equal(Uv, tracker.State.TrackedSettings);
    }

    [Fact]
    public void SkipToNowIgnoresEarlierEvents()
    {
        var tracker = Tracker();
        tracker.SettingsChanged(Uv);
        _now = _now.AddMinutes(10);
        _source.Events.Add(new(_now.AddMinutes(-2), IncidentKind.SystemCrash, "Kernel-Power 41"));
        tracker.SkipToNow();
        Assert.Empty(tracker.Check());
    }

    [Fact]
    public void DetectorReportsSessionWithIncidents()
    {
        var detector = new GameSessionDetector(() => _now);
        GameSession? Feed(int load) => detector.Feed(new GpuTelemetry(_now, 70, 2700, 15000, 300, 360, null, null, null, load), "RDR2", Uv);

        for (var i = 0; i < 40; i++) { Assert.Null(Feed(95)); _now = _now.AddSeconds(1); }
        Assert.Equal("RDR2", detector.CurrentGame);
        detector.MarkIncident();
        GameSession? session = null;
        for (var i = 0; i < 70 && session is null; i++) { session = Feed(5); _now = _now.AddSeconds(1); }
        Assert.NotNull(session);
        Assert.Equal(1, session!.Incidents);
        Assert.Null(detector.CurrentGame);
    }

    [Fact]
    public void GoingToStockPausesButKeepsCleanTime()
    {
        var tracker = Tracker();
        tracker.SettingsChanged(Uv);
        tracker.AddSession(new GameSession("RDR2", _now, _now.AddHours(3), 70, 250, 2700, Uv, 0));
        tracker.SettingsChanged(TuningSettings.Stock);

        _now = _now.AddMinutes(1);
        _source.Events.Add(new(_now.AddSeconds(-5), IncidentKind.SystemCrash, "power cut"));
        Assert.Null(Assert.Single(tracker.Check()).Adjustment);

        tracker.SettingsChanged(Uv);
        Assert.Equal(TimeSpan.FromHours(3), tracker.State.CleanGameTime);
        Assert.Equal(Uv, tracker.State.TrackedSettings);
    }

    [Fact]
    public void RestartWhileIdleOnDesktopIsNotCounted()
    {
        var tracker = Tracker();
        tracker.SettingsChanged(Uv);
        tracker.Heartbeat(busy: true);
        _now = _now.AddMinutes(30);
        tracker.Heartbeat(busy: false);  // idle on the desktop, then the PC restarts

        _now = _now.AddMinutes(2);
        var restarted = Tracker();
        _source.Events.Add(new(_now.AddSeconds(-30), IncidentKind.SystemCrash, "Kernel-Power 41"));
        var update = Assert.Single(restarted.Check());
        Assert.True(update.Incident.Ignored);
        Assert.Null(update.Adjustment);
        Assert.Equal(Uv, restarted.State.TrackedSettings);
    }

    [Fact]
    public void RestartUnderLoadStepsDown()
    {
        var tracker = Tracker();
        tracker.SettingsChanged(Uv);
        _now = _now.AddMinutes(30);
        tracker.Heartbeat(busy: true);  // the PC goes down in the middle of a game

        _now = _now.AddMinutes(2);
        var restarted = Tracker();
        _source.Events.Add(new(_now.AddSeconds(-30), IncidentKind.SystemCrash, "Kernel-Power 41"));
        var update = Assert.Single(restarted.Check());
        Assert.False(update.Incident.Ignored);
        Assert.Equal(105, update.Adjustment!.Settings.CoreOffsetMHz);
    }

    [Fact]
    public void LastStepDownCanBeUndone()
    {
        var tracker = Tracker();
        tracker.SettingsChanged(Uv);
        _now = _now.AddMinutes(5);
        _source.Events.Add(new(_now.AddMinutes(-1), IncidentKind.DriverReset, "nvlddmkm"));
        tracker.Check();

        var undo = tracker.UndoLastAdjustment()!;
        Assert.Equal(105, undo.From.CoreOffsetMHz);
        Assert.Equal(Uv, undo.Settings);
        Assert.Equal(Uv, tracker.State.TrackedSettings);
        Assert.True(Assert.Single(tracker.State.Incidents).Ignored);
        Assert.Null(tracker.UndoLastAdjustment());
    }

    [Fact]
    public void UndoAfterHandEditOnlyDropsTheIncident()
    {
        var tracker = Tracker();
        tracker.SettingsChanged(Uv);
        _now = _now.AddMinutes(5);
        _source.Events.Add(new(_now.AddMinutes(-1), IncidentKind.DriverReset, "nvlddmkm"));
        tracker.Check();
        var byHand = Uv with { CoreOffsetMHz = 120 };
        tracker.SettingsChanged(byHand);

        Assert.NotNull(tracker.Undoable);
        var undo = tracker.UndoLastAdjustment()!;
        Assert.Equal(undo.From, undo.Settings);
        Assert.Equal(byHand, tracker.State.TrackedSettings);
        Assert.True(Assert.Single(tracker.State.Incidents).Ignored);
        Assert.Null(tracker.Undoable);
    }

    [Fact]
    public void StepDownFromOlderVersionCanBeUndone()
    {
        var stepped = Uv with { CoreOffsetMHz = 105 };
        var old = new StabilityState
        {
            LastCheckedAt = _now, TrackedSettings = stepped, CustomActive = true,
            Incidents = [new(_now.AddMinutes(-1), IncidentKind.SystemCrash, "Kernel-Power 41")],
        };
        File.WriteAllText(Path.Combine(_dir, "stability.json"), System.Text.Json.JsonSerializer.Serialize(old, JsonDefaults.Options));

        var undo = Tracker().UndoLastAdjustment()!;
        Assert.Equal(Uv, undo.Settings);
    }
}
