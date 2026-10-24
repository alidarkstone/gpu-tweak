using GpuTweak.Core.Models;

namespace GpuTweak.Core.Stability;

public enum IncidentKind
{
    /// <summary>The display driver stopped responding and was reset (screen flashes black, game usually closes).</summary>
    DriverReset,
    /// <summary>A game process crashed.</summary>
    GameCrash,
    /// <summary>Windows restarted without shutting down cleanly, or showed a blue screen.</summary>
    SystemCrash,
    /// <summary>The user pressed "I saw an artifact".</summary>
    UserReportedArtifact,
}

/// <param name="Ignored">
/// Logged but not held against the settings: a restart while the GPU was idle (another program, power cut, Windows
/// update), or one the user said was not caused by the settings.
/// </param>
public sealed record StabilityIncident(DateTimeOffset Time, IncidentKind Kind, string Description, string? Game = null, bool Ignored = false);

/// <summary>One stretch of gaming, detected from sustained GPU load.</summary>
public sealed record GameSession(
    string Game,
    DateTimeOffset Start,
    DateTimeOffset End,
    double MaxTemperatureC,
    double AvgPowerW,
    int AvgCoreClockMHz,
    TuningSettings Settings,
    int Incidents)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Everything the stability tracker remembers, saved as stability.json in the data folder.</summary>
public sealed record StabilityState
{
    public DateTimeOffset LastCheckedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>When the currently tracked settings were applied; clean gaming time counts from here.</summary>
    public DateTimeOffset SettingsSince { get; init; } = DateTimeOffset.Now;
    /// <summary>The last custom settings used; kept while the card is temporarily at stock.</summary>
    public TuningSettings TrackedSettings { get; init; } = TuningSettings.Stock;
    /// <summary>True while <see cref="TrackedSettings"/> are actually on the card.</summary>
    public bool CustomActive { get; init; }
    public TimeSpan CleanGameTime { get; init; }
    public List<StabilityIncident> Incidents { get; init; } = [];
    public List<GameSession> Sessions { get; init; } = [];
    /// <summary>Last time Tweakstone was running, and last time the GPU was busy; a restart is only blamed on the settings when they were close.</summary>
    public DateTimeOffset? LastAliveAt { get; init; }
    public DateTimeOffset? LastBusyAt { get; init; }
    /// <summary>The most recent automatic step-down, so the user can undo it when the problem was not the card's.</summary>
    public Adjustment? LastAdjustment { get; init; }
}
