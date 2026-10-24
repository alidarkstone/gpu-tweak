using System.Runtime.InteropServices;

namespace GpuTweak.Core.Backends;

/// <summary>
/// EXPERIMENTAL, read-only: memory (VRAM junction) temperature through NVAPI's undocumented thermal sensor call.
/// GeForce cards report "N/A" for memory temperature through NVML, while tools like HWiNFO and LibreHardwareMonitor
/// read it this way. Nothing is ever written to the card. Which slot holds the memory sensor differs per
/// generation; the slots follow LibreHardwareMonitor (Hardware/Gpu/NvidiaGpu.cs):
/// RTX 50 → 2, RTX 40 → 7, older → 9. Any failure turns the reader off for the rest of the run.
/// </summary>
internal sealed unsafe class NvApiThermal
{
    private const uint IdInitialize = 0x0150E828;
    private const uint IdEnumPhysicalGpus = 0xE5AC921F;
    private const uint IdGetThermalSensors = 0x65FE3AAD;
    private const int MaxPhysicalGpus = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThermalSensors
    {
        public uint Version;
        public uint Mask;
        public fixed int Reserved[8];
        /// <summary>Degrees Celsius × 256.</summary>
        public fixed int Temperatures[32];
    }

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr QueryInterface(uint id);

    private readonly delegate* unmanaged[Cdecl]<IntPtr, ThermalSensors*, int> _getThermalSensors;
    private readonly IntPtr _gpu;
    private readonly uint _mask;
    private readonly int _slot;
    private bool _failed;

    private NvApiThermal(delegate* unmanaged[Cdecl]<IntPtr, ThermalSensors*, int> getThermalSensors, IntPtr gpu, uint mask, int slot)
    {
        _getThermalSensors = getThermalSensors;
        _gpu = gpu;
        _mask = mask;
        _slot = slot;
    }

    /// <summary>Null when NVAPI or the call is missing, or when there is more than one NVIDIA GPU (the right one can't be told apart).</summary>
    public static NvApiThermal? TryCreate(string gpuName)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        try
        {
            var initialize = (delegate* unmanaged[Cdecl]<int>)QueryInterface(IdInitialize);
            var enumGpus = (delegate* unmanaged[Cdecl]<IntPtr*, int*, int>)QueryInterface(IdEnumPhysicalGpus);
            var getSensors = (delegate* unmanaged[Cdecl]<IntPtr, ThermalSensors*, int>)QueryInterface(IdGetThermalSensors);
            if (initialize == null || enumGpus == null || getSensors == null || initialize() != 0) return null;

            var handles = stackalloc IntPtr[MaxPhysicalGpus];
            var count = 0;
            if (enumGpus(handles, &count) != 0 || count != 1) return null;
            var gpu = handles[0];

            // Find the widest sensor mask the driver accepts (bits 0..n all set).
            uint mask = 0;
            for (var bit = 0; bit < 32; bit++)
            {
                var candidate = (bit == 31 ? uint.MaxValue : (1u << (bit + 1)) - 1);
                if (!Read(getSensors, gpu, candidate, out _)) break;
                mask = candidate;
            }
            var slot = SlotFor(gpuName);
            if (mask == 0 || (mask & (1u << slot)) == 0) return null;
            return new NvApiThermal(getSensors, gpu, mask, slot);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    public static int SlotFor(string gpuName) =>
        gpuName.Contains("RTX 50", StringComparison.OrdinalIgnoreCase) ? 2 :
        gpuName.Contains("RTX 40", StringComparison.OrdinalIgnoreCase) ? 7 : 9;

    /// <summary>Memory temperature in °C, or null if the reading failed or is implausible.</summary>
    public double? ReadMemoryTemperature()
    {
        if (_failed) return null;
        if (!Read(_getThermalSensors, _gpu, _mask, out var sensors))
        {
            _failed = true;
            return null;
        }
        var celsius = sensors.Temperatures[_slot] / 256.0;
        return celsius is > 5 and < 130 ? Math.Round(celsius, 1) : null;
    }

    private static bool Read(delegate* unmanaged[Cdecl]<IntPtr, ThermalSensors*, int> call, IntPtr gpu, uint mask, out ThermalSensors sensors)
    {
        sensors = default;
        sensors.Version = (uint)sizeof(ThermalSensors) | (2u << 16);
        sensors.Mask = mask;
        fixed (ThermalSensors* p = &sensors) return call(gpu, p) == 0;
    }
}
