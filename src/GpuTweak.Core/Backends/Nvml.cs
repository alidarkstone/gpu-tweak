using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace GpuTweak.Core.Backends;

/// <summary>
/// Minimal P/Invoke surface for NVIDIA's official NVML library (nvml.dll ships with the driver).
/// Only documented entry points are used, so a driver update cannot silently break them.
/// </summary>
internal static class Nvml
{
    private const string Lib = "nvml";

    static Nvml()
    {
        NativeLibrary.SetDllImportResolver(typeof(Nvml).Assembly, Resolve);
    }

    private static IntPtr Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        if (name != Lib) return IntPtr.Zero;

        string[] candidates = OperatingSystem.IsWindows()
            ? [
                "nvml.dll",
                Path.Combine(Environment.SystemDirectory, "nvml.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll"),
              ]
            : ["libnvidia-ml.so.1", "libnvidia-ml.so"];

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle)) return handle;
        }
        return IntPtr.Zero;
    }

    public enum Return
    {
        Success = 0,
        Uninitialized = 1,
        InvalidArgument = 2,
        NotSupported = 3,
        NoPermission = 4,
        NotFound = 6,
        Unknown = 999,
    }

    public enum ClockType { Graphics = 0, Sm = 1, Mem = 2, Video = 3 }

    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>nvmlClockOffset_v1_t (driver R555+). The older Gpc/MemClkVfOffset calls are not supported on newer cards such as the RTX 50 series.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ClockOffset
    {
        public uint Version;
        public ClockType Type;
        public int PState;
        public int OffsetMHz;
        public int MinOffsetMHz;
        public int MaxOffsetMHz;

        public static ClockOffset For(ClockType type) => new()
        {
            // NVML_STRUCT_VERSION(ClockOffset, 1) = sizeof(struct) | (1 << 24)
            Version = (uint)Marshal.SizeOf<ClockOffset>() | (1u << 24),
            Type = type,
            PState = 0, // P0: the full-performance state that games run in
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [DllImport(Lib, EntryPoint = "nvmlInit_v2")] public static extern Return Init();
    [DllImport(Lib, EntryPoint = "nvmlShutdown")] public static extern Return Shutdown();
    [DllImport(Lib, EntryPoint = "nvmlErrorString")] private static extern IntPtr ErrorStringPtr(Return result);
    [DllImport(Lib, EntryPoint = "nvmlSystemGetDriverVersion")] private static extern Return SystemGetDriverVersion(byte[] version, uint length);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetCount_v2")] public static extern Return DeviceGetCount(out uint count);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")] public static extern Return DeviceGetHandleByIndex(uint index, out IntPtr device);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetName")] private static extern Return DeviceGetName(IntPtr device, byte[] name, uint length);

    /// <summary>nvmlFieldValue_t. Value is a union; every field read here is an unsigned int in its low 4 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FieldValue
    {
        public uint FieldId;
        public uint ScopeId;
        public long Timestamp;
        public long LatencyUsec;
        public int ValueType;
        public Return Result;
        public ulong Value;
    }

    public const uint FieldMemoryTemperature = 82; // NVML_FI_DEV_MEMORY_TEMP

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetFieldValues")] public static extern Return DeviceGetFieldValues(IntPtr device, int count, [In, Out] FieldValue[] values);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetTemperature")] public static extern Return DeviceGetTemperature(IntPtr device, int sensor, out uint tempC);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetClockInfo")] public static extern Return DeviceGetClockInfo(IntPtr device, ClockType type, out uint mhz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetMaxClockInfo")] public static extern Return DeviceGetMaxClockInfo(IntPtr device, ClockType type, out uint mhz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerUsage")] public static extern Return DeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetMemoryInfo")] public static extern Return DeviceGetMemoryInfo(IntPtr device, out Memory memory);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetUtilizationRates")] public static extern Return DeviceGetUtilizationRates(IntPtr device, out Utilization utilization);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetFanSpeed")] public static extern Return DeviceGetFanSpeed(IntPtr device, out uint percent);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerManagementLimit")] public static extern Return DeviceGetPowerManagementLimit(IntPtr device, out uint milliwatts);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerManagementDefaultLimit")] public static extern Return DeviceGetPowerManagementDefaultLimit(IntPtr device, out uint milliwatts);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetPowerManagementLimitConstraints")] public static extern Return DeviceGetPowerManagementLimitConstraints(IntPtr device, out uint minMilliwatts, out uint maxMilliwatts);
    [DllImport(Lib, EntryPoint = "nvmlDeviceSetPowerManagementLimit")] public static extern Return DeviceSetPowerManagementLimit(IntPtr device, uint milliwatts);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetGpcClkVfOffset")] public static extern Return DeviceGetGpcClkVfOffset(IntPtr device, out int offsetMHz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceSetGpcClkVfOffset")] public static extern Return DeviceSetGpcClkVfOffset(IntPtr device, int offsetMHz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetGpcClkMinMaxVfOffset")] public static extern Return DeviceGetGpcClkMinMaxVfOffset(IntPtr device, out int minMHz, out int maxMHz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetMemClkVfOffset")] public static extern Return DeviceGetMemClkVfOffset(IntPtr device, out int offsetMHz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceSetMemClkVfOffset")] public static extern Return DeviceSetMemClkVfOffset(IntPtr device, int offsetMHz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetMemClkMinMaxVfOffset")] public static extern Return DeviceGetMemClkMinMaxVfOffset(IntPtr device, out int minMHz, out int maxMHz);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetClockOffsets")] public static extern Return DeviceGetClockOffsets(IntPtr device, ref ClockOffset info);
    [DllImport(Lib, EntryPoint = "nvmlDeviceSetClockOffsets")] public static extern Return DeviceSetClockOffsets(IntPtr device, ref ClockOffset info);

    [DllImport(Lib, EntryPoint = "nvmlDeviceSetGpuLockedClocks")] public static extern Return DeviceSetGpuLockedClocks(IntPtr device, uint minMHz, uint maxMHz);
    [DllImport(Lib, EntryPoint = "nvmlDeviceResetGpuLockedClocks")] public static extern Return DeviceResetGpuLockedClocks(IntPtr device);

    [DllImport(Lib, EntryPoint = "nvmlDeviceGetNumFans")] public static extern Return DeviceGetNumFans(IntPtr device, out uint count);
    [DllImport(Lib, EntryPoint = "nvmlDeviceGetMinMaxFanSpeed")] public static extern Return DeviceGetMinMaxFanSpeed(IntPtr device, out uint min, out uint max);
    [DllImport(Lib, EntryPoint = "nvmlDeviceSetFanSpeed_v2")] public static extern Return DeviceSetFanSpeed(IntPtr device, uint fan, uint percent);
    [DllImport(Lib, EntryPoint = "nvmlDeviceSetDefaultFanSpeed_v2")] public static extern Return DeviceSetDefaultFanSpeed(IntPtr device, uint fan);

    public static string ErrorString(Return result)
    {
        try { return Marshal.PtrToStringAnsi(ErrorStringPtr(result)) ?? result.ToString(); }
        catch (EntryPointNotFoundException) { return result.ToString(); }
    }

    public static string GetName(IntPtr device)
    {
        var buffer = new byte[96];
        return DeviceGetName(device, buffer, (uint)buffer.Length) == Return.Success ? Decode(buffer) : "NVIDIA GPU";
    }

    public static string GetDriverVersion()
    {
        var buffer = new byte[80];
        return SystemGetDriverVersion(buffer, (uint)buffer.Length) == Return.Success ? Decode(buffer) : "?";
    }

    private static string Decode(byte[] buffer)
    {
        var length = Array.IndexOf(buffer, (byte)0);
        return Encoding.ASCII.GetString(buffer, 0, length < 0 ? buffer.Length : length);
    }
}
