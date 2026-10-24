namespace GpuTweak.Core.Backends;

public static class GpuBackendFactory
{
    /// <summary>Picks the first backend that finds a card: NVIDIA (NVML) first, then AMD (ADLX).</summary>
    public static IGpuBackend? Create(bool demo)
    {
        if (demo) return new DemoBackend();
        return (IGpuBackend?)NvmlBackend.TryCreate() ?? AdlxBackend.TryCreate();
    }
}
