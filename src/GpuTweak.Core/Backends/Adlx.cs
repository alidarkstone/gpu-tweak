using System.Runtime.InteropServices;

namespace GpuTweak.Core.Backends;

/// <summary>
/// Minimal binding to AMD's official ADLX library (amdadlx64.dll ships with the Radeon driver).
/// ADLX objects are C++ interfaces; each call goes through the object's vtable. The slot numbers below are
/// taken from the C headers of the ADLX SDK (github.com/GPUOpen-LibrariesAndSDKs/ADLX, SDK/Include).
/// </summary>
internal static unsafe class Adlx
{
    public const int Ok = 0, AlreadyEnabled = 1, AlreadyInitialized = 2, NotSupported = 12;
    public static bool Succeeded(int result) => result is Ok or AlreadyEnabled or AlreadyInitialized;

    public static string ErrorString(int result) => result switch
    {
        3 => "FAIL", 4 => "INVALID_ARGS", 5 => "BAD_VER", 6 => "UNKNOWN_INTERFACE", 7 => "TERMINATED",
        8 => "ADL_INIT_ERROR", 9 => "NOT_FOUND", 10 => "INVALID_OBJECT", 12 => "NOT_SUPPORTED",
        13 => "PENDING_OPERATION", 14 => "GPU_INACTIVE", 15 => "GPU_IN_USE", 16 => "TIMEOUT", 17 => "NOT_ACTIVE",
        18 => "RESET_NEEDED", _ => $"#{result}",
    };

    [StructLayout(LayoutKind.Sequential)]
    public struct IntRange { public int Min, Max, Step; }

    // ---- Library entry points ----

    private static IntPtr _library;
    private static delegate* unmanaged[Cdecl]<ulong*, int> _queryFullVersion;
    private static delegate* unmanaged[Cdecl]<ulong, IntPtr*, int> _initialize;
    private static delegate* unmanaged[Cdecl]<ulong, IntPtr*, int> _initializeIncompatible;
    private static delegate* unmanaged[Cdecl]<int> _terminate;

    /// <summary>Loads the library and returns the IADLXSystem pointer, or zero when there is no AMD driver.</summary>
    public static IntPtr Initialize()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        if (_library == IntPtr.Zero)
        {
            if (!NativeLibrary.TryLoad("amdadlx64.dll", out _library) &&
                !NativeLibrary.TryLoad(Path.Combine(Environment.SystemDirectory, "amdadlx64.dll"), out _library))
                return IntPtr.Zero;
            if (!NativeLibrary.TryGetExport(_library, "ADLXInitialize", out var init) ||
                !NativeLibrary.TryGetExport(_library, "ADLXTerminate", out var term))
                return IntPtr.Zero;
            _initialize = (delegate* unmanaged[Cdecl]<ulong, IntPtr*, int>)init;
            _terminate = (delegate* unmanaged[Cdecl]<int>)term;
            if (NativeLibrary.TryGetExport(_library, "ADLXInitializeWithIncompatibleDriver", out var incompatible))
                _initializeIncompatible = (delegate* unmanaged[Cdecl]<ulong, IntPtr*, int>)incompatible;
            if (NativeLibrary.TryGetExport(_library, "ADLXQueryFullVersion", out var query))
                _queryFullVersion = (delegate* unmanaged[Cdecl]<ulong*, int>)query;
        }

