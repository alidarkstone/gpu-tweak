namespace GpuTweak.Core.Backends;

public static class GpuBackendFactory
{
    /// <summary>Picks the first backend that finds a card. AMD (ADLX) comes in a later version.</summary>
    public static IGpuBackend? Create(bool demo)
    {
        if (demo) return new DemoBackend();
        return NvmlBackend.TryCreate();
    }
}
