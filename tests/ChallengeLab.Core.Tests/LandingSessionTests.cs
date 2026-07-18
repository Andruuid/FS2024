using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class LandingSessionTests
{
    private static string FindConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "catalog.json")))
                return Path.Combine(dir.FullName, "config");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("config not found");
    }

    private static (ChallengeConfig Challenge, LandingSessionSettings Settings) Load()
    {
        var loader = new ConfigLoader(FindConfig());
        var keyResult = loader.LoadEvaluationKey();
        Assert.True(keyResult.IsValid, string.Join("; ", keyResult.Errors));
        var challenge = loader.LoadChallenge("challenges/barcelona-crosswind-final.json");
        return (challenge, keyResult.Key!.ToSessionSettings());
    }

    private static TelemetrySample Sample(
        DateTimeOffset t,
        bool onGround,
        double agl = 2000,
        double vs = -200,
        double lat = 43.40,
        double lon = 5.25,
        double gs = 140,
        double ias = 145,
        bool stallWarning = false,
        bool overspeedWarning = false,
        double heading = 313)
        => new()
        {
            Timestamp = t,
            SimOnGround = onGround,
            AglFeet = agl,
            RadioHeightFeet = agl,
            VerticalSpeedFpm = vs,
            Latitude = lat,
            Longitude = lon,
            AltitudeFeet = 43 + agl,
            GroundSpeedKts = gs,
            AirspeedKts = ias,
            HeadingTrueDeg = heading,
            PitchDeg = 2,
            BankDeg = 0,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            GForce = 1.1,
            StallWarningActive = stallWarning,
            StallWarningAvailable = true,
            OverspeedWarningActive = overspeedWarning,
            OverspeedWarningAvailable = true
        };

    [Fact]
    public void RunwayLength_IsLatchedFromChallengeAtConstructionAndSurvivesArm()
    {
        var (challenge, settings) = Load();
        Assert.True(challenge.Runway.LengthM > 0);

        var session = new LandingSession(challenge, settings);
        Assert.Equal(challenge.Runway.LengthM, session.Snapshot.GateObservations.RunwayLengthMeters);

        session.Arm();
        Assert.Equal(challenge.Runway.LengthM, session.Snapshot.GateObservations.RunwayLengthMeters);

        session.CleanMetrics();
        Assert.Equal(challenge.Runway.LengthM, session.Snapshot.GateObservations.RunwayLengthMeters);
    }

    [Fact]
    public void StallWarning_IsLatchedForTheAttemptAndClearedWithMetrics()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        session.Ingest(Sample(t0, onGround: false, stallWarning: true));
        session.Ingest(Sample(t0.AddSeconds(0.1), onGround: false));

        Assert.True(session.Snapshot.StallWarningOccurred);
        session.CleanMetrics();
        Assert.False(session.Snapshot.StallWarningOccurred);
    }

    [Fact]
    public void OverspeedWarning_IsLatchedForTheAttemptAndClearedWithMetrics()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        session.Ingest(Sample(t0, onGround: false, overspeedWarning: true));
        session.Ingest(Sample(t0.AddSeconds(0.1), onGround: false));

        Assert.True(session.Snapshot.OverspeedWarningCoverageAvailable);
        Assert.True(session.Snapshot.OverspeedWarningOccurred);
        session.CleanMetrics();
        Assert.False(session.Snapshot.OverspeedWarningOccurred);
        Assert.False(session.Snapshot.OverspeedWarningCoverageAvailable);
    }

    [Fact]
    public void ArmOnGround_DoesNotCaptureTouchdownDuringGraceOrWhileGrounded()
    {
        var (challenge, settings) = Load();
        Assert.True(settings.PostArmIgnoreSeconds >= 1);
        Assert.True(settings.RequireAirborneBeforeTouchdown);

        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        // Stream grounded samples well past the post-arm grace window.
        for (var i = 0; i < 60; i++)
        {
            session.Ingest(Sample(t0.AddSeconds(i * 0.2), onGround: true, agl: 0, gs: 20));
        }

        Assert.Null(session.Snapshot.Touchdown);
        Assert.False(session.IsComplete);
        Assert.NotEqual(LandingPhase.Scored, session.Phase);
    }

    [Fact]
    public void RealApproachThenTouchdown_CapturesAfterAirborneHistory()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        // Stay past grace in the air with enough airborne samples.
        var t = t0.AddSeconds(settings.PostArmIgnoreSeconds + 0.5);
        for (var i = 0; i < settings.MinAirborneSamples + 5; i++)
        {
            session.Ingest(Sample(t.AddMilliseconds(i * 100), onGround: false, agl: 500 - i * 20, vs: -400));
        }

        // Rising edge to ground.
        var tdTime = t.AddSeconds(2);
        session.Ingest(Sample(tdTime, onGround: true, agl: 0, vs: -150, gs: 120, ias: 140));

        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.Equal(-150, session.Snapshot.VerticalSpeedAtTouchdownFpm, 3);
        Assert.Equal(LandingPhase.Rollout, session.Phase);
    }

    [Fact]
    public void RolloutAlignment_ZeroDelayIncludesTouchdownFrame()
    {
        var (challenge, settings) = Load();
        settings = settings with
        {
            PostTouchdownAlignmentDelaySeconds = 0,
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false,
            OperationalGates = new OperationalGateSessionSettings()
        };
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        var runwayHeading = challenge.Runway.HeadingTrueDeg;
        session.Arm();

        session.Ingest(Sample(t0, onGround: false, heading: runwayHeading));
        session.Ingest(Sample(
            t0.AddSeconds(1), onGround: true, agl: 0, gs: 120,
            heading: runwayHeading + 10));
        session.Ingest(Sample(
            t0.AddSeconds(1.5), onGround: true, agl: 0, gs: 100,
            heading: runwayHeading));

        Assert.Equal(10, session.Snapshot.TouchdownHeadingErrorDeg, 6);
        Assert.Equal(2, session.Snapshot.PostTouchdownAlignmentSampleCount);
        Assert.Equal(5, session.Snapshot.PostTouchdownAlignmentMeanDeg, 6);
        Assert.Equal(10, session.Snapshot.PostTouchdownAlignmentPeakDeg, 6);

        for (var i = 4; i <= 12; i++)
        {
            session.Ingest(Sample(
                t0.AddSeconds(1 + i * 0.25), onGround: true, agl: 0, gs: 100,
                heading: runwayHeading));
        }

        var crab = Assert.IsType<CrabAngleAnalysis>(session.Snapshot.CrabAngle);
        Assert.True(crab.CoverageSufficient, crab.DegradedReason);
        Assert.Equal(10, crab.TouchdownErrorDeg, 6);
        Assert.Equal(2.5, crab.IntegratedDeviationDegSeconds, 3);
        Assert.Equal(3, crab.CoverageSeconds, 3);
        Assert.Equal(10, crab.PeakDeviationDeg, 6);
    }

    [Fact]
    public void SettlingEarly_WaitsForCompleteThreeSecondCrabWindow()
    {
        var (challenge, settings) = Load();
        settings = settings with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false,
            OperationalGates = new OperationalGateSessionSettings()
        };
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        var runwayHeading = challenge.Runway.HeadingTrueDeg;
        session.Arm();

        session.Ingest(Sample(t0, onGround: false, heading: runwayHeading));
        var touchdown = t0.AddSeconds(1);
        session.Ingest(Sample(
            touchdown, onGround: true, agl: 0, gs: 20, heading: runwayHeading));
        session.Ingest(Sample(
            touchdown.AddSeconds(0.75), onGround: true, agl: 0, gs: 20,
            heading: runwayHeading));
        session.Ingest(Sample(
            touchdown.AddSeconds(1.5), onGround: true, agl: 0, gs: 20,
            heading: runwayHeading));

        Assert.False(session.IsComplete);

        session.Ingest(Sample(
            touchdown.AddSeconds(2.25), onGround: true, agl: 0, gs: 20,
            heading: runwayHeading));
        session.Ingest(Sample(
            touchdown.AddSeconds(3), onGround: true, agl: 0, gs: 20,
            heading: runwayHeading));

        Assert.True(session.IsComplete);
        Assert.True(session.Snapshot.CrabAngle?.CoverageSufficient);
    }

    [Fact]
    public void ApproachPathRms_IgnoresHighIntermediateAndUsesShortFinalOnly()
    {
        var (challenge, settings) = Load();
        Assert.True(settings.ApproachPathMaxDistNm <= 5);
        Assert.True(settings.ApproachPathMinDistNm < settings.ApproachPathMaxDistNm);

        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        // Far / high (spawn-like): ~7.5 NM, ~3500 ft — must NOT dominate path RMS.
        session.Ingest(new TelemetrySample
        {
            Timestamp = t0.AddSeconds(5),
            SimOnGround = false,
            Latitude = 43.346046,
            Longitude = 5.344348,
            AltitudeFeet = 3594,
            AglFeet = 3500,
            RadioHeightFeet = 3500,
            HeadingTrueDeg = 313,
            GroundSpeedKts = 170,
            AirspeedKts = 170,
            VerticalSpeedFpm = -700,
            GearHandlePosition = 0,
            FlapsHandleIndex = 2,
            GForce = 1
        });

        // Short final ~1.0 NM on a perfect 3° path to the normalized aim point (threshold + 1,000 ft).
        var elev = challenge.Runway.ElevationFeet;
        var perfectAlt = RunwayPathGeometry.ExpectedAltitudeFeet(1.0, elev);
        // Point ~1 NM along runway heading from threshold
        var h = challenge.Runway.HeadingTrueDeg * Math.PI / 180.0;
        var nm = 1.0;
        var m = nm * 1852.0;
        var dLat = (m * Math.Cos(h)) / 111320.0;
        var dLon = (m * Math.Sin(h)) / (111320.0 * Math.Cos(challenge.Runway.ThresholdLatitude * Math.PI / 180.0));
        // Approach is FROM outside toward threshold, so go opposite runway heading.
        var lat1 = challenge.Runway.ThresholdLatitude - dLat;
        var lon1 = challenge.Runway.ThresholdLongitude - dLon;

        for (var i = 0; i < 10; i++)
        {
            session.Ingest(new TelemetrySample
            {
                Timestamp = t0.AddSeconds(6 + i * 0.2),
                SimOnGround = false,
                Latitude = lat1,
                Longitude = lon1,
                AltitudeFeet = perfectAlt,
                AglFeet = perfectAlt - elev,
                RadioHeightFeet = perfectAlt - elev,
                HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
                GroundSpeedKts = 140,
                AirspeedKts = 140,
                VerticalSpeedFpm = -700,
                GearHandlePosition = 1,
                FlapsHandleIndex = 3,
                GForce = 1
            });
        }

        // Force finalize by touchdown + settle.
        var td = t0.AddSeconds(10);
        session.Ingest(new TelemetrySample
        {
            Timestamp = td,
            SimOnGround = true,
            Latitude = challenge.Runway.ThresholdLatitude,
            Longitude = challenge.Runway.ThresholdLongitude,
            AltitudeFeet = elev,
            AglFeet = 0,
            RadioHeightFeet = 0,
            HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
            GroundSpeedKts = 120,
            AirspeedKts = 138,
            VerticalSpeedFpm = -150,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            GForce = 1.1
        });
        // Past reverse-thrust / spoiler / brake gate windows and settle hold (GS < 50 for ≥1 s).
        for (var i = 1; i <= 14; i++)
        {
            session.Ingest(new TelemetrySample
            {
                Timestamp = td.AddSeconds(i * 0.5),
                SimOnGround = true,
                Latitude = challenge.Runway.ThresholdLatitude,
                Longitude = challenge.Runway.ThresholdLongitude,
                AltitudeFeet = elev,
                AglFeet = 0,
                RadioHeightFeet = 0,
                HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
                GroundSpeedKts = i < 10 ? 80 : 20,
                AirspeedKts = 80,
                VerticalSpeedFpm = 0,
                GearHandlePosition = 1,
                FlapsHandleIndex = 3,
                GForce = 1
            });
        }

        Assert.True(session.IsComplete || session.Phase is LandingPhase.Settled or LandingPhase.Scored,
            $"Expected settled after gate windows; phase={session.Phase}");
        // Perfect short final should yield near-zero average bias / variation; high spawn must not pollute.
        Assert.True(session.Snapshot.ApproachPathSampleCount >= 2);
        Assert.True(session.Snapshot.ApproachPathRms < 80,
            $"Expected short-final RMS near 0, got {session.Snapshot.ApproachPathRms}");
        Assert.True(session.Snapshot.ApproachGlideslopeMeanAbsFt < 50,
            $"Expected near-zero mean absolute GS error, got {session.Snapshot.ApproachGlideslopeMeanAbsFt}");
        Assert.True(session.Snapshot.ApproachVerticalVariationFtPerSec < 5,
            $"Expected calm vertical variation, got {session.Snapshot.ApproachVerticalVariationFtPerSec}");
    }

    [Fact]
    public void CleanMetrics_WipesSamplesAndRearmsWithoutIdling()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        var elev = challenge.Runway.ElevationFeet;
        for (var i = 0; i < 12; i++)
        {
            session.Ingest(new TelemetrySample
            {
                Timestamp = t0.AddSeconds(5 + i * 0.2),
                SimOnGround = false,
                Latitude = challenge.Runway.ThresholdLatitude - 0.02,
                Longitude = challenge.Runway.ThresholdLongitude + 0.02,
                AltitudeFeet = elev + 800,
                AglFeet = 800,
                RadioHeightFeet = 800,
                HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
                GroundSpeedKts = 140,
                AirspeedKts = 140,
                VerticalSpeedFpm = -700,
                GearHandlePosition = 1,
                FlapsHandleIndex = 3,
                GForce = 1
            });
        }

        Assert.True(session.Snapshot.ApproachSamples.Count > 0);
        Assert.NotEqual(LandingPhase.Armed, session.Phase);

        session.CleanMetrics();

        Assert.Equal(LandingPhase.Armed, session.Phase);
        Assert.Empty(session.Snapshot.ApproachSamples);
        Assert.Empty(session.Snapshot.RolloutSamples);
        Assert.Null(session.Snapshot.Touchdown);
        Assert.Equal(0, session.Snapshot.ApproachPathSampleCount);
        Assert.Equal(0, session.Snapshot.ApproachGlideslopeMeanAbsFt);
        Assert.True(session.IsArmed);
        Assert.False(session.IsComplete);
    }

    [Fact]
    public void RefreshDerivedMetrics_PopulatesApproachBeforeSettle()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        var elev = challenge.Runway.ElevationFeet;
        var h = challenge.Runway.HeadingTrueDeg * Math.PI / 180.0;
        var nm = 1.0;
        var m = nm * 1852.0;
        var dLat = (m * Math.Cos(h)) / 111320.0;
        var dLon = (m * Math.Sin(h)) / (111320.0 * Math.Cos(challenge.Runway.ThresholdLatitude * Math.PI / 180.0));
        var lat = challenge.Runway.ThresholdLatitude - dLat;
        var lon = challenge.Runway.ThresholdLongitude - dLon;
        var perfectAlt = RunwayPathGeometry.ExpectedAltitudeFeet(nm, elev);

        for (var i = 0; i < 12; i++)
        {
            session.Ingest(new TelemetrySample
            {
                Timestamp = t0.AddSeconds(5 + i * 0.25),
                SimOnGround = false,
                Latitude = lat,
                Longitude = lon,
                AltitudeFeet = perfectAlt + 20, // slight high bias
                AglFeet = perfectAlt + 20 - elev,
                RadioHeightFeet = perfectAlt + 20 - elev,
                HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
                GroundSpeedKts = 140,
                AirspeedKts = 140,
                VerticalSpeedFpm = -700,
                GearHandlePosition = 1,
                FlapsHandleIndex = 3,
                GForce = 1
            });
        }

        // Mid-approach, before touchdown/settle — live path must already expose approach metrics.
        Assert.True(session.Phase is LandingPhase.Approach or LandingPhase.Flare);
        Assert.True(session.Snapshot.ApproachPathSampleCount >= 2);
        Assert.True(session.Snapshot.ApproachMetricDurationSec >= 0.5);
        Assert.InRange(session.Snapshot.ApproachGlideslopeMeanAbsFt, 5, 50);
    }

    [Fact]
    public void ApproachMetrics_AlternatingErrorsDoNotCancelAndCountAsPumping()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        var elev = challenge.Runway.ElevationFeet;
        var h = challenge.Runway.HeadingTrueDeg * Math.PI / 180.0;
        // ~1 NM short final, fixed ground point; alternate +200 / −200 ft path error → mean ≈ 0, high variation.
        var nm = 1.0;
        var m = nm * 1852.0;
        var dLat = (m * Math.Cos(h)) / 111320.0;
        var dLon = (m * Math.Sin(h)) / (111320.0 * Math.Cos(challenge.Runway.ThresholdLatitude * Math.PI / 180.0));
        var lat = challenge.Runway.ThresholdLatitude - dLat;
        var lon = challenge.Runway.ThresholdLongitude - dLon;
        var perfectAlt = RunwayPathGeometry.ExpectedAltitudeFeet(nm, elev);

        for (var i = 0; i < 20; i++)
        {
            var err = (i % 2 == 0) ? 200.0 : -200.0;
            session.Ingest(new TelemetrySample
            {
                Timestamp = t0.AddSeconds(5 + i * 0.5),
                SimOnGround = false,
                Latitude = lat,
                Longitude = lon,
                AltitudeFeet = perfectAlt + err,
                AglFeet = perfectAlt + err - elev,
                RadioHeightFeet = perfectAlt + err - elev,
                HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
                GroundSpeedKts = 140,
                AirspeedKts = 140,
                VerticalSpeedFpm = -700,
                GearHandlePosition = 1,
                FlapsHandleIndex = 3,
                GForce = 1
            });
        }

        var td = t0.AddSeconds(20);
        session.Ingest(new TelemetrySample
        {
            Timestamp = td,
            SimOnGround = true,
            Latitude = challenge.Runway.ThresholdLatitude,
            Longitude = challenge.Runway.ThresholdLongitude,
            AltitudeFeet = elev,
            AglFeet = 0,
            RadioHeightFeet = 0,
            HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
            GroundSpeedKts = 120,
            AirspeedKts = 138,
            VerticalSpeedFpm = -150,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            GForce = 1.1
        });
        for (var i = 1; i <= 8; i++)
        {
            session.Ingest(new TelemetrySample
            {
                Timestamp = td.AddSeconds(i * 0.5),
                SimOnGround = true,
                Latitude = challenge.Runway.ThresholdLatitude,
                Longitude = challenge.Runway.ThresholdLongitude,
                AltitudeFeet = elev,
                AglFeet = 0,
                RadioHeightFeet = 0,
                HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
                GroundSpeedKts = i < 6 ? 80 : 20,
                AirspeedKts = 80,
                VerticalSpeedFpm = 0,
                GearHandlePosition = 1,
                FlapsHandleIndex = 3,
                GForce = 1
            });
        }

        Assert.True(session.Snapshot.ApproachMetricDurationSec >= 0.5);
        // Mean absolute error cannot cancel even though the signed mean is approximately zero.
        Assert.True(session.Snapshot.ApproachGlideslopeMeanAbsFt > 50,
            $"Alternating errors should retain material MAE, got {session.Snapshot.ApproachGlideslopeMeanAbsFt}");
        // Repeated reversals remain very unsteady after the one-way net correction is removed.
        Assert.True(session.Snapshot.ApproachVerticalVariationFtPerSec > 50,
            $"Expected large vertical variation, got {session.Snapshot.ApproachVerticalVariationFtPerSec}");
    }

    [Fact]
    public void GroundEdgeBeforeAirborneGate_IsIgnored_ThenRealTdWorks()
    {
        var (challenge, settings) = Load();
        // Force strict airborne gate
        settings = settings with
        {
            PostArmIgnoreSeconds = 0.5,
            RequireAirborneBeforeTouchdown = true,
            MinAirborneSamples = 5,
            MinAirborneAglFeet = 80
        };

        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        // After grace, still grounded (restart-on-runway) — no TD.
        session.Ingest(Sample(t0.AddSeconds(1.0), onGround: true, agl: 0, gs: 10));
        session.Ingest(Sample(t0.AddSeconds(1.2), onGround: true, agl: 0, gs: 10));
        Assert.Null(session.Snapshot.Touchdown);

        // Get airborne (restart succeeded later / fly out).
        for (var i = 0; i < 8; i++)
            session.Ingest(Sample(t0.AddSeconds(2 + i * 0.15), onGround: false, agl: 300, vs: -100));

        // Real touchdown edge.
        session.Ingest(Sample(t0.AddSeconds(4), onGround: true, agl: 0, vs: -180, gs: 130));
        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.Equal(-180, session.Snapshot.VerticalSpeedAtTouchdownFpm, 3);
    }
}
