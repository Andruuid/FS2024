using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class FreeFlightEvaluationStartTests
{
    private static readonly AirportFacility Airport = new("TEST", "ZZ", 0, 0, 0);
    private static readonly FreeFlightEvaluationStartPolicy Policy = new()
    {
        HeightAboveRunwayFeet = 2_000,
        LeadSeconds = 5,
        ApproachPathMaxDistNm = 15
    };

    [Fact]
    public void StandardThreeDegreePath_StartsAboutSixPointThreeNmAt140Knots()
    {
        var state = FreeFlightEvaluationStartCalculator.Calculate(
            Sample(longitude: -0.2, heading: 90, groundSpeed: 140),
            Runway(glideslope: 3),
            Policy);

        Assert.Equal(6.12, state.InterceptDistanceNm, 2);
        Assert.Equal(6.31, state.TriggerDistanceNm, 2);
        Assert.True(state.SecondsUntilStart > 140);
        Assert.False(state.IsReady);
    }

    [Fact]
    public void InsideTrigger_StartsImmediatelyAndRunwayElevationDoesNotChangeGeometry()
    {
        var low = FreeFlightEvaluationStartCalculator.Calculate(
            Sample(longitude: -0.1, heading: 90, groundSpeed: 140),
            Runway(glideslope: 3, elevationFeet: 0),
            Policy);
        var high = FreeFlightEvaluationStartCalculator.Calculate(
            Sample(longitude: -0.1, heading: 90, groundSpeed: 140, altitudeFeet: 10_000),
            Runway(glideslope: 3, elevationFeet: 8_000),
            Policy);

        Assert.True(low.IsReady);
        Assert.True(high.IsReady);
        Assert.Equal(0, low.SecondsUntilStart);
        Assert.Equal(low.InterceptDistanceNm, high.InterceptDistanceNm, 9);
        Assert.Equal(low.TriggerDistanceNm, high.TriggerDistanceNm, 9);
    }

    [Fact]
    public void SteepPathMovesInterceptCloserAndTrackingAwayNeverStarts()
    {
        var steep = FreeFlightEvaluationStartCalculator.Calculate(
            Sample(longitude: -0.1, heading: 90, groundSpeed: 140),
            Runway(glideslope: 6.65),
            Policy);
        var away = FreeFlightEvaluationStartCalculator.Calculate(
            Sample(longitude: -0.05, heading: 270, groundSpeed: 140),
            Runway(glideslope: 3),
            Policy);

        Assert.Equal(2.85, steep.TriggerDistanceNm, 2);
        Assert.False(steep.IsReady);
        Assert.Equal(0, away.ClosingSpeedKts);
        Assert.False(away.IsReady);
        Assert.Null(away.SecondsUntilStart);
    }

    [Fact]
    public void ClosingSpeedUsesGroundTrackDuringLargeCrab()
    {
        var alignedTrack = FreeFlightEvaluationStartCalculator.Calculate(
            Sample(longitude: -.1, heading: 50, groundSpeed: 140, groundTrack: 90),
            Runway(glideslope: 3),
            Policy);
        var crossingTrack = FreeFlightEvaluationStartCalculator.Calculate(
            Sample(longitude: -.1, heading: 90, groundSpeed: 140, groundTrack: 180),
            Runway(glideslope: 3),
            Policy);

        Assert.Equal(140, alignedTrack.ClosingSpeedKts, 6);
        Assert.Equal(0, crossingTrack.ClosingSpeedKts, 6);
        Assert.False(crossingTrack.IsReady);
    }

    private static RunwayEndFacility Runway(double glideslope, double elevationFeet = 0) =>
        new(Airport, "09", 0, 0, elevationFeet, 90, 2_000, 45, 4, false, glideslope);

    private static TelemetrySample Sample(
        double longitude,
        double heading,
        double groundSpeed,
        double altitudeFeet = 2_000,
        double? groundTrack = null) => new()
    {
        Latitude = 0,
        Longitude = longitude,
        AltitudeFeet = altitudeFeet,
        HeadingTrueDeg = heading,
        GroundTrackTrueDeg = groundTrack,
        GroundSpeedKts = groundSpeed,
        SimOnGround = false
    };
}
