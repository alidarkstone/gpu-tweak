namespace GpuTweak.Core.AutoTune;

public enum StressPattern
{
    /// <summary>Full load all the time. Power-limited; measures throughput.</summary>
    Heavy,
    /// <summary>Short bursts with idle gaps. The card boosts to its highest clock, and load changes stress voltage regulation.</summary>
    Transient,
    /// <summary>Alternates heavy and transient segments. Used for every tuning step.</summary>
    Mixed,
    /// <summary>
    /// Streams large buffers through video memory and checks every value. Bandwidth-bound; Score is GB/s, so a memory
    /// overclock that makes the chips retry transfers shows up as a lower score before it shows up as errors.
    /// </summary>
    Memory,
}

/// <summary>Result of one stress run. <see cref="Score"/> is real work done per second, so clock stretching shows up as a lower score.</summary>
public sealed record StressResult(bool Passed, double Score, string? Failure)
{
    public static StressResult Fail(string reason) => new(false, 0, reason);
}

/// <summary>
/// A GPU workload that knows the correct answer to what it computes, so silent calculation errors
/// (the first sign of too little voltage) are caught long before a crash.
/// </summary>
public interface IStressTest : IDisposable
{
    /// <summary>Sizes the workload and records the reference results. Must be called at stock settings.</summary>
    Task<StressResult> PrepareAsync(CancellationToken cancellationToken);

    Task<StressResult> RunAsync(StressPattern pattern, TimeSpan duration, CancellationToken cancellationToken);
}
