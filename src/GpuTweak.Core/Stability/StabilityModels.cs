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

public sealed record StabilityIncident(DateTimeOffset Time, IncidentKind Kind, string Description, string? Game = null);

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
}
