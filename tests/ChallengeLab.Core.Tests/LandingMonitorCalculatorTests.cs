using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class LandingMonitorCalculatorTests
{
    private static readonly RunwayConfig Runway = new()
    {
        AirportIcao = "TEST",
        RunwayId = "09",
        ThresholdLatitude = 0,
        ThresholdLongitude = 0,
        HeadingTrueDeg = 90,
        ElevationFeet = 100
    };

    [Theory]
    [InlineData(-10, LandingMonitorStatus.Green)]
    [InlineData(10, LandingMonitorStatus.Green)]
    [InlineData(-10.1, LandingMonitorStatus.Orange)]
    [InlineData(49.9, LandingMonitorStatus.Orange)]
    [InlineData(-50, LandingMonitorStatus.Red)]
    [InlineData(50, LandingMonitorStatus.Red)]
    public void AirspeedBands_RespectExactBoundaries(double delta, LandingMonitorStatus expected) =>
        Assert.Equal(expected, LandingMonitorCalculator.ClassifyAirspeed(delta));

    [Theory]
    [InlineData(-1001, LandingMonitorStatus.Red)]
    [InlineData(-1000, LandingMonitorStatus.Orange)]
    [InlineData(-700.1, LandingMonitorStatus.Orange)]
    [InlineData(-700, LandingMonitorStatus.Green)]
    [InlineData(0, LandingMonitorStatus.Green)]
    [InlineData(.1, LandingMonitorStatus.Red)]
    public void VerticalSpeedBands_RespectExactBoundaries(double verticalSpeed, LandingMonitorStatus expected) =>
        Assert.Equal(expected, LandingMonitorCalculator.ClassifyVerticalSpeed(verticalSpeed));

    [Theory]
    [InlineData(2.8, 3.0, LandingMonitorStatus.Green)]
    [InlineData(3.2, 3.0, LandingMonitorStatus.Green)]
    [InlineData(2.79, 3.0, LandingMonitorStatus.Red)]
    [InlineData(3.21, 3.0, LandingMonitorStatus.Red)]
    [InlineData(5.3, 5.5, LandingMonitorStatus.Green)]
    [InlineData(5.8, 5.5, LandingMonitorStatus.Red)]
    public void GlideslopeBands_AreRelativeToTarget(
        double measured, double target, LandingMonitorStatus expected) =>
        Assert.Equal(expected, LandingMonitorCalculator.ClassifyGlideslope(measured, target));

    [Fact]
    public void Calculate_UsesGeometricPathAndTrueGroundTrackUnderCrab()
    {
        var sample = SampleAtDistance(2, 3.0, heading: 120, airspeed: 140, verticalSpeed: -650);

        var reading = LandingMonitorCalculator.Calculate(sample, Runway, 135, .2, 4.5);

        Assert.Equal(LandingMonitorStatus.Green, reading.AirspeedStatus);
        Assert.Equal(LandingMonitorStatus.Green, reading.GlideslopeStatus);
        Assert.Equal(LandingMonitorStatus.Green, reading.VerticalSpeedStatus);
        Assert.InRange(reading.GlideslopeDeg!.Value, 2.999, 3.001);
        Assert.InRange(reading.ClosingSpeedKts!.Value, 99.999, 100.001);
        Assert.True(reading.IsInsideCollectionWindow);
    }

    [Fact]
    public void Calculate_UsesGroundTrackComponentForCrosswindClosure()
    {
        var sample = SampleAtDistance(2, 3, track: 120);

        var reading = LandingMonitorCalculator.Calculate(sample, Runway, 135, .2, 4.5);

        Assert.InRange(reading.ClosingSpeedKts!.Value, 86.59, 86.61);
    }

    [Theory]
    [InlineData(4.5, 0)]
    [InlineData(2.25, 50)]
    [InlineData(0, 100)]
    public void Progress_RunsFromOuterBoundaryToThreshold(double distanceNm, double expected)
    {
        var reading = LandingMonitorCalculator.Calculate(
            SampleAtDistance(distanceNm, 3), Runway, 135, .2, 4.5);

        Assert.InRange(reading.ProgressPercent, expected - .01, expected + .01);
    }

    [Fact]
    public void PastAimPointAndOnGround_GlideslopeIsNeutral()
    {
        // 0.25 NM past threshold is past the 1,200 ft aim point → no geometric path angle.
        var pastAim = SampleAtDistance(-.25, 3);
        var ground = SampleAtDistance(1, 3, onGround: true);

        var pastAimReading = LandingMonitorCalculator.Calculate(pastAim, Runway, 135, .2, 4.5);
        var groundReading = LandingMonitorCalculator.Calculate(ground, Runway, 135, .2, 4.5);

        Assert.Null(pastAimReading.GlideslopeDeg);
        Assert.Equal(LandingMonitorStatus.Neutral, pastAimReading.GlideslopeStatus);
        Assert.Null(groundReading.GlideslopeDeg);
        Assert.Equal(LandingMonitorStatus.Neutral, groundReading.GlideslopeStatus);
        Assert.False(groundReading.IsInsideCollectionWindow);
    }

    [Fact]
    public void ExpectedAltitude_MeetsElevationAtAimPointNotThreshold()
    {
        var atThreshold = RunwayPathGeometry.ExpectedAltitudeFeet(0, Runway.ElevationFeet);
        var atAim = RunwayPathGeometry.ExpectedAltitudeFeet(
            -RunwayPathGeometry.GlideslopeAimPointOffsetNm, Runway.ElevationFeet);

        Assert.InRange(atThreshold - Runway.ElevationFeet, 60, 70); // ~1,200 ft × tan(3°)
        Assert.InRange(atAim, Runway.ElevationFeet - 0.01, Runway.ElevationFeet + 0.01);
    }

    private static TelemetrySample SampleAtDistance(
        double distanceNm,
        double angleDeg,
        double heading = 90,
        double track = 90,
        double groundSpeed = 100,
        double airspeed = 135,
        double verticalSpeed = -700,
        bool onGround = false)
    {
        var distanceMeters = distanceNm * RunwayPathGeometry.MetersPerNauticalMile;
        var longitude = -distanceMeters / RunwayPathGeometry.EarthRadiusMeters * 180 / Math.PI;
        // Height for the requested geometric angle is measured to the aim point, not threshold.
        var pathDistanceMeters = distanceMeters + RunwayPathGeometry.GlideslopeAimPointOffsetMeters;
        var heightFeet = Math.Tan(angleDeg * Math.PI / 180)
                         * Math.Abs(pathDistanceMeters) * LandingMonitorCalculator.FeetPerMeter;
        return new TelemetrySample
        {
            Timestamp = DateTimeOffset.Parse("2026-07-15T10:00:00Z"),
            Latitude = 0,
            Longitude = longitude,
            AltitudeFeet = Runway.ElevationFeet + heightFeet,
            AglFeet = heightFeet,
            RadioHeightFeet = heightFeet,
            AirspeedKts = airspeed,
            GroundSpeedKts = groundSpeed,
            GroundTrackTrueDeg = track,
            HeadingTrueDeg = heading,
            VerticalSpeedFpm = verticalSpeed,
            SimOnGround = onGround
        };
    }
}
