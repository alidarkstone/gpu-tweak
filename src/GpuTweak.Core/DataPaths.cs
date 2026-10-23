namespace GpuTweak.Core;

public static class DataPaths
{
    /// <summary>
    /// Portable mode: a "GpuTweakData" folder next to the exe. Falls back to %LOCALAPPDATA%
    /// when the exe sits somewhere read-only (e.g. Program Files or a locked USB stick).
    /// </summary>
    public static string Resolve()
    {
        var portable = Path.Combine(AppContext.BaseDirectory, "GpuTweakData");
        try
        {
            Directory.CreateDirectory(portable);
            var probe = Path.Combine(portable, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return portable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpuTweak");
        }
    }
}
