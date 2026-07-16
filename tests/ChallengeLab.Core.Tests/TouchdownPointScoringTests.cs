using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Tests;

public sealed class TouchdownPointScoringTests
{
    private const double MetersPerFoot = 0.3048;

    [Theory]
    [InlineData(5_999.9, 1_100)]
    [InlineData(6_000.0, 1_100)]
    [InlineData(6_000.1, 1_200)]
    public void PerfectPoint_UsesInclusiveSixThousandFootBoundary(
        double runwayLengthFeet,
        double expectedFeet)
    {
        Assert.Equal(
            expectedFeet,
            TouchdownPointCalculator.PerfectTouchdownPointFeet(runwayLengthFeet));
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
        Assert.Equal(1_100, result.PerfectDistanceFeet);
        Assert.Equal(alongFeet, result.ActualDistanceFeet, 3);
        Assert.Equal(alongFeet - 1_100, result.SignedErrorFeet, 3);
        Assert.Equal(Math.Abs(alongFeet - 1_100), result.AbsoluteErrorFeet, 3);
    }

    [Fact]
    public void Calculator_ConvertsMetersBeforeChoosingPerfectPoint()
    {
        var atBoundary = Measure(Runway(0, 6_000 * MetersPerFoot), 1_100);
        var aboveBoundary = Measure(Runway(0, 6_000.1 * MetersPerFoot), 1_200);

        Assert.Equal(1_100, atBoundary.PerfectDistanceFeet);
        Assert.Equal(1_200, aboveBoundary.PerfectDistanceFeet);
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
    public void UpperBoundBands_UseInclusiveCeilings(double errorFeet, double expectedPercent)
    {
        var actual = UpperBoundBandsEvaluator.Instance.Evaluate(errorFeet, TouchdownMetric());
        Assert.Equal(expectedPercent, actual * 100, 6);
    }

    [Fact]
    public void Metric_ScoresEarlyAndLateErrorsSymmetricallyAndReportsActualPoint()
    {
        var (key, challenge) = LoadProfile();
        challenge.Runway = Runway(73, 8_000 * MetersPerFoot);
        var early = PreviewAt(key, challenge, 1_125);
        var late = PreviewAt(key, challenge, 1_275);

        var earlyMetric = early.Criteria.Single(c => c.Id == "touchdown_point");
        var lateMetric = late.Criteria.Single(c => c.Id == "touchdown_point");
        Assert.Equal(90, earlyMetric.ScorePercent);
        Assert.Equal(earlyMetric.ScorePercent, lateMetric.ScorePercent);
        Assert.Equal(1_125, earlyMetric.RawValue!.Value, 3);
        Assert.Equal(20, earlyMetric.PhaseImportancePercent);
        Assert.Equal(14, earlyMetric.MaxOverallPoints);
        Assert.Contains("perfect point 1200.0 ft", earlyMetric.Note, StringComparison.Ordinal);
        Assert.Contains("error -75.0 ft (early)", earlyMetric.Note, StringComparison.Ordinal);
        Assert.Contains("error +75.0 ft (late)", lateMetric.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void LandingSession_KeepsTheFirstAcceptedMainGearTouchdownPosition()
    {
        var (key, challenge) = LoadProfile();
        challenge.Runway = Runway(90, 8_000 * MetersPerFoot);
        var session = new LandingSession(challenge, key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false,
            OperationalGates = new OperationalGateSessionSettings()
        });

        session.Arm();
        session.Ingest(SessionSample(challenge.Runway, 0, -100, contact: false));
        session.Ingest(SessionSample(challenge.Runway, 1, 1_200, contact: true));
        session.Ingest(SessionSample(challenge.Runway, 2, 1_700, contact: true));

        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.True(TouchdownPointCalculator.TryCalculate(
            challenge.Runway,
            session.Snapshot.Touchdown,
            out var measurement,
            out var reason), reason);
        Assert.Equal(1_200, measurement.ActualDistanceFeet, 3);
        Assert.Equal(0, measurement.AbsoluteErrorFeet, 3);
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
    public void ScoreResult_CapturesSelfContainedLandingVisualization()
    {
        var (key, challenge) = LoadProfile();
        challenge.Runway = Runway(73, 8_000 * MetersPerFoot);
        challenge.Runway.AirportIcao = "KTVS";
        challenge.Runway.RunwayId = "09L";
        var (latitude, longitude) = Project(challenge.Runway, 1_275, 12.5);
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

        Assert.Equal(LandingVisualizationData.CurrentVersion, visual.Version);
        Assert.Equal("KTVS", visual.AirportIcao);
        Assert.Equal("09L", visual.RunwayId);
        Assert.Equal(73, visual.RunwayHeadingTrueDeg);
        Assert.Equal(8_000 * MetersPerFoot, visual.RunwayLengthM, 6);
        Assert.Equal(45, visual.RunwayWidthM);
        Assert.Equal(1_275 * MetersPerFoot, visual.TouchdownDistanceFromThresholdM, 3);
        Assert.Equal(1_200 * MetersPerFoot, visual.IdealTouchdownDistanceFromThresholdM, 6);
        Assert.Equal(12.5, visual.TouchdownLateralOffsetM);
        Assert.Equal(-2.25, visual.TouchdownHeadingErrorDeg);
        Assert.Equal(3.5, visual.TouchdownBankDeg);
        Assert.Equal(5.75, visual.TouchdownPitchDeg);
        Assert.Equal(-236, visual.TouchdownVerticalSpeedFpm);
        Assert.Equal(1.47, visual.TouchdownRawPeakG);
        Assert.Equal(1.36, visual.TouchdownRobustPeakG);
        Assert.Equal(139, visual.TouchdownAirspeedKts);
        Assert.Equal(138, visual.TargetTouchdownAirspeedKts);
        Assert.Equal(1_275, result.Criteria.Single(c => c.Id == "touchdown_point").RawValue!.Value, 3);
    }

    [Fact]
    public void UpperBoundBandsValidation_RejectsUnorderedAndDuplicateBounds()
    {
        var (key, _) = LoadProfile();
        var metric = key.Phases.SelectMany(p => p.Metrics)
            .Single(m => m.Id == "touchdown_point");
        metric.Points =
        [
            new ScorePoint { V = 100, S = 90 },
            new ScorePoint { V = 50, S = 100 },
            new ScorePoint { V = 50, S = 75 }
        ];

        var errors = EvaluationKeyValidator.Validate(key);

        Assert.Contains(errors, error =>
            error.Contains("strictly increasing", StringComparison.OrdinalIgnoreCase));
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
        var snapshot = new LandingSnapshot
        {
            Touchdown = new TelemetrySample { Latitude = latitude, Longitude = longitude }
        };
        return new ScoreEngine(key).EvaluatePreview(challenge, snapshot);
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

    private static EvaluationMetric TouchdownMetric() => new()
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
