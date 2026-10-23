using GpuTweak.Core.Models;

namespace GpuTweak.Core.Backends;

public interface IGpuBackend : IDisposable
{
    GpuInfo Info { get; }
    TuningLimits Limits { get; }

    GpuTelemetry ReadTelemetry();
    TuningSettings ReadCurrentSettings();

    /// <summary>Writes settings to the card. Callers must clamp first (see <see cref="Safety.SafetyClamp"/>).</summary>
    void Apply(TuningSettings settings);

    void ResetToStock();
}

public sealed class GpuBackendException(string message, Exception? inner = null) : Exception(message, inner);
