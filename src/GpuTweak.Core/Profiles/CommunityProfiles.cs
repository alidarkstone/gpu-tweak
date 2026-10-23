using System.Text.Json;
using GpuTweak.Core.Models;

namespace GpuTweak.Core.Profiles;

public enum CommunityProfileKind { Undervolt, Overclock, Efficiency }

/// <summary>A setting someone shared publicly. Always carries the source it came from.</summary>
public sealed record CommunityProfile(
    string GpuMatch,
    string Title,
    CommunityProfileKind Kind,
    TuningSettings Settings,
    string SourceUrl,
    string SourceTitle,
    string? Notes = null,
    string? RetrievedAt = null)
{
    /// <summary>
    /// Every chip is different, so suggestions are applied at half strength first.
    /// Offsets are halved; power limit and clock cap stay as given because they only reduce load.
    /// </summary>
    public TuningSettings ConservativeStart() => Settings with
    {
        CoreOffsetMHz = Settings.CoreOffsetMHz / 2,
        MemoryOffsetMHz = Settings.MemoryOffsetMHz / 2,
    };
}

public sealed class CommunityProfileDb
{
    private sealed record DbFile(int SchemaVersion, List<CommunityProfile> Entries);

    public IReadOnlyList<CommunityProfile> Entries { get; }
    public int RejectedCount { get; }

    private CommunityProfileDb(IReadOnlyList<CommunityProfile> entries, int rejected)
    {
        Entries = entries;
        RejectedCount = rejected;
    }

    public static CommunityProfileDb Parse(string json)
    {
        var file = JsonSerializer.Deserialize<DbFile>(json, JsonDefaults.Options) ?? new DbFile(1, []);
        var valid = file.Entries.Where(IsValid).ToList();
        return new CommunityProfileDb(valid, file.Entries.Count - valid.Count);
    }

    /// <summary>The database compiled into the exe.</summary>
    public static CommunityProfileDb LoadEmbedded()
    {
        using var stream = typeof(CommunityProfileDb).Assembly.GetManifestResourceStream("GpuTweak.Core.community-profiles.json");
        if (stream is null) return new CommunityProfileDb([], 0);
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    /// <summary>Uses an updated community-profiles.json next to the exe when present, otherwise the embedded one.</summary>
    public static CommunityProfileDb Load(string dataDirectory)
    {
        var overridePath = Path.Combine(dataDirectory, "community-profiles.json");
        if (File.Exists(overridePath))
        {
            try { return Parse(File.ReadAllText(overridePath)); }
            catch (JsonException) { }
        }
        return LoadEmbedded();
    }

    public IReadOnlyList<CommunityProfile> ForGpu(GpuInfo gpu)
    {
        var name = Normalize(gpu.Name);
        return Entries
            .Where(e => name.Contains(Normalize(e.GpuMatch), StringComparison.Ordinal))
            // Longest match first, so "RTX 5070 Ti" wins over "RTX 5070" for a Ti card.
            .OrderByDescending(e => e.GpuMatch.Length)
            .ToList();
    }

    /// <summary>"RTX 5070" must not match "RTX 5070 Ti"; matching is done on whole words.</summary>
    private static string Normalize(string value) =>
        " " + string.Join(' ', value.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries)) + " ";

    private static bool IsValid(CommunityProfile entry) =>
        !string.IsNullOrWhiteSpace(entry.GpuMatch) &&
        !string.IsNullOrWhiteSpace(entry.Title) &&
        Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps;
}
