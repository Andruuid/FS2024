using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Tests;

public sealed class TouchdownPointScoringTests
{
    private const double MetersPerFoot = 0.3048;

    [Theory]
    [InlineData(1_400, 100.0)]
    [InlineData(1_000, 62.5)]
    [InlineData(1_100, 75.0)]
    [InlineData(1_700, 60.0)]
    [InlineData(500, 0.0)]
    [InlineData(2_000, 0.0)]
    public void LandingScorer_MatchesPublishedExamples(double touchdownFeet, double expectedPercent)
    {
        Assert.Equal(expectedPercent, LandingScorer.Score(1_000, touchdownFeet), 6);
    }

    [Theory]
    [InlineData(500, 0.0)]
    [InlineData(1_299.999, 100.0)]
    [InlineData(1_300, 100.0)]
    [InlineData(1_500, 100.0)]
    [InlineData(1_500.001, 100.0)]
    [InlineData(2_000, 0.0)]
    [InlineData(-10_000, 0.0)]
    [InlineData(10_000, 0.0)]
    public void LandingScorer_UsesInclusiveBandBoundariesClampingAndRounding(
        double touchdownFeet,
        double expectedPercent)
    {
        Assert.Equal(expectedPercent, LandingScorer.Score(1_000, touchdownFeet), 6);
    }

    [Fact]
    public void LandingScorer_IsMoreSevereForAnEqualLongMiss()
    {
        Assert.Equal(75.0, LandingScorer.Score(1_000, 1_100));
        Assert.Equal(60.0, LandingScorer.Score(1_000, 1_700));
        Assert.Equal(66.7, LandingScorer.Score(1_000, 1_666.5));
    }

