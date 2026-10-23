namespace GpuTweak.Core.Tests;

public sealed class FrameRateTrackerTests
{
    private static void Frames(FrameRateTracker tracker, int pid, double from, int count)
    {
        for (var i = 0; i < count; i++) tracker.OnPresent(pid, from + (i + 1) / (double)count);
    }

    [Fact]
    public void CountsFramesOfTheForegroundProcessOnly()
    {
        var tracker = new FrameRateTracker();
        Frames(tracker, 10, 0, 120);
        Frames(tracker, 20, 0, 30);
        Assert.Equal(120, tracker.Sample(10, 1)!.Current);
        Assert.Null(tracker.Sample(99, 1));
    }

    [Fact]
    public void KeepsMinAverageMaxUntilTheProcessChanges()
    {
        var tracker = new FrameRateTracker();
        Frames(tracker, 10, 0, 60);
        tracker.Sample(10, 1);
        Frames(tracker, 10, 1, 120);
        var rate = tracker.Sample(10, 2)!;
        Assert.Equal(120, rate.Current);
        Assert.Equal(60, rate.Min);
        Assert.Equal(90, rate.Average);
        Assert.Equal(120, rate.Max);

        Frames(tracker, 20, 1, 50);
        var other = tracker.Sample(20, 2)!;
        Assert.Equal(50, other.Min);
    }
}
