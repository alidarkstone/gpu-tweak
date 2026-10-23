using GpuTweak.Core.Models;
using GpuTweak.Core.Profiles;

namespace GpuTweak.Core.Tests;

public class CommunityProfileTests
{
    private const string Json = """
    {
      "schemaVersion": 1,
      "entries": [
        { "gpuMatch": "RTX 5070", "title": "base", "kind": "undervolt", "settings": { "coreOffsetMHz": 200 }, "sourceUrl": "https://example.com/a", "sourceTitle": "a" },
        { "gpuMatch": "RTX 5070 Ti", "title": "ti", "kind": "overclock", "settings": { "coreOffsetMHz": 300, "memoryOffsetMHz": 1000 }, "sourceUrl": "https://example.com/b", "sourceTitle": "b" },
        { "gpuMatch": "RTX 5080", "title": "no source", "kind": "overclock", "settings": {}, "sourceUrl": "", "sourceTitle": "" },
        { "gpuMatch": "RTX 5080", "title": "http source", "kind": "overclock", "settings": {}, "sourceUrl": "http://example.com", "sourceTitle": "" }
      ]
    }
    """;

    private static GpuInfo Gpu(string name) => new(GpuVendor.Nvidia, name, "", 0);

    [Fact]
    public void EntriesWithoutHttpsSourceAreRejected()
    {
        var db = CommunityProfileDb.Parse(Json);
        Assert.Equal(2, db.Entries.Count);
        Assert.Equal(2, db.RejectedCount);
    }

    [Fact]
    public void MatchingIsWholeWord()
    {
        var db = CommunityProfileDb.Parse(Json);
        Assert.Equal(["base"], db.ForGpu(Gpu("NVIDIA GeForce RTX 5070")).Select(e => e.Title));
        Assert.Equal(["ti", "base"], db.ForGpu(Gpu("NVIDIA GeForce RTX 5070 Ti")).Select(e => e.Title));
    }

    [Fact]
    public void ConservativeStartHalvesOffsets()
    {
        var ti = CommunityProfileDb.Parse(Json).Entries.Single(e => e.Title == "ti");
        Assert.Equal(new TuningSettings(150, 500), ti.ConservativeStart());
    }

    [Fact]
    public void EmbeddedDatabaseLoads()
    {
        var db = CommunityProfileDb.LoadEmbedded();
        Assert.Equal(0, db.RejectedCount);
    }
}
