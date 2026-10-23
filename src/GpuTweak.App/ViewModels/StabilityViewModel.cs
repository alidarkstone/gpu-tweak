using GpuTweak.Core.Stability;

namespace GpuTweak.App.ViewModels;

public sealed record IncidentRow(string When, string Text);

/// <summary>Counters and recent log lines for the "Oyun kararlılığı" card.</summary>
public sealed class StabilityViewModel : ObservableObject
{
    private string _cleanTime = "0 dk", _currentGame = "Oyun yok", _lastChange = "";
    private int _incidentCount, _sessionCount;
    private double _cleanLevel;
    private IReadOnlyList<IncidentRow> _recent = [];

    public string CleanTime { get => _cleanTime; private set => Set(ref _cleanTime, value); }
    /// <summary>0..1 of the way to the clean time after which "Stabil" is suggested.</summary>
    public double CleanLevel { get => _cleanLevel; private set => Set(ref _cleanLevel, value); }
    public string CleanGoal => $"{StabilityAdvisor.StableAfter.TotalHours:0} saat sorunsuz oyunda \"Stabil\" önerilir";
    public int IncidentCount { get => _incidentCount; private set => Set(ref _incidentCount, value); }
    public int SessionCount { get => _sessionCount; private set => Set(ref _sessionCount, value); }
    public string CurrentGame { get => _currentGame; private set => Set(ref _currentGame, value); }
    public string LastChange { get => _lastChange; set => Set(ref _lastChange, value); }
    public IReadOnlyList<IncidentRow> Recent { get => _recent; private set => Set(ref _recent, value); }

    public void Update(StabilityState state, string? currentGame)
    {
        CleanTime = Format(state.CleanGameTime);
        CleanLevel = Math.Clamp(state.CleanGameTime / StabilityAdvisor.StableAfter, 0, 1);
        IncidentCount = state.Incidents.Count;
        SessionCount = state.Sessions.Count;
        Recent = state.Incidents.AsEnumerable().Reverse().Take(3)
            .Select(i => new IncidentRow(i.Time.LocalDateTime.ToString("dd.MM HH:mm"), i.Game is { } g && !i.Description.StartsWith(g) ? $"{g}: {i.Description}" : i.Description))
            .ToList();
        SetCurrentGame(currentGame);
    }

    public void SetCurrentGame(string? game) => CurrentGame = game ?? "Oyun yok";

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} sa {t.Minutes} dk" : $"{t.Minutes} dk";
}
