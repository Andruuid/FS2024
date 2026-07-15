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
        double spoilers = 0) => new()
    {
        AirspeedKts = ias,
        GearHandlePosition = gear,
        FlapsHandleIndex = flaps,
        SpoilersHandlePosition = spoilers
    };

    [Fact]
    public void Evaluate_AllOk_IsReady()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(270),
            Setup(gearDown: false, flaps: 0, spoilersRetracted: true),
            Sample(ias: 270, gear: 0, flaps: 0, spoilers: 0));

        Assert.True(result.Ready);
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
    [InlineData(256.5, true)]   // exactly 95% of 270
    [InlineData(256.4, false)]  // just under 95%
    [InlineData(270, true)]
    [InlineData(0, false)]
    public void Evaluate_IasThreshold_AtHighTarget(double liveIas, bool expectedReady)
    {
        // Surfaces perfect so only IAS decides.
        var result = SpawnReadiness.Evaluate(
            Spawn(270),
            Setup(),
            Sample(ias: liveIas, gear: 0, flaps: 0, spoilers: 0));

        Assert.Equal(expectedReady, result.Ready);
    }

    [Fact]
    public void IsAirspeedReady_LowTarget_UsesAbsoluteFloor()
    {
        // Target 40 → 95% = 38; absolute floor target−5 = 35 → threshold = min(38,35) = 35.
        Assert.True(SpawnReadiness.IsAirspeedReady(35, 40));
        Assert.False(SpawnReadiness.IsAirspeedReady(34.9, 40));
    }

    [Fact]
    public void Evaluate_WrongGear_NotReady()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(gearDown: false),
            Sample(ias: 155, gear: 1, flaps: 0, spoilers: 0));

        Assert.False(result.Ready);
        Assert.Contains("want up", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_WrongFlaps_NotReady()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(flaps: 2),
            Sample(ias: 155, gear: 0, flaps: 0, spoilers: 0));

        Assert.False(result.Ready);
        Assert.Contains("want 2", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_SpoilersOut_WhenRetractedRequired_NotReady()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(spoilersRetracted: true),
            Sample(ias: 155, gear: 0, flaps: 0, spoilers: 0.5));

        Assert.False(result.Ready);
        Assert.Contains("want in", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_SpoilersOut_WhenNotRequired_Ready()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(spoilersRetracted: false),
            Sample(ias: 155, gear: 0, flaps: 0, spoilers: 0.8));

        Assert.True(result.Ready);
    }

    [Fact]
    public void Evaluate_SpoilersPercentScale_TreatedAsOut()
    {
        // Handle reported as 0–100 percent.
        var result = SpawnReadiness.Evaluate(
            Spawn(155),
            Setup(spoilersRetracted: true),
            Sample(ias: 155, gear: 0, flaps: 0, spoilers: 50));

        Assert.False(result.Ready);
    }

    [Fact]
    public void Evaluate_GearDownConfig_Matches()
    {
        var result = SpawnReadiness.Evaluate(
            Spawn(140),
            Setup(gearDown: true, flaps: 3),
            Sample(ias: 140, gear: 1, flaps: 3, spoilers: 0));

        Assert.True(result.Ready);
    }
}
