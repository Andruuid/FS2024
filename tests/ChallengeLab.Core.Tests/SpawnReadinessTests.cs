using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public class SpawnReadinessTests
{
    private static SpawnConfig Spawn(double ias = 270) => new()
    {
        Latitude = 0,
        Longitude = 0,
        AltitudeFeet = 3000,
        HeadingDeg = 90,
        AirspeedKts = ias
    };

    private static AircraftSetupConfig Setup(
        bool gearDown = false,
        int flaps = 0,
        bool spoilersRetracted = true) => new()
    {
        GearDown = gearDown,
        FlapsHandleIndex = flaps,
        SpoilersRetracted = spoilersRetracted
    };

    private static TelemetrySample Sample(
        double ias,
        double gear = 0,
        int flaps = 0,
        double spoilersHandle = 0,
        double? spoilersSurface = null) => new()
    {
        AirspeedKts = ias,
        GearHandlePosition = gear,
        FlapsHandleIndex = flaps,
        SpoilersHandlePosition = spoilersHandle,
        SpoilersSurfacePosition = spoilersSurface
    };

    [Fact]
    public void Evaluate_AllOk_IsReady()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(270),
            Setup(gearDown: false, flaps: 0, spoilersRetracted: true),
            Sample(ias: 270, gear: 0, flaps: 0, spoilersHandle: 0, spoilersSurface: 0));

        Assert.True(result.Ready);
        Assert.True(result.CriticalReady);
        Assert.False(result.SoftReady);
        Assert.Contains("ok", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NullSample_NotReady()
    {
        var result = SpawnReadiness.Evaluate(Spawn(), Setup(), sample: null);
        Assert.False(result.Ready);
        Assert.Contains("telemetry", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(256.5, true)]
    [InlineData(256.4, false)]
    [InlineData(270, true)]
    [InlineData(0, false)]
    public void Evaluate_IasThreshold_AtHighTarget(double liveIas, bool expectedReady)
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(270),
            Setup(),
            Sample(ias: liveIas, gear: 0, flaps: 0, spoilersSurface: 0));

        Assert.Equal(expectedReady, result.Ready);
    }

    [Fact]
    public void IsAirspeedReady_LowTarget_UsesAbsoluteFloor()
    {
        Assert.True(SpawnReadiness.IsAirspeedReady(35, 40));
        Assert.False(SpawnReadiness.IsAirspeedReady(34.9, 40));
    }

    [Fact]
    public void Evaluate_WrongGear_NotReady()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(gearDown: false),
            Sample(ias: 155, gear: 1, flaps: 0, spoilersSurface: 0));

        Assert.False(result.Ready);
        Assert.Contains("want up", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WrongFlaps_NotReady()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(flaps: 2),
            Sample(ias: 155, gear: 0, flaps: 0, spoilersSurface: 0));

        Assert.False(result.Ready);
        Assert.Contains("want 2", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_SurfaceDeployed_NotReadyBeforeSoftTimeout()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(spoilersRetracted: true),
            Sample(ias: 155, gear: 0, flaps: 0, spoilersHandle: 0, spoilersSurface: 0.5),
            elapsedSeconds: 2);

        Assert.False(result.Ready);
        Assert.True(result.CriticalReady);
        Assert.Contains("want in", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_HandleArmedButSurfaceStowed_IsReady()
    {
        // Airbus: lever may read non-zero / "armed" while panels are fully stowed.
        var result = SpawnReadiness.Evaluate(
            Spawn(270),
            Setup(spoilersRetracted: true),
            Sample(ias: 270, gear: 0, flaps: 0, spoilersHandle: 0.4, spoilersSurface: 0.0));

        Assert.True(result.Ready);
        Assert.False(result.SoftReady);
        Assert.Contains("in", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_SurfaceStowed_SoftTimeoutNotNeeded()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(270),
            Setup(spoilersRetracted: true),
            Sample(ias: 270, gear: 0, flaps: 0, spoilersHandle: 0, spoilersSurface: 0.05));

        Assert.True(result.Ready);
        Assert.False(result.SoftReady);
    }

    [Fact]
    public void Evaluate_SurfaceOut_SoftTimeout_UnlocksGo()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(270),
            Setup(spoilersRetracted: true),
            Sample(ias: 270, gear: 0, flaps: 0, spoilersSurface: 0.6),
            elapsedSeconds: SpawnReadiness.SoftTimeoutSeconds);

        Assert.True(result.Ready);
        Assert.True(result.SoftReady);
        Assert.False(result.ForceReady);
    }

    [Fact]
    public void Evaluate_WrongGear_SoftTimeout_UnlocksWhenIasOk()
    {
        // Under SET PAUSE A330 often leaves gear down; GO must not spin forever.
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(gearDown: false),
            Sample(ias: 155, gear: 1, flaps: 0, spoilersSurface: 1.0),
            elapsedSeconds: SpawnReadiness.SoftTimeoutSeconds);

        Assert.True(result.Ready);
        Assert.True(result.SoftReady);
        Assert.False(result.CriticalReady);
        Assert.Contains("soft-ready", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_HardTimeout_ForceReadyWithoutIas()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(gearDown: false),
            Sample(ias: 0, gear: 1, flaps: 0, spoilersSurface: 1.0),
            elapsedSeconds: SpawnReadiness.HardTimeoutSeconds);

        Assert.True(result.Ready);
        Assert.True(result.ForceReady);
        Assert.False(result.SoftReady);
        Assert.Contains("force-ready", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_HandleOnly_PercentScaleOut()
    {
        // No surface telemetry — fall back to handle (0–100%).
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(spoilersRetracted: true),
            Sample(ias: 155, gear: 0, flaps: 0, spoilersHandle: 50, spoilersSurface: null),
            elapsedSeconds: 0);

        Assert.False(result.Ready);
    }

    [Fact]
    public void NormalizeSpoiler01_16kPositionUnits()
    {
        // Full deflection on position scale.
        Assert.InRange(SpawnReadiness.NormalizeSpoiler01(16383), 0.99, 1.01);
        Assert.InRange(SpawnReadiness.NormalizeSpoiler01(0), 0, 0.01);
        // Half on percent-over-100.
        Assert.InRange(SpawnReadiness.NormalizeSpoiler01(0.5), 0.49, 0.51);
    }

    [Fact]
    public void Evaluate_GearDownConfig_Matches()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(140),
            Setup(gearDown: true, flaps: 3),
            Sample(ias: 140, gear: 1, flaps: 3, spoilersSurface: 0));

        Assert.True(result.Ready);
    }

    [Fact]
    public void Evaluate_SpoilersNotRequired_IgnoresSurface()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(spoilersRetracted: false),
            Sample(ias: 155, gear: 0, flaps: 0, spoilersSurface: 0.9));

        Assert.True(result.Ready);
    }
}
