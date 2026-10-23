namespace GpuTweak.Core.Tests;

public sealed class RunningStatsTests
{
    [Fact]
    public void TracksMinMaxAverageAndIgnoresMissingValues()
    {
        var stats = new RunningStats();
        Assert.Null(stats.Average);
        foreach (var v in new double?[] { 40, null, 60, 50 }) stats.Add(v);
        Assert.Equal(40, stats.Min);
        Assert.Equal(60, stats.Max);
        Assert.Equal(50, stats.Average);

        stats.Reset();
        Assert.Null(stats.Max);
        Assert.Equal(0, stats.Count);
    }
}
