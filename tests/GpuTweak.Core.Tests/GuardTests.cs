using GpuTweak.Core.Models;
using GpuTweak.Core.Profiles;
using GpuTweak.Core.Safety;

namespace GpuTweak.Core.Tests;

public sealed class GuardTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gputweak").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void CleanExitIsNotACrash()
    {
        var guard = new CrashGuard(_dir);
        Assert.Null(guard.BeginSession());
        guard.MarkCustomApplied("UV");
        guard.EndSession();
        Assert.Null(new CrashGuard(_dir).BeginSession());
    }

    [Fact]
    public void UncleanExitWithCustomSettingsIsACrash()
    {
        var guard = new CrashGuard(_dir);
        guard.BeginSession();
        guard.MarkCustomApplied("UV");
        // no EndSession: simulates a crash
        var report = new CrashGuard(_dir).BeginSession();
        Assert.NotNull(report);
        Assert.Equal("UV", report.ProfileName);
    }

    [Fact]
    public void UncleanExitAtStockIsNotACrash()
    {
        var guard = new CrashGuard(_dir);
        guard.BeginSession();
        guard.MarkCustomApplied("UV");
        guard.MarkStock();
        Assert.Null(new CrashGuard(_dir).BeginSession());
    }

    [Fact]
    public void AutoApplyNeedsStableProfileAndNoCrash()
    {
        var store = new ProfileStore(_dir);
        const string gpu = "NVIDIA GeForce RTX 5080";
        store.Save(new UserProfile("Unstable", gpu, new TuningSettings(100), IsStable: false, AutoApplyOnStart: true));
        Assert.Null(store.GetStartupProfile(gpu, null));

        store.Save(new UserProfile("Stable", gpu, new TuningSettings(100), IsStable: true, AutoApplyOnStart: true));
        Assert.Equal("Stable", store.GetStartupProfile(gpu, null)?.Name);
        Assert.Null(store.GetStartupProfile(gpu, new CrashReport("Stable", null)));
        Assert.Null(store.GetStartupProfile("NVIDIA GeForce RTX 4090", null));

        // Persisted and only one auto-apply profile survives.
        var reloaded = new ProfileStore(_dir);
        Assert.Single(reloaded.Profiles, p => p.AutoApplyOnStart);
    }

    [Fact]
    public void ThermalGuardNeedsConsecutiveHotSamples()
    {
        var guard = new ThermalGuard(88, 3);
        GpuTelemetry At(double t) => new(DateTimeOffset.Now, t, null, null, null, null, null, null, null, null);

        Assert.False(guard.ShouldRevert(At(90), true));
        Assert.False(guard.ShouldRevert(At(90), true));
        Assert.False(guard.ShouldRevert(At(80), true)); // resets
        Assert.False(guard.ShouldRevert(At(90), true));
        Assert.False(guard.ShouldRevert(At(90), true));
        Assert.True(guard.ShouldRevert(At(90), true));
        Assert.False(guard.ShouldRevert(At(95), false)); // stock: never revert
    }
}