        // Ask for exactly the version the installed driver has, so an older driver does not refuse a newer SDK number.
        ulong version = (1UL << 48) | (0UL << 32);
        if (_queryFullVersion != null)
        {
            ulong full;
            if (Succeeded(_queryFullVersion(&full)) && full != 0) version = full;
        }
        IntPtr system;
        if (Succeeded(_initialize(version, &system)) && system != IntPtr.Zero) return system;
        if (_initializeIncompatible != null && Succeeded(_initializeIncompatible(version, &system)) && system != IntPtr.Zero) return system;
        return IntPtr.Zero;
    }

    public static void Terminate()
    {
        if (_terminate != null) _terminate();
    }

    // ---- Vtable calls (x64 has a single calling convention, so __stdcall needs no special handling) ----

    private static IntPtr Slot(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];

    public static int Release(IntPtr obj) => obj == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, int>)Slot(obj, 1))(obj);

    public static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        Release(obj);
        obj = IntPtr.Zero;
    }

    /// <summary>Interface ids are the interface names as wide strings, e.g. "IADLXManualGraphicsTuning2".</summary>
    public static IntPtr QueryInterface(IntPtr obj, string iid, int slot = 2)
    {
        if (obj == IntPtr.Zero) return IntPtr.Zero;
        IntPtr result;
        fixed (char* id = iid)
            return Succeeded(((delegate* unmanaged<IntPtr, char*, IntPtr*, int>)Slot(obj, slot))(obj, id, &result)) ? result : IntPtr.Zero;
    }

    public static int Call(IntPtr obj, int slot) => ((delegate* unmanaged<IntPtr, int>)Slot(obj, slot))(obj);

    public static int GetPtr(IntPtr obj, int slot, out IntPtr value)
    {
        IntPtr v;
        var r = ((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, &v);
        value = Succeeded(r) ? v : IntPtr.Zero;
        return r;
    }

    public static int GetInt(IntPtr obj, int slot, out int value)
    {
        int v;
        var r = ((delegate* unmanaged<IntPtr, int*, int>)Slot(obj, slot))(obj, &v);
        value = v;
        return r;
    }

    public static int? TryInt(IntPtr obj, int slot) => obj != IntPtr.Zero && Succeeded(GetInt(obj, slot, out var v)) ? v : null;

    public static double? TryDouble(IntPtr obj, int slot)
    {
        if (obj == IntPtr.Zero) return null;
        double v;
        return Succeeded(((delegate* unmanaged<IntPtr, double*, int>)Slot(obj, slot))(obj, &v)) ? v : null;
    }

    public static int SetInt(IntPtr obj, int slot, int value) => ((delegate* unmanaged<IntPtr, int, int>)Slot(obj, slot))(obj, value);

    public static int SetBool(IntPtr obj, int slot, bool value) => ((delegate* unmanaged<IntPtr, byte, int>)Slot(obj, slot))(obj, value ? (byte)1 : (byte)0);

    public static bool? TryBool(IntPtr obj, int slot)
    {
        if (obj == IntPtr.Zero) return null;
        byte v;
        return Succeeded(((delegate* unmanaged<IntPtr, byte*, int>)Slot(obj, slot))(obj, &v)) ? v != 0 : null;
    }

    public static IntRange? TryRange(IntPtr obj, int slot)
    {
        if (obj == IntPtr.Zero) return null;
        IntRange v;
        return Succeeded(((delegate* unmanaged<IntPtr, IntRange*, int>)Slot(obj, slot))(obj, &v)) ? v : null;
    }

    public static bool TryRanges(IntPtr obj, int slot, out IntRange first, out IntRange second)
    {
        IntRange a = default, b = default;
        var ok = obj != IntPtr.Zero && Succeeded(((delegate* unmanaged<IntPtr, IntRange*, IntRange*, int>)Slot(obj, slot))(obj, &a, &b));
        first = a;
        second = b;
        return ok;
    }

    public static bool TryMinMax(IntPtr obj, int slot, out int min, out int max)
    {
        int a = 0, b = 0;
        var ok = obj != IntPtr.Zero && Succeeded(((delegate* unmanaged<IntPtr, int*, int*, int>)Slot(obj, slot))(obj, &a, &b));
        min = a;
        max = b;
        return ok;
    }

    public static string? TryString(IntPtr obj, int slot)
    {
        if (obj == IntPtr.Zero) return null;
        sbyte* v;
        return Succeeded(((delegate* unmanaged<IntPtr, sbyte**, int>)Slot(obj, slot))(obj, &v)) && v != null ? Marshal.PtrToStringUTF8((IntPtr)v) : null;
    }

    /// <summary>Methods of the services objects that take the GPU as first argument.</summary>
    public static int GpuGetPtr(IntPtr obj, int slot, IntPtr gpu, out IntPtr value)
    {
        IntPtr v;
        var r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(obj, slot))(obj, gpu, &v);
        value = Succeeded(r) ? v : IntPtr.Zero;
        return r;
    }

    public static bool GpuIsSupported(IntPtr obj, int slot, IntPtr gpu)
    {
        byte v = 0;
        return Succeeded(((delegate* unmanaged<IntPtr, IntPtr, byte*, int>)Slot(obj, slot))(obj, gpu, &v)) && v != 0;
    }

    public static int GpuCall(IntPtr obj, int slot, IntPtr gpu) => ((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, gpu);

    // ---- Lists (IADLXList): Size = 3, typed At = 11 ----

    public static uint ListSize(IntPtr list) => ((delegate* unmanaged<IntPtr, uint>)Slot(list, 3))(list);

    public static IntPtr ListAt(IntPtr list, uint index)
    {
        IntPtr v;
        return Succeeded(((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(list, 11))(list, index, &v)) ? v : IntPtr.Zero;
    }

    public static int CallWithPtr(IntPtr obj, int slot, IntPtr arg) => ((delegate* unmanaged<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, arg);

    public static int CallWithPtrOutInt(IntPtr obj, int slot, IntPtr arg, out int value)
    {
        int v;
        var r = ((delegate* unmanaged<IntPtr, IntPtr, int*, int>)Slot(obj, slot))(obj, arg, &v);
        value = v;
        return r;
    }

    // ---- Vtable slots, from the SDK's C headers ----

    public static class System
    {
        public const int GetGPUs = 1, GetGPUTuningServices = 8, GetPerformanceMonitoringServices = 9;
    }

    public static class Gpu
    {
        public const int VendorId = 3, Type = 5, Name = 7, TotalVRAM = 11, DeviceId = 14, UniqueId = 18;
        public const int TypeDiscrete = 2;
    }

    public static class TuningServices
    {
        public const int IsAtFactory = 4, ResetToFactory = 5;
        public const int IsSupportedManualGFXTuning = 8, IsSupportedManualVRAMTuning = 9, IsSupportedManualFanTuning = 10, IsSupportedManualPowerTuning = 11;
        public const int GetManualGFXTuning = 14, GetManualVRAMTuning = 15, GetManualFanTuning = 16, GetManualPowerTuning = 17;
    }

    /// <summary>IADLXManualGraphicsTuning2 (RDNA and newer). From RDNA4 on, max frequency and voltage are offsets.</summary>
    public static class GfxTuning2
    {
        public const string Iid = "IADLXManualGraphicsTuning2", Iid2_1 = "IADLXManualGraphicsTuning2_1";
        public const int GetMaxFrequencyRange = 6, GetMaxFrequency = 7, SetMaxFrequency = 8;
        public const int GetVoltageRange = 9, GetVoltage = 10, SetVoltage = 11;
        // IADLXManualGraphicsTuning2_1
        public const int GetMaxFrequencyDefault = 13, GetVoltageDefault = 14;
    }

    /// <summary>IADLXManualVRAMTuning2 (RDNA and newer).</summary>
    public static class VramTuning2
    {
        public const string Iid = "IADLXManualVRAMTuning2", Iid2_1 = "IADLXManualVRAMTuning2_1";
        public const int GetMaxFrequencyRange = 7, GetMaxFrequency = 8, SetMaxFrequency = 9;
        public const int GetMaxFrequencyDefault = 10;
    }

    /// <summary>IADLXManualPowerTuning: the power limit is a percentage relative to the card's default.</summary>
    public static class PowerTuning
    {
        public const string Iid = "IADLXManualPowerTuning";
        public const int GetPowerLimitRange = 3, GetPowerLimit = 4, SetPowerLimit = 5;
    }

    public static class FanTuning
    {
        public const string Iid = "IADLXManualFanTuning";
        public const int GetRanges = 3, GetStates = 4, GetEmptyStates = 5, IsValidStates = 6, SetStates = 7;
        public const int IsSupportedZeroRPM = 8, SetZeroRPMState = 10;
        // IADLXManualFanTuningState
        public const int StateSetFanSpeed = 4, StateSetTemperature = 6;
    }

    public static class Monitoring
    {
        public const int GetCurrentGPUMetrics = 18, GetSupportedGPUMetrics = 21;
    }

    /// <summary>IADLXGPUMetricsSupport range getters.</summary>
    public static class MetricsSupport
    {
        public const int GetGPUFanSpeedRange = 19;
    }

    /// <summary>IADLXGPUMetrics (and IADLXGPUMetrics1 for memory temperature).</summary>
    public static class Metrics
    {
        public const string Iid1 = "IADLXGPUMetrics1";
        public const int Usage = 4, ClockSpeed = 5, VramClockSpeed = 6, Temperature = 7, HotspotTemperature = 8;
        public const int Power = 9, TotalBoardPower = 10, FanSpeed = 11, Vram = 12, Voltage = 13;
        public const int MemoryTemperature = 15;
    }
}
