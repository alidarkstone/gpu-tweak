using GpuTweak.Core.Models;

namespace GpuTweak.Core.Stability;

/// <summary>
/// Turns telemetry samples into game sessions: a session starts after the GPU has been busy for a while
/// with some program in the foreground, and ends after the load has been low for a while.
/// </summary>
public sealed class GameSessionDetector(Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan StartAfter = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan EndAfter = TimeSpan.FromSeconds(60);
    private const int BusyLoad = 60, IdleLoad = 30;

    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private DateTimeOffset? _busySince, _idleSince;
    private string? _candidate;
    private Accumulator? _session;

    public string? CurrentGame => _session?.Game;

    /// <summary>Counts a problem against the session that is running now, so it does not add to clean time.</summary>
    public void MarkIncident() => _session?.MarkIncident();

    /// <summary>Returns a finished session when one just ended, otherwise null.</summary>
    public GameSession? Feed(GpuTelemetry sample, string? foregroundProcess, TuningSettings settings)
    {
        var now = _now();
        var load = sample.LoadPercent ?? 0;

        if (_session is null)
        {
            if (load >= BusyLoad && !string.IsNullOrEmpty(foregroundProcess))
            {
                if (_candidate != foregroundProcess) { _candidate = foregroundProcess; _busySince = now; }
                if (now - _busySince >= StartAfter) _session = new Accumulator(foregroundProcess, _busySince!.Value);
            }
            else
            {
                _candidate = null;
                _busySince = null;
            }
            if (_session is null) return null;
        }

        _session.Add(sample, settings);
        if (load < IdleLoad)
        {
            _idleSince ??= now;
            if (now - _idleSince >= EndAfter) return End(_idleSince.Value);
        }
        else
        {
            _idleSince = null;
        }
        return null;
    }

    /// <summary>Closes an open session (app exit, or an incident that should be attached to it).</summary>
    public GameSession? Flush() => _session is null ? null : End(_now());

    private GameSession End(DateTimeOffset end)
    {
        var session = _session!.ToSession(end);
        _session = null;
        _candidate = null;
        _busySince = _idleSince = null;
        return session;
    }

    private sealed class Accumulator(string game, DateTimeOffset start)
    {
        private double _maxTemp, _powerSum, _clockSum;
        private int _count, _incidents;
        private TuningSettings _settings = TuningSettings.Stock;
        public string Game { get; } = game;

        public void MarkIncident() => _incidents++;

        public void Add(GpuTelemetry s, TuningSettings settings)
        {
            _settings = settings;
            if ((s.LoadPercent ?? 0) < IdleLoad) return;
            _maxTemp = Math.Max(_maxTemp, s.TemperatureC ?? 0);
            _powerSum += s.PowerW ?? 0;
            _clockSum += s.CoreClockMHz ?? 0;
            _count++;
        }

        public GameSession ToSession(DateTimeOffset end) => new(
            Game, start, end, _maxTemp,
            _count == 0 ? 0 : _powerSum / _count,
            _count == 0 ? 0 : (int)(_clockSum / _count),
            _settings, _incidents);
    }
}
