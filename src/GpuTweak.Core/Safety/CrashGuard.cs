using System.Text.Json;

namespace GpuTweak.Core.Safety;

/// <summary>
/// Detects that the previous session ended without a clean exit while custom settings were active
/// (driver crash, black screen, hard reset). In that case the app must start at stock and must not auto-apply.
/// </summary>
public sealed class CrashGuard
{
    private readonly string _path;

    public CrashGuard(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "session.lock");
    }

    private sealed record SessionState(bool CustomApplied, string? ProfileName, DateTimeOffset StartedAt);

    /// <summary>Call once at startup. Returns the profile name that was active when the last session crashed, or null.</summary>
    public CrashReport? BeginSession()
    {
        CrashReport? report = null;
        if (File.Exists(_path))
        {
            try
            {
                var previous = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(_path));
                if (previous is { CustomApplied: true })
                    report = new CrashReport(previous.ProfileName, previous.StartedAt);
            }
            catch (JsonException)
            {
                // A torn write is itself a sign of an abrupt end; be conservative.
                report = new CrashReport(null, null);
            }
        }
        Write(new SessionState(false, null, DateTimeOffset.Now));
        return report;
    }

    public void MarkCustomApplied(string? profileName) => Write(new SessionState(true, profileName, DateTimeOffset.Now));

    public void MarkStock() => Write(new SessionState(false, null, DateTimeOffset.Now));

    public void EndSession()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    private void Write(SessionState state)
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, _path, overwrite: true);
    }
}

public sealed record CrashReport(string? ProfileName, DateTimeOffset? SessionStartedAt);
