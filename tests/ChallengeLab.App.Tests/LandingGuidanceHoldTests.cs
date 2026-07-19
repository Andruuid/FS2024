using System.IO;
using ChallengeLab.App.Controls.Hud;
using ChallengeLab.App.ViewModels;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Tests;

public sealed class LandingGuidanceHoldTests
{
    [Fact]
    public void InferenceTransitionsArePersistedAsStructuredJsonLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"free-inference-{Guid.NewGuid():N}.jsonl");
        try
        {
            var log = new FreeFlightInferenceLog(path);
            log.Append(new FreeFlightInferenceLogEntry(
                DateTimeOffset.Parse("2026-07-18T21:34:03Z"),
                "lock-acquired",
                "stable target confirmed",
                "LSZH:34",
                "LSZH:34",
                .771,
                4.5,
                .1,
                .02,
                47.43,
                8.56,
                1_696,
                143,
                138,
                1,
                true,
                "A320neo V2",
                GroundMotionResolver.GpsGroundTrackSource,
                -17.3));

            var line = Assert.Single(File.ReadAllLines(path));
            Assert.Contains("\"event\":\"lock-acquired\"", line, StringComparison.Ordinal);
            Assert.Contains("\"lockedKey\":\"LSZH:34\"", line, StringComparison.Ordinal);
            Assert.Contains("\"courseErrorDeg\":0.1", line, StringComparison.Ordinal);
            Assert.Contains("\"courseSource\":\"GPS GROUND TRUE TRACK\"", line, StringComparison.Ordinal);
            Assert.Contains("\"crabAngleDeg\":-17.3", line, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void HoldsPathAcrossAimPointAndDescentAcrossTouchdown()
    {
        var hold = new LandingGuidanceHold();
        var runway = Runway();
        var flare = Sample(latitude: -0.0009, altitudeFeet: 1_040, aglFeet: 40, verticalSpeedFpm: -350);
        var afterAimPoint = Sample(
            latitude: 0.003,
            altitudeFeet: 1_012,
            aglFeet: 12,
            verticalSpeedFpm: -220);
        var touchdown = Sample(
            latitude: 0.0031,
            altitudeFeet: 1_010,
            aglFeet: 10,
            verticalSpeedFpm: -180,
            onGround: true);

        var flareRaw = Calculate(flare, runway);
        var flareHeld = hold.Update(flare, runway, flareRaw, flareAglFeet: 50);
        var afterAimRaw = Calculate(afterAimPoint, runway);
        var afterAimHeld = hold.Update(afterAimPoint, runway, afterAimRaw, flareAglFeet: 50);
        var touchdownRaw = Calculate(touchdown, runway);
        var touchdownHeld = hold.Update(touchdown, runway, touchdownRaw, flareAglFeet: 50);

        Assert.NotNull(flareHeld.GlideslopeDeg);
        Assert.Null(afterAimRaw.GlideslopeDeg);
        Assert.Equal(flareHeld.GlideslopeDeg, afterAimHeld.GlideslopeDeg);
        Assert.NotNull(afterAimHeld.DescentAngleDeg);
        Assert.Null(touchdownRaw.DescentAngleDeg);
        Assert.Equal(afterAimHeld.DescentAngleDeg, touchdownHeld.DescentAngleDeg);
        Assert.Equal(afterAimHeld.GlideslopeDeg, touchdownHeld.GlideslopeDeg);
    }

    [Fact]
    public void ResetOrMissingRunwayClearsHeldGuidance()
    {
        var hold = new LandingGuidanceHold();
        var runway = Runway();
        var flare = Sample(latitude: -0.0009, altitudeFeet: 1_040, aglFeet: 40, verticalSpeedFpm: -350);
        var held = hold.Update(flare, runway, Calculate(flare, runway), 50);
        Assert.NotNull(held.GlideslopeDeg);

        hold.Reset();
        var noRunwayRaw = Calculate(flare, runway: null);
        var noRunway = hold.Update(flare, runway: null, noRunwayRaw, 50);

        Assert.Null(noRunway.GlideslopeDeg);
        Assert.Null(noRunway.DescentAngleDeg);
    }

    [Fact]
    public void BothHudsConsumeTheExactSameHeldReading()
    {
        var hold = new LandingGuidanceHold();
        var runway = Runway();
        var challenge = new ChallengeConfig { Runway = runway };
        var flare = Sample(latitude: -0.0009, altitudeFeet: 1_040, aglFeet: 40, verticalSpeedFpm: -350);
        var touchdown = Sample(
            latitude: 0.0031,
            altitudeFeet: 1_010,
            aglFeet: 10,
            verticalSpeedFpm: -180,
            onGround: true);
        hold.Update(flare, runway, Calculate(flare, runway), 50);
        var shared = hold.Update(touchdown, runway, Calculate(touchdown, runway), 50);

        var frame = HudPresentationFrame.FromGuidance(touchdown, true, 1, runway, shared);
        var monitor = new SecondaryHudViewModel();
        monitor.Update(
            touchdown,
            challenge,
            settings: null,
            targetAirspeedKts: null,
            projectedScorePercent: null,
            phase: LandingPhase.Touchdown,
            isConnected: true,
            sharedGuidance: shared,
            evaluationArmed: true);

        Assert.Equal(shared, frame.Guidance);
        Assert.Equal($"{shared.GlideslopeDeg:0.0}°", monitor.GlideslopeDisplay);
        Assert.Equal($"{shared.DescentAngleDeg:0.0}°", monitor.DescentAngleDisplay);
    }

    private static LandingMonitorReading Calculate(TelemetrySample sample, RunwayConfig? runway) =>
        LandingMonitorCalculator.Calculate(sample, runway, 135, .2, 15);

    private static RunwayConfig Runway() => new()
    {
        AirportIcao = "TEST",
        RunwayId = "36",
        ThresholdLatitude = 0,
        ThresholdLongitude = 0,
        ElevationFeet = 1_000,
        HeadingTrueDeg = 0,
        LengthM = 3_000,
        WidthM = 45,
        GlideslopeDeg = 3,
    };

    private static TelemetrySample Sample(
        double latitude,
        double altitudeFeet,
        double aglFeet,
        double verticalSpeedFpm,
        bool onGround = false) => new()
    {
        Timestamp = DateTimeOffset.Parse("2026-07-18T21:34:28Z"),
        Latitude = latitude,
        Longitude = 0,
        AltitudeFeet = altitudeFeet,
        AglFeet = aglFeet,
        RadioHeightFeet = aglFeet,
        HeadingTrueDeg = 0,
        GroundTrackTrueDeg = 0,
        AirspeedKts = 130,
        GroundSpeedKts = 125,
        VerticalSpeedFpm = verticalSpeedFpm,
        SimOnGround = onGround,
        AircraftTitle = "A320neo V2",
    };
}