    [Theory]
    [InlineData(-1, 1_000, 300, 500, 800, 500)]
    [InlineData(1_000, double.NaN, 300, 500, 800, 500)]
    [InlineData(1_000, 1_400, -1, 500, 800, 500)]
    [InlineData(1_000, 1_400, 500, 500, 800, 500)]
    [InlineData(1_000, 1_400, 300, 500, 0, 500)]
    [InlineData(1_000, 1_400, 300, 500, 800, double.PositiveInfinity)]
    public void LandingScorer_RejectsInvalidParameters(
        double marker,
        double touchdown,
        double near,
        double far,
        double shortSpan,
        double longSpan)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            LandingScorer.Score(marker, touchdown, near, far, shortSpan, longSpan));
    }

    [Theory]
    [InlineData(0, 1_050, 0)]
    [InlineData(90, 1_250, 35)]
    [InlineData(225, -80, -20)]
    public void Calculator_ProjectsSignedAlongRunwayDistanceIndependentlyOfLateralOffset(
        double headingDeg,
        double alongFeet,
        double lateralMeters)
    {
        var runway = Runway(headingDeg, 6_000 * MetersPerFoot);
        var (latitude, longitude) = Project(runway, alongFeet, lateralMeters);
        var touchdown = new TelemetrySample { Latitude = latitude, Longitude = longitude };

        var available = TouchdownPointCalculator.TryCalculate(
            runway,
            touchdown,
            out var result,
            out var reason);

        Assert.True(available, reason);
        Assert.Equal(6_000, result.RunwayLengthFeet, 6);
        Assert.Equal(984, result.AimingMarkerDistanceFeet);
        Assert.Equal(1_284, result.IdealNearDistanceFeet);
        Assert.Equal(1_484, result.IdealFarDistanceFeet);
        Assert.Equal(alongFeet, result.ActualDistanceFeet, 3);
        Assert.Equal(alongFeet - 984, result.OffsetFromAimingMarkerFeet, 3);
        var expectedBandDistance = alongFeet < 1_284
            ? alongFeet - 1_284
            : alongFeet > 1_484
                ? alongFeet - 1_484
                : 0;
        Assert.Equal(expectedBandDistance, result.SignedDistanceFromIdealBandFeet, 3);
    }

    [Fact]
    public void RunwaySpecificAimingMarker_OverridesLengthRule()
    {
        var runway = Runway(0, 8_000 * MetersPerFoot);
        runway.AimingMarkerStartM = 830 * MetersPerFoot;

        var measurement = Measure(runway, 1_250);

        Assert.Equal(830, measurement.AimingMarkerDistanceFeet, 6);
        Assert.Equal(1_130, measurement.IdealNearDistanceFeet, 6);
        Assert.Equal(1_330, measurement.IdealFarDistanceFeet, 6);
        Assert.Equal(0, measurement.SignedDistanceFromIdealBandFeet, 3);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(50, 100)]
    [InlineData(50.001, 90)]
    [InlineData(100, 90)]
    [InlineData(100.001, 75)]
    [InlineData(200, 75)]
    [InlineData(200.001, 50)]
    [InlineData(300, 50)]
    [InlineData(300.001, 25)]
    [InlineData(500, 25)]
    [InlineData(500.001, 0)]
    public void HistoricalUpperBoundEvaluator_RemainsAvailableForOldProfiles(
        double errorFeet,
        double expectedPercent)
    {
        var actual = UpperBoundBandsEvaluator.Instance.Evaluate(errorFeet, HistoricalTouchdownMetric());
        Assert.Equal(expectedPercent, actual * 100, 6);
    }

    [Fact]
    public void Metric_ScoresShortAndLongMissesAsymmetricallyAndReportsActualPoint()
    {
        var (key, challenge) = LoadProfile();
        challenge.Runway = Runway(73, 8_000 * MetersPerFoot);
        challenge.Runway.CountryCode = "US";
        challenge.Runway.AimingMarkerStartM = 1_000 * MetersPerFoot;
        var inside = PreviewAt(key, challenge, 1_400);
        var shortResult = PreviewAt(key, challenge, 1_100);
        var longResult = PreviewAt(key, challenge, 1_700);

        var insideMetric = inside.Criteria.Single(c => c.Id == "touchdown_point");
        var shortMetric = shortResult.Criteria.Single(c => c.Id == "touchdown_point");
        var longMetric = longResult.Criteria.Single(c => c.Id == "touchdown_point");
        Assert.Equal(100, insideMetric.ScorePercent);
        Assert.Equal(75, shortMetric.ScorePercent);
        Assert.Equal(60, longMetric.ScorePercent);
        Assert.Equal(1_100, shortMetric.RawValue!.Value, 3);
        Assert.Equal(22, shortMetric.PhaseImportancePercent);
        Assert.Equal(13.2, shortMetric.MaxOverallPoints, 6);
        Assert.Contains("aiming marker 1000.0 ft", insideMetric.Note, StringComparison.Ordinal);
        Assert.Contains("ideal band 1300.0-1500.0 ft", insideMetric.Note, StringComparison.Ordinal);
        Assert.Contains("200.0 ft short", shortMetric.Note, StringComparison.Ordinal);
        Assert.Contains("200.0 ft long", longMetric.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void LandingSession_KeepsTheFirstAcceptedMainGearTouchdownPosition()
    {
        var (key, challenge) = LoadProfile();
        challenge.Runway = Runway(90, 8_000 * MetersPerFoot);
        challenge.Runway.AimingMarkerStartM = 984 * MetersPerFoot;
        var session = new LandingSession(challenge, key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false,
            OperationalGates = new OperationalGateSessionSettings()
        });

        session.Arm();
        session.Ingest(SessionSample(challenge.Runway, 0, -100, contact: false));
        session.Ingest(SessionSample(challenge.Runway, 1, 1_384, contact: true));
        session.Ingest(SessionSample(challenge.Runway, 2, 1_700, contact: true));

        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.True(TouchdownPointCalculator.TryCalculate(
            challenge.Runway,
            session.Snapshot.Touchdown,
            out var measurement,
            out var reason), reason);
        Assert.Equal(1_384, measurement.ActualDistanceFeet, 3);
        Assert.Equal(0, measurement.AbsoluteDistanceFromIdealBandFeet, 3);
    }

    [Fact]
    public void InvalidTouchdownGeometry_IsUnavailableAndFinalResultIsUnranked()
    {
        var (key, challenge) = LoadProfile();
        var snapshot = new LandingSnapshot
        {
            Touchdown = new TelemetrySample
            {
                Latitude = double.NaN,
                Longitude = challenge.Runway.ThresholdLongitude
            }
        };

        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);
        var metric = result.Criteria.Single(c => c.Id == "touchdown_point");

        Assert.Equal(MetricStatus.Unavailable, metric.Status);
        Assert.Contains("position", metric.UnavailableReason, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.IsRanked);
        Assert.Null(result.ScorePercent);
        Assert.Null(result.LandingVisualization);
    }

    [Fact]
    public void ScoreResult_CapturesSelfContainedVersionThreeLandingVisualization()
    {
        var (key, challenge) = LoadProfile();
        challenge.Runway = Runway(73, 8_000 * MetersPerFoot);
        challenge.Runway.AirportIcao = "KTVS";
        challenge.Runway.RunwayId = "09L";
        challenge.Runway.AimingMarkerStartM = 1_000 * MetersPerFoot;
        challenge.Runway.AimingMarkerLengthM = 45;
        challenge.Runway.AimingMarkerCenterM = challenge.Runway.AimingMarkerStartM + 22.5;
        challenge.Runway.IdealTouchdownDistanceM = 830 * MetersPerFoot;
        challenge.Runway.AimingMarkerSource = "OurAirports CSV";
        challenge.Runway.AimingMarkerConfidence = "Dataset";
        var (latitude, longitude) = Project(challenge.Runway, 1_400, 12.5);
        var snapshot = new LandingSnapshot
        {
            Touchdown = new TelemetrySample { Latitude = latitude, Longitude = longitude },
            TouchdownLateralOffsetM = 12.5,
            TouchdownHeadingErrorDeg = -2.25,
            BankAtTouchdownDeg = 3.5,
            PitchAtTouchdownDeg = 5.75,
            VerticalSpeedAtTouchdownFpm = -236,
            AirspeedAtTouchdownKts = 139,
            TargetTouchdownIasKts = 138,
            PeakGForce = 1.41,
            InitialImpact = new ImpactAnalysis(
                true, false, 10, -236, "test", 1.47, 1.36, 6, 1.0, null)
        };

        var result = new ScoreEngine(key).EvaluatePreview(challenge, snapshot);
        var visual = Assert.IsType<LandingVisualizationData>(result.LandingVisualization);

        Assert.Equal(3, visual.Version);
        Assert.Equal("KTVS", visual.AirportIcao);
        Assert.Equal("09L", visual.RunwayId);
        Assert.Equal(1_400 * MetersPerFoot, visual.TouchdownDistanceFromThresholdM, 3);
        Assert.Equal(1_400 * MetersPerFoot, visual.IdealTouchdownDistanceFromThresholdM, 6);
        Assert.Equal(1_300 * MetersPerFoot, visual.IdealTouchdownNearDistanceFromThresholdM!.Value, 6);
        Assert.Equal(1_500 * MetersPerFoot, visual.IdealTouchdownFarDistanceFromThresholdM!.Value, 6);
        Assert.Equal(1_000 * MetersPerFoot, visual.AimingMarkerStartDistanceFromThresholdM!.Value, 6);
        Assert.Equal(45, visual.AimingMarkerNominalLengthM);
        Assert.Equal(1_000 * MetersPerFoot + 22.5, visual.AimingMarkerCenterDistanceFromThresholdM!.Value, 6);
        Assert.Equal("OurAirports CSV", visual.AimingMarkerSource);
        Assert.Equal("Dataset", visual.AimingMarkerConfidence);
        Assert.Equal(12.5, visual.TouchdownLateralOffsetM);
        Assert.Equal(-2.25, visual.TouchdownHeadingErrorDeg);
        Assert.Equal(1_400, result.Criteria.Single(c => c.Id == "touchdown_point").RawValue!.Value, 3);
    }

    [Fact]
    public void TouchdownPointValidation_RejectsInvalidOffsetsAndSpans()
    {
        var (key, _) = LoadProfile();
        var metric = key.Phases.SelectMany(p => p.Metrics)
            .Single(m => m.Id == "touchdown_point");
        metric.Params["idealNearOffsetFt"] = -1;
        metric.Params["idealFarOffsetFt"] = -2;
        metric.Params["shortSpanFt"] = 0;
        metric.Params["longSpanFt"] = double.NaN;

        var errors = EvaluationKeyValidator.Validate(key);

        Assert.Contains(errors, error => error.Contains("idealNearOffsetFt", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("idealFarOffsetFt", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("shortSpanFt", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("longSpanFt", StringComparison.Ordinal));
    }

    private static TouchdownPointMeasurement Measure(RunwayConfig runway, double alongFeet)
    {
        var (latitude, longitude) = Project(runway, alongFeet, 0);
        Assert.True(TouchdownPointCalculator.TryCalculate(
            runway,
            new TelemetrySample { Latitude = latitude, Longitude = longitude },
            out var measurement,
            out var reason), reason);
        return measurement;
    }

    private static ScoreResult PreviewAt(
        LandingEvaluationKey key,
        ChallengeConfig challenge,
        double alongFeet)
    {
        var (latitude, longitude) = Project(challenge.Runway, alongFeet, 0);
        return new ScoreEngine(key).EvaluatePreview(challenge, new LandingSnapshot
        {
            Touchdown = new TelemetrySample { Latitude = latitude, Longitude = longitude }
        });
    }

    private static TelemetrySample SessionSample(
        RunwayConfig runway,
        double timeSeconds,
        double alongFeet,
        bool contact)
    {
        var (latitude, longitude) = Project(runway, alongFeet, 0);
        return new TelemetrySample
        {
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(timeSeconds),
            SimulationTimeSeconds = timeSeconds,
            Latitude = latitude,
            Longitude = longitude,
            HeadingTrueDeg = runway.HeadingTrueDeg,
            SimOnGround = contact,
            AglFeet = contact ? 0 : 100,
            RadioHeightFeet = contact ? 0 : 100,
            GroundSpeedKts = 100,
            AirspeedKts = 100,
            VerticalSpeedFpm = contact ? -100 : -500,
            TouchdownNormalVelocityFps = contact ? 100.0 / 60.0 : null,
            GForce = contact ? 1.2 : 1,
            IsGearWheels = true,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            GearOnGroundByIndex = new Dictionary<int, bool>
            {
                [0] = false,
                [1] = contact,
                [2] = contact
            }
        };
    }

    private static EvaluationMetric HistoricalTouchdownMetric() => new()
    {
        Id = "touchdown_point",
        DisplayName = "Touchdown point",
        Evaluator = "upperBoundBands",
        Metric = "touchdownPointErrorFt",
        Points =
        [
            new ScorePoint { V = 50, S = 100 },
            new ScorePoint { V = 100, S = 90 },
            new ScorePoint { V = 200, S = 75 },
            new ScorePoint { V = 300, S = 50 },
            new ScorePoint { V = 500, S = 25 }
        ]
    };

    private static RunwayConfig Runway(double headingDeg, double lengthM) => new()
    {
        AirportIcao = "TEST",
        RunwayId = "01",
        ThresholdLatitude = 45,
        ThresholdLongitude = 7,
        HeadingTrueDeg = headingDeg,
        ElevationFeet = 1_000,
        LengthM = lengthM,
        WidthM = 45
    };

    private static (double Latitude, double Longitude) Project(
        RunwayConfig runway,
        double alongFeet,
        double lateralMeters)
    {
        const double earthRadiusMeters = 6_371_000;
        var heading = runway.HeadingTrueDeg * Math.PI / 180.0;
        var alongMeters = alongFeet * MetersPerFoot;
        var northMeters = alongMeters * Math.Cos(heading) - lateralMeters * Math.Sin(heading);
        var eastMeters = alongMeters * Math.Sin(heading) + lateralMeters * Math.Cos(heading);
        var latitude = runway.ThresholdLatitude
                       + northMeters / earthRadiusMeters * 180.0 / Math.PI;
        var longitude = runway.ThresholdLongitude
                        + eastMeters / (earthRadiusMeters
                                        * Math.Cos(runway.ThresholdLatitude * Math.PI / 180.0))
                        * 180.0 / Math.PI;
        return (latitude, longitude);
    }

    private static (LandingEvaluationKey Key, ChallengeConfig Challenge) LoadProfile()
    {
        var root = FindRepositoryRoot();
        var loader = new ConfigLoader(Path.Combine(root, "config"));
        var loaded = loader.LoadEvaluationKey();
        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        return (loaded.Key!, loader.LoadChallenge("challenges/barcelona-crosswind-final.json"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChallengeLab.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root not found.");
    }
}
