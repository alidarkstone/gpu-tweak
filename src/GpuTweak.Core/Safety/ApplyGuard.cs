using GpuTweak.Core.Backends;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Safety;

public enum ApplyOutcome { Confirmed, RevertedByUser, RevertedByTimeout, Failed }

/// <summary>
/// "Apply → confirm within N seconds" flow. If the user does not confirm (for example because the
/// screen froze or went black), the previous settings are restored automatically.
/// </summary>
public sealed class ApplyGuard : IDisposable
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromSeconds(15);

    private readonly IGpuBackend _backend;
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private readonly object _gate = new();
    private ITimer? _timer;
    private TuningSettings? _previous;
    private DateTimeOffset _deadline;

    public ApplyGuard(IGpuBackend backend, TimeProvider? time = null, TimeSpan? window = null)
    {
        _backend = backend;
        _time = time ?? TimeProvider.System;
        _window = window ?? DefaultWindow;
    }

    /// <summary>Seconds left until automatic revert. Raised once per second, from a timer thread.</summary>
    public event Action<int>? CountdownTick;

    /// <summary>Raised when a pending apply ends. Raised from a timer thread on timeout.</summary>
    public event Action<ApplyOutcome, TuningSettings>? Completed;

    public bool IsPending { get { lock (_gate) return _previous is not null; } }

    /// <summary>Clamps, applies and starts the countdown. Returns the clamp result so the UI can show what changed.</summary>
    public ClampResult Apply(TuningSettings requested)
    {
        var clamp = SafetyClamp.Clamp(requested, _backend.Limits);
        lock (_gate)
        {
            // A second Apply during a pending one keeps the original fallback, not the unconfirmed values.
            var fallback = _previous ?? _backend.ReadCurrentSettings();
            try
            {
                _backend.Apply(clamp.Settings);
            }
            catch (Exception)
            {
                TryRestore(fallback);
                StopTimer();
                _previous = null;
                Completed?.Invoke(ApplyOutcome.Failed, fallback);
                throw;
            }

            _previous = fallback;
            _deadline = _time.GetUtcNow() + _window;
            StopTimer();
            _timer = _time.CreateTimer(_ => OnTick(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        CountdownTick?.Invoke((int)_window.TotalSeconds);
        return clamp;
    }

    public void Confirm()
    {
        TuningSettings current;
        lock (_gate)
        {
            if (_previous is null) return;
            StopTimer();
            _previous = null;
            current = _backend.ReadCurrentSettings();
        }
        Completed?.Invoke(ApplyOutcome.Confirmed, current);
    }

    public void Revert() => RevertCore(ApplyOutcome.RevertedByUser);

    private void OnTick()
    {
        int remaining;
        lock (_gate)
        {
            if (_previous is null) return;
            remaining = (int)Math.Ceiling((_deadline - _time.GetUtcNow()).TotalSeconds);
        }
        if (remaining <= 0) RevertCore(ApplyOutcome.RevertedByTimeout);
        else CountdownTick?.Invoke(remaining);
    }

    private void RevertCore(ApplyOutcome outcome)
    {
        TuningSettings restored;
        lock (_gate)
        {
            if (_previous is null) return;
            StopTimer();
            restored = _previous;
            _previous = null;
            TryRestore(restored);
        }
        Completed?.Invoke(outcome, restored);
    }

    private void TryRestore(TuningSettings settings)
    {
        try
        {
            if (settings.IsStock) _backend.ResetToStock();
            else _backend.Apply(settings);
        }
        catch (Exception)
        {
            // Last resort: stock is always the safest state.
            try { _backend.ResetToStock(); } catch (Exception) { }
        }
    }

    private void StopTimer()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose()
    {
        lock (_gate) StopTimer();
    }
}
