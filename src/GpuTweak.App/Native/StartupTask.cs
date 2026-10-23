using System.Diagnostics;

namespace GpuTweak.App.Native;

/// <summary>
/// Starts GPU Tweak at Windows sign-in. A Task Scheduler task is used instead of the Run registry key,
/// because the app needs administrator rights and Run-key entries are not allowed to elevate.
/// </summary>
internal static class StartupTask
{
    private const string TaskName = "GPU Tweak";
    public const string StartupArgument = "--startup";

    public static bool IsEnabled() => Run("/Query", "/TN", TaskName) == 0;

    public static bool Enable()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Exe yolu bulunamadı.");
        return Run("/Create", "/TN", TaskName, "/TR", $"\"{exe}\" {StartupArgument}", "/SC", "ONLOGON", "/RL", "HIGHEST", "/F") == 0;
    }

    public static bool Disable() => Run("/Delete", "/TN", TaskName, "/F") == 0;

    private static int Run(params string[] args)
    {
        var info = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) info.ArgumentList.Add(a);
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit(10_000);
        return process.ExitCode;
    }
}
