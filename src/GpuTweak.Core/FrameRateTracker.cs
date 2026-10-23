namespace GpuTweak.Core;

public sealed record FrameRate(double Current, double Min, double Average, double Max);

/// <summary>
/// Turns present (frame shown) timestamps per process into frames per second. Current is the last second;
/// min / avg / max are over the one-second values since the tracked process last changed.
/// </summary>
public sealed class FrameRateTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<int, Queue<double>> _presents = new();
    private readonly RunningStats _stats = new();
    private int? _trackedProcess;

    /// <summary>Called from the event thread for every frame a process presents. Time in seconds.</summary>
    public void OnPresent(int processId, double time)
    {
        lock (_gate)
        {
            if (!_presents.TryGetValue(processId, out var queue)) _presents[processId] = queue = new Queue<double>();
            queue.Enqueue(time);
            while (queue.Count > 0 && queue.Peek() < time - 2) queue.Dequeue();
        }
    }

    /// <summary>Call about once a second with the foreground process. Returns null when it shows no frames.</summary>
    public FrameRate? Sample(int? processId, double now)
    {
        lock (_gate)
        {
            if (processId != _trackedProcess)
            {
                _trackedProcess = processId;
                _stats.Reset();
            }
            foreach (var pid in _presents.Where(p => p.Value.Count == 0 || p.Value.Last() < now - 5).Select(p => p.Key).ToList())
                _presents.Remove(pid);

            if (processId is not { } id || !_presents.TryGetValue(id, out var queue) || queue.Count == 0) return null;
            // Real-time events arrive in batches up to a second late, so count the last second that has arrived.
            var end = queue.Last();
            if (end < now - 3) return null;
            var frames = queue.Count(t => t > end - 1);
            _stats.Add(frames);
            return new FrameRate(frames, _stats.Min ?? frames, _stats.Average ?? frames, _stats.Max ?? frames);
        }
    }
}
