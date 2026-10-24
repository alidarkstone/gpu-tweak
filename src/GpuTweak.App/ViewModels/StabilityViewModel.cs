using GpuTweak.Core.Stability;
using GpuTweak.Core.Localization;

namespace GpuTweak.App.ViewModels;

public sealed record IncidentRow(string When, string Text);

/// <summary>Counters and recent log lines for the "Oyun kararlılığı" card.</summary>
public sealed class StabilityViewModel : ObservableObject
{
    private string _cleanTime = L.T("0 dk"), _currentGame = L.T("Oyun yok"), _lastChange = "";
    private int _incidentCount, _sessionCount;
    private bool _canUndo;
    private double _cleanLevel;
    private IReadOnlyList<IncidentRow> _recent = [];

    public string CleanTime { get => _cleanTime; private set => Set(ref _cleanTime, value); }
    /// <summary>0..1 of the way to the clean time after which "Stabil" is suggested.</summary>
    public double CleanLevel { get => _cleanLevel; private set => Set(ref _cleanLevel, value); }
    public string CleanGoal => L.F("{0:0} saat sorunsuz oyunda \"Stabil\" önerilir", StabilityAdvisor.StableAfter.TotalHours);
    public int IncidentCount { get => _incidentCount; private set => Set(ref _incidentCount, value); }
    public int SessionCount { get => _sessionCount; private set => Set(ref _sessionCount, value); }
    public string CurrentGame { get => _currentGame; private set => Set(ref _currentGame, value); }
    public string LastChange { get => _lastChange; set => Set(ref _lastChange, value); }
    /// <summary>The last automatic step-down can still be taken back.</summary>
    public bool CanUndo { get => _canUndo; private set => Set(ref _canUndo, value); }
    public IReadOnlyList<IncidentRow> Recent { get => _recent; private set => Set(ref _recent, value); }

    public void Update(StabilityState state, string? currentGame)
    {
        CleanTime = Format(state.CleanGameTime);
        CleanLevel = Math.Clamp(state.CleanGameTime / StabilityAdvisor.StableAfter, 0, 1);
        IncidentCount = state.Incidents.Count(i => !i.Ignored);
        CanUndo = state.LastAdjustment is not null && state.Incidents.Any(i => !i.Ignored);
        SessionCount = state.Sessions.Count;
        Recent = state.Incidents.AsEnumerable().Reverse().Take(3)
            .Select(i => new IncidentRow(i.Time.LocalDateTime.ToString("dd.MM HH:mm"), Describe(i)))
            .ToList();
        SetCurrentGame(currentGame);
    }

    public static string Describe(StabilityIncident incident)
    {
        var what = incident.Kind switch
        {
            IncidentKind.DriverReset => L.T("Ekran sürücüsü çöktü ve sıfırlandı"),
            IncidentKind.GameCrash => L.T("Oyun çöktü veya dondu"),
            IncidentKind.SystemCrash => L.T("Bilgisayar beklenmedik şekilde kapandı / mavi ekran"),
            _ => L.T("Oyunda artifact görüldü"),
        };
        if (incident.Game is { } game) what = $"{game}: {what}";
        return incident.Ignored ? what + " " + L.T("(sayılmadı)") : what;
    }

    public void SetCurrentGame(string? game) => CurrentGame = game ?? L.T("Oyun yok");

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? L.F("{0} sa {1} dk", (int)t.TotalHours, t.Minutes) : L.F("{0} dk", t.Minutes);
}
