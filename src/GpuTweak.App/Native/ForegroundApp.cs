using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GpuTweak.App.Native;

internal static class ForegroundApp
{
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    /// <summary>Process id of the window in front, or null for this app.</summary>
    public static int? ProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return pid == 0 || pid == Environment.ProcessId ? null : (int)pid;
    }

    /// <summary>Process name of the window in front (the game while playing), or null for desktop / this app.</summary>
    public static string? ProcessName()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
            if (pid == 0 || pid == Environment.ProcessId) return null;
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            return name is "explorer" or "ShellExperienceHost" or "SearchHost" ? null : name;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
