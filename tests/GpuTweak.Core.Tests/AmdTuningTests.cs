using GpuTweak.Core.AutoTune;
using GpuTweak.Core.Models;
using GpuTweak.Core.Safety;
using GpuTweak.Core.Stability;

namespace GpuTweak.Core.Tests;

/// <summary>AMD cards undervolt by lowering the voltage itself; the power limit is a percentage of stock.</summary>
public sealed class AmdTuningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gputweak-amd").FullName;

    // Like an RX 9070 XT: frequency offset -500…+200, voltage down to -200 mV, power 70…110 %, memory +0…+200.
    private static readonly TuningLimits AmdLimits = new(-500, 200, 0, 200, 70, 110, 100, 0, 0, 30, 100, true,
        VoltageOffsetMinMv: -200, PowerInPercent: true, SupportsClockCap: false);

    private readonly FakeBackend _backend = new() { Limits = AmdLimits };

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Simulated chip that makes errors below a given voltage.</summary>
    private sealed class VoltageStress(FakeBackend backend, int failBelowMv) : IStressTest
    {
        public List<TuningSettings> Seen { get; } = [];
        public Task<StressResult> PrepareAsync(CancellationToken ct) => Task.FromResult(new StressResult(true, 100, null));

        public Task<StressResult> RunAsync(StressPattern pattern, TimeSpan duration, CancellationToken ct)
        {
            Seen.Add(backend.Current);
            return Task.FromResult(backend.Current.VoltageOffsetMv < failBelowMv ? StressResult.Fail("hesap hatası") : new StressResult(true, 100, null));
        }

        public void Dispose() { }
    }

    private static TunerOptions Fast => new()
    {
        BoostProbe = TimeSpan.Zero, Baseline = TimeSpan.Zero, Step = TimeSpan.Zero, Validation = TimeSpan.Zero,
        SampleInterval = TimeSpan.FromMilliseconds(1),
    };

    [Fact]
    public async Task CoolGoalLowersVoltageAndKeepsStockClocks()
    {
        var stress = new VoltageStress(_backend, failBelowMv: -80);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        // Steps of 10 mV: -80 passes, -90 fails; minus the 30 mV margin = -50.
        Assert.Equal(-50, result.Settings!.VoltageOffsetMv);
        Assert.Equal(0, result.Settings.CoreOffsetMHz);
        Assert.Null(result.Settings.MaxCoreClockMHz);
        Assert.Null(result.Settings.PowerLimitW);
        Assert.Equal(80, result.HighestPassedOffsetMHz);
        // The core offset is never touched while searching.
        Assert.All(stress.Seen, s => Assert.Equal(0, s.CoreOffsetMHz));
    }

    [Fact]
    public async Task SearchStopsAtTheConservativeDepth()
    {
        var stress = new VoltageStress(_backend, failBelowMv: -1000);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(null, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.Equal(-120, result.Settings!.VoltageOffsetMv); // -150 limit, minus 30
        Assert.True(result.ReachedSearchLimit);
        Assert.DoesNotContain(stress.Seen, s => s.VoltageOffsetMv < -150);
    }

    [Fact]
    public async Task PerformanceGoalRaisesThePowerLimitInPercent()
    {
        var result = await new AutoTuner(_backend, new VoltageStress(_backend, -60), _dir, Fast).RunAsync(null, CancellationToken.None, TuneGoal.Performance);
        Assert.True(result.Success, result.Message);
        Assert.Equal(110, result.Settings!.PowerLimitW);
        Assert.Equal(-30, result.Settings.VoltageOffsetMv);
    }

    [Fact]
    public async Task EfficientGoalLowersTheMaxClockByOffset()
    {
        var result = await new AutoTuner(_backend, new VoltageStress(_backend, -60), _dir, Fast).RunAsync(null, CancellationToken.None, TuneGoal.Efficient);
        Assert.True(result.Success, result.Message);
        // 6 % of the 2700 MHz boost = 162, rounded down to 15 MHz.
        Assert.Equal(-150, result.Settings!.CoreOffsetMHz);
        Assert.Null(result.Settings.MaxCoreClockMHz);
    }

    [Fact]
    public async Task CrashDuringTestMakesThatVoltageTheFloor()
    {
        var journal = new TuneJournal(0, 100, 250, 60, 40, 70, DateTimeOffset.Now, TuneGoal.Cool, VoltageMode: true);
        var stress = new VoltageStress(_backend, failBelowMv: -1000);
        var result = await new AutoTuner(_backend, stress, _dir, Fast).RunAsync(journal, CancellationToken.None);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain(stress.Seen, s => s.VoltageOffsetMv <= -70);
        Assert.Equal(-30, result.Settings!.VoltageOffsetMv); // -60 passed, minus 30
    }

    [Fact]
    public void ClampNeverRaisesVoltageAndDropsTheClockCap()
    {
        var clamped = SafetyClamp.Clamp(new TuningSettings(MaxCoreClockMHz: 2500, VoltageOffsetMv: 50), AmdLimits).Settings;
        Assert.Equal(0, clamped.VoltageOffsetMv);
        Assert.Null(clamped.MaxCoreClockMHz);
        Assert.Equal(-200, SafetyClamp.Clamp(new TuningSettings(VoltageOffsetMv: -400), AmdLimits).Settings.VoltageOffsetMv);
        Assert.Equal(110, SafetyClamp.Clamp(new TuningSettings(PowerLimitW: 150), AmdLimits).Settings.PowerLimitW);
    }

    [Fact]
    public void NvidiaLimitsKeepVoltageAtZero()
    {
        var nvidia = new FakeBackend().Limits;
        Assert.False(nvidia.UndervoltsByVoltage);
        Assert.Equal(0, SafetyClamp.Clamp(new TuningSettings(VoltageOffsetMv: -50), nvidia).Settings.VoltageOffsetMv);
    }

    [Fact]
    public void CrashInGameGivesBackTenMillivolts()
    {
        var step = StabilityAdvisor.StepDown(new TuningSettings(MemoryOffsetMHz: 100, VoltageOffsetMv: -60), IncidentKind.DriverReset,
            StabilityAdvisor.SmallMemoryStepMHz)!;
        Assert.Equal(-50, step.Settings.VoltageOffsetMv);
        Assert.Equal(100, step.Settings.MemoryOffsetMHz);

        var artifact = StabilityAdvisor.StepDown(new TuningSettings(MemoryOffsetMHz: 100, VoltageOffsetMv: -60), IncidentKind.UserReportedArtifact,
            StabilityAdvisor.SmallMemoryStepMHz)!;
        Assert.Equal(75, artifact.Settings.MemoryOffsetMHz);
    }

    [Fact]
    public async Task StressTestRaisesVoltageOnError()
    {
        var stress = new VoltageStress(_backend, failBelowMv: -40);
        var options = new StressTestOptions
        {
            Sustained = TimeSpan.FromMinutes(2), Transient = TimeSpan.FromMinutes(1), Segment = TimeSpan.FromMinutes(1),
            SweepPoints = 3, SampleInterval = TimeSpan.FromMilliseconds(1),
        };
        var result = await new StressTestRunner(_backend, stress, options).RunAsync(new TuningSettings(VoltageOffsetMv: -60), CancellationToken.None);

        Assert.True(result.Passed, result.Failure);
        Assert.Equal(-40, result.FinalSettings!.VoltageOffsetMv);
        Assert.Equal(2, result.StepDowns);
        // No clock cap on AMD, so there is no V/F sweep: 2 sustained + 1 transient segments plus the 2 repeats.
        Assert.Equal(5, stress.Seen.Count);
    }

    [Fact]
    public void MemoryStepsFitTheSmallAmdRange()
    {
        Assert.Equal(20, MemoryTuner.StepFor(AmdLimits, 10));
        Assert.Equal(600, MemoryTuner.StepFor(new FakeBackend().Limits with { MemoryOffsetMaxMHz = 6000 }, 10));
    }

    [Fact]
    public void PowerIsShownInPercent()
    {
        Assert.Equal("%95", AmdLimits.FormatPower(95));
        Assert.Equal("300 W", new FakeBackend().Limits.FormatPower(300));
    }
}
