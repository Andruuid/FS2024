using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Tests;

public sealed class ScoreEngineTests
{
    private static string FindConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "catalog.json"))) return Path.Combine(dir.FullName, "config");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("config not found");
    }

    private static (LandingEvaluationKey Key, ChallengeConfig Challenge) Load()
    {
        var loader = new ConfigLoader(FindConfig());
        var loaded = loader.LoadEvaluationKey();
        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        return (loaded.Key!, loader.LoadChallenge("challenges/barcelona-crosswind-final.json"));
    }

    private static RunwayAlignmentAnalysis PassingRunwayAlignment() =>
        new(true, 0.5, 0.5, 0, 1.5, 1.5, 3, 0.5, 0.5, 0.5, 0.5, 20,
            "GPS GROUND TRUE TRACK", null);

    private static LandingSnapshot CompleteSnapshot(
        ChallengeConfig challenge,
        bool gearDown = true,
        int flapsIndex = 3)
    {
        var perfectDistanceFeet = TouchdownPointCalculator.PerfectTouchdownPointFeet(
            challenge.Runway.LengthM / RunwayPathGeometry.MetersPerFoot);
        var (latitude, longitude) = PositionAlongRunway(challenge.Runway, perfectDistanceFeet);
        return new LandingSnapshot
        {
        Touchdown = new TelemetrySample
        {
            Timestamp = DateTimeOffset.UtcNow,
            SimOnGround = true,
            Latitude = latitude,
            Longitude = longitude
        },
        GearDownAtTouchdown = gearDown,
        FlapsIndexAtTouchdown = flapsIndex,
        VerticalSpeedAtTouchdownFpm = -100,
        AirspeedAtTouchdownKts = 138,
        VappKts = 143,
        TargetTouchdownIasKts = 138,
        TouchdownIasErrorKts = 0,
        ExcessSpeedOverVappKts = 0,
        PeakGForce = 1.1,
        TouchdownLateralOffsetM = 1,
        MaxLateralOffsetM = 2,
        TouchdownHeadingErrorDeg = 1,
        RunwayAlignment = PassingRunwayAlignment(),
        ApproachPathRms = 1,
        ApproachPathSampleCount = 3,
        ApproachGlideslopeMeanAbsFt = 20,
        ApproachGlideslopeWeightedDeviationDeg = 0.1,
        ApproachVerticalVariationFtPerSec = 1.5,
        ApproachLateralWeaveIndex = 0.001,
        ApproachLateralDistanceM = 2000,
        ApproachMetricDurationSec = 45,
        PostTouchdownAlignmentSampleCount = 2,
        PostTouchdownAlignmentMeanDeg = 1,
        PostTouchdownAlignmentRmsDeg = 1,
        PostTouchdownAlignmentPeakDeg = 2,
        RolloutPathSampleCount = 3,
        RolloutPathSegmentCount = 2,
        RolloutDistanceM = 10,
        RolloutLateralMeanM = 1,
        RolloutLateralPeakM = 2,
        RolloutWeaveIndex = .01,
        InitialImpact = new ImpactAnalysis(
            true, false, 10, -100, "VERTICAL SPEED (airborne/contact bracket mean)",
            1.2, 1.2, 10, 1.0, null),
        FloatAnalysis = new FloatAnalysis(
            true, false, 0, 0, 0, 0, 0, null),
        ContactStability = new ContactStabilityAnalysis(
            true, Array.Empty<BounceEvent>(), 0, 0, null),
        TouchdownAnalysisComplete = true,
        GateObservations =
        {
            MonitoringStarted = true,
            MonitoringStartTimeSeconds = 0,
            MonitoringStartPauseGeneration = 0,
            PauseCoverageAvailable = true,
            SimulationRateCoverageAvailable = true,
            MinimumSimulationRate = 1,
            CameraStateCoverageAvailable = true,
            CockpitViewExitCount = 0,
            RadioHeightCoverageAvailable = true,
            HeadingAltitudeAutomationCoverageAvailable = true,
            FullAutomationCoverageAvailable = true,
            HeadingAltitudeThresholdObserved = true,
            FullAutomationThresholdObserved = true,
            SpoilerTelemetryCoverageAvailable = true,
            MainGearTouchdownTimeSeconds = 10,
            FirstSpoilerDeploymentTimeSeconds = 11,
            NoseGearContactCoverageAvailable = true,
            ManualBrakeTelemetryCoverageAvailable = true,
            NoseGearTouchdownTimeSeconds = 10.5,
            FirstSimultaneousBrakingTimeSeconds = 11,
            NoseGearImpact = PassingNoseImpact(),
            RolloutDistanceEvaluated = true,
            GroundSpeedKtsAtRolloutCheck = 45,
            RunwayLengthMeters = 3500,
            RemainingRunwayMetersAtSettleSpeed = 2000,
            RequiredRemainingRunwayMeters = 525,
            RolloutEndOfRunwayViolation = false,
            ReverseThrustTelemetryCoverageAvailable = true,
            OperatingEnginesCapturedAtTouchdown = true,
            EngineCountAtTouchdown = 2,
            OperatingEngineIndicesAtTouchdown = new List<int> { 1, 2 },
            FirstReverseSelectionTimeSecondsByEngine = new Dictionary<int, double>
            {
                [1] = 11,
                [2] = 11.5
            },
            PoweredReverseReductionEvaluated = true,
            PoweredReverseReductionCoverageAvailable = true,
            GroundSpeedKtsAtPoweredReverseCheck = 60,
            PoweredReverseReducedAtThreshold = true,
            ReverseThrustStowEvaluated = true,
            ReverseThrustStowCoverageAvailable = true,
            GroundSpeedKtsAtReverseStowCheck = 30,
            ReverseThrustStowedAtThreshold = true
        }
        };
    }

    private static (double Latitude, double Longitude) PositionAlongRunway(
        RunwayConfig runway,
        double distanceFeet)
    {
        const double earthRadiusMeters = 6_371_000;
        var distanceMeters = distanceFeet * RunwayPathGeometry.MetersPerFoot;
        var heading = runway.HeadingTrueDeg * Math.PI / 180.0;
        var north = distanceMeters * Math.Cos(heading);
        var east = distanceMeters * Math.Sin(heading);
        return (
            runway.ThresholdLatitude + north / earthRadiusMeters * 180.0 / Math.PI,
            runway.ThresholdLongitude
            + east / (earthRadiusMeters * Math.Cos(runway.ThresholdLatitude * Math.PI / 180.0))
            * 180.0 / Math.PI);
    }

    private static NoseGearImpactAnalysis PassingNoseImpact() => new()
    {
        CoverageSufficient = true,
        NoseGearContactCoverageAvailable = true,
        GForceCoverageAvailable = true,
        CompressionFallbackUsed = true,
        Events =
        {
            new NoseGearImpactEvent
            {
                ContactTimeSeconds = 10.5,
                MedianPreContactG = 1,
                RawPeakG = 1.1,
                RobustPeakG = 1.1,
                DeltaG = 0.1,
                ValidPostContactSamples = 8,
                CompressionFallbackUsed = true
            }
        },
        WorstEvent = new NoseGearImpactEvent
        {
            ContactTimeSeconds = 10.5,
            MedianPreContactG = 1,
            RawPeakG = 1.1,
            RobustPeakG = 1.1,
            DeltaG = 0.1,
            ValidPostContactSamples = 8,
            CompressionFallbackUsed = true
        }
    };

    [Fact]
    public void Piecewise_UsesPercentScale_IncludingOnePercent()
    {
        var metric = new EvaluationMetric { Evaluator = "piecewise", Points = new() { new() { V = 0, S = 1 }, new() { V = 1, S = 100 } } };
        Assert.Equal(.01, PiecewiseEvaluator.Instance.Evaluate(0, metric), 6);
        Assert.Equal(1, PiecewiseEvaluator.Instance.Evaluate(1, metric), 6);
    }

    [Fact]
    public void TouchdownImpactVerticalSpeed_ExtremeHardLanding_ScoresZeroNotSeventy()
    {
        var (key, _) = Load();
        var metric = key.Phases
            .SelectMany(p => p.Metrics)
            .Single(m => m.Id == "touchdown_impact");
        var curve = metric.Curves["verticalSpeed"];

        // Catastrophic sink rates must not clamp to the old −600→70% end score.
        Assert.Equal(0, CompositeScoringMath.PiecewiseScorePercent(-9805, curve), 6);
        Assert.Equal(0, CompositeScoringMath.PiecewiseScorePercent(-2000, curve), 6);
        Assert.True(CompositeScoringMath.PiecewiseScorePercent(-1200, curve) < 20);
        Assert.Equal(70, CompositeScoringMath.PiecewiseScorePercent(-600, curve), 6);
        Assert.Equal(100, CompositeScoringMath.PiecewiseScorePercent(-100, curve), 6);
    }

    [Fact]
    public void Hierarchy_WeightsAreAppliedExactlyOnce()
    {
        var (key, challenge) = Load();
        var result = new ScoreEngine(key).Evaluate(challenge, CompleteSnapshot(challenge));
        Assert.True(result.IsRanked, string.Join("; ", result.IncompleteReasons));
        Assert.NotNull(result.ScorePercent);
        var literalWeightedTotal = result.PhaseScores.Sum(p => p.ScorePercent!.Value * p.WeightPercent / 100.0);
        Assert.Equal(Math.Round(literalWeightedTotal, 1), result.ScorePercent!.Value, 6);
        Assert.Equal(60, result.PhaseScores.Single(p => p.PhaseId == "touchdown").WeightPercent);
        Assert.Equal(30, result.PhaseScores.Single(p => p.PhaseId == "approach").WeightPercent);
        Assert.Equal(10, result.PhaseScores.Single(p => p.PhaseId == "rollout").WeightPercent);
    }

    [Fact]
    public void PerfectResult_RemainsExactlyOneHundredPercent()
    {
        var (key, challenge) = Load();
        var result = new ScoreEngine(key).Evaluate(challenge, CompleteSnapshot(challenge));
        Assert.True(result.IsRanked, string.Join("; ", result.IncompleteReasons));
        Assert.Equal(100, result.ScoreBeforeGatesPercent!.Value, 6);
        Assert.Equal(100, result.ScorePercent!.Value, 6);
        Assert.Equal("S", result.Grade);
    }

    [Fact]
    public void MissingTelemetry_IsUnrankedAndHasNoScore()
    {
        var (key, challenge) = Load();
        var result = new ScoreEngine(key).Evaluate(challenge, new LandingSnapshot());
        Assert.False(result.IsRanked);
        Assert.Null(result.ScorePercent);
        Assert.NotEmpty(result.IncompleteReasons);
    }

    [Fact]
    public void Preview_EmptySnapshot_Is100Percent()
    {
        var (key, challenge) = Load();
        var preview = new ScoreEngine(key).EvaluatePreview(challenge, new LandingSnapshot());
        Assert.True(preview.IsPreview);
        Assert.False(preview.IsRanked); // never treat as final ranked highscore
        Assert.Equal(100.0, preview.ScorePercent);
        Assert.Equal("S", preview.Grade);
        Assert.False(preview.GearUpPenaltyApplied);
    }

    [Fact]
    public void Preview_BadApproachOnly_PullsDownFrom100()
    {
        var (key, challenge) = Load();
        // Only approach metrics measurable; TD + rollout still "assumed 100%".
        var snap = new LandingSnapshot
        {
            ApproachPathSampleCount = 10,
            ApproachMetricDurationSec = 30,
            ApproachLateralDistanceM = 2000,
            ApproachGlideslopeWeightedDeviationDeg = 1.75, // zero on asymmetric curve
            ApproachVerticalVariationFtPerSec = 25, // zero
            ApproachLateralWeaveIndex = 0.08, // zero
            ApproachBankMeanAbsDeg = 8 // zero on bank curve
        };
        var preview = new ScoreEngine(key).EvaluatePreview(challenge, snap);
        Assert.True(preview.IsPreview);
        // Approach phase ~0% × 30% weight → overall ≈ 70% (TD 60 + roll 10 assumed perfect).
        Assert.InRange(preview.ScorePercent!.Value, 69.0, 71.0);
        Assert.True(preview.ScorePercent < 100);
    }

    [Fact]
    public void Preview_DoesNotWriteAsRankedForHighscorePath()
    {
        var (key, challenge) = Load();
        var preview = new ScoreEngine(key).EvaluatePreview(challenge, CompleteSnapshot(challenge));
        Assert.True(preview.IsPreview);
        Assert.False(preview.IsRanked);
        // Same numbers as final would produce, but flagged preview so UI/store can refuse.
        var final = new ScoreEngine(key).Evaluate(challenge, CompleteSnapshot(challenge));
        Assert.Equal(final.ScorePercent, preview.ScorePercent);
    }

    [Fact]
    public void GearGate_AppliesMultiplierOnlyWhenRequired()
    {
        var (key, challenge) = Load();
        var engine = new ScoreEngine(key);
        var down = engine.Evaluate(challenge, CompleteSnapshot(challenge, true));
        var up = engine.Evaluate(challenge, CompleteSnapshot(challenge, false));
        Assert.True(down.IsRanked && up.IsRanked);
        var multiplier = key.GeneralPenalties!.Gear!.MultiplierOnFail;
        Assert.Equal(100,
            up.PhaseScores.Single(p => p.PhaseId == "touchdown").ScorePercent!.Value, 6);
        Assert.Equal(Math.Round(100 * multiplier, 1),
            up.ScorePercent!.Value, 6);
        challenge.RequireGearDown = false;
        var free = engine.Evaluate(challenge, CompleteSnapshot(challenge, false));
        Assert.Equal(down.ScorePercent, free.ScorePercent);
    }

    [Fact]
    public void FlapsGate_IsInformationalWhenSet_PenaltyWhenNot()
    {
        var (key, challenge) = Load();
        Assert.NotNull(key.GeneralPenalties?.Flaps);
        var engine = new ScoreEngine(key);

        var ok = engine.Evaluate(challenge, CompleteSnapshot(challenge, flapsIndex: 3));
        Assert.True(ok.IsRanked);
        Assert.False(ok.FlapsPenaltyApplied);
        var flapsOk = ok.Criteria.Single(c => c.Id == "flaps");
        Assert.Equal(MetricStatus.Informational, flapsOk.Status);
        Assert.Null(flapsOk.ScorePercent); // no phase points

        var bare = engine.Evaluate(challenge, CompleteSnapshot(challenge, flapsIndex: 0));
        Assert.True(bare.IsRanked);
        Assert.True(bare.FlapsPenaltyApplied);
        var multiplier = key.GeneralPenalties!.Flaps!.MultiplierOnFail;
        Assert.Equal(
            Math.Round(100 * multiplier, 1),
            bare.ScorePercent!.Value,
            6);
        Assert.Contains(ok.Criteria, c =>
            c.Id == "flaps" && string.IsNullOrWhiteSpace(c.PhaseId));
    }

    [Fact]
    public void ContactStabilityGate_TreatsRecontactsAsSecondAndThirdTouchdowns()
    {
        var (key, challenge) = Load();
        var engine = new ScoreEngine(key);
        var gate = Assert.IsType<ContactStabilityGateConfig>(key.GeneralPenalties!.ContactStability);

        ScoreResult ScoreWithBounces(int count)
        {
            var snapshot = CompleteSnapshot(challenge);
            var bounces = Enumerable.Range(1, count)
                .Select(i => new BounceEvent(i, i + 0.2, 0.2, -100, 1.2, 1.2, false))
                .ToArray();
            snapshot.ContactStability = new ContactStabilityAnalysis(
                true, bounces, count, count == 0 ? 0 : 0.2, null);
            return engine.Evaluate(challenge, snapshot);
        }

        var clean = ScoreWithBounces(0);
        var one = ScoreWithBounces(1);
        var two = ScoreWithBounces(2);
        var three = ScoreWithBounces(3);

        Assert.Equal(100, clean.ScorePercent);
        Assert.Equal(Math.Round(100 * gate.OneBounceMultiplier, 1), one.ScorePercent);
        Assert.Equal(Math.Round(100 * gate.TwoOrMoreBouncesMultiplier, 1), two.ScorePercent);
        Assert.Equal(two.ScorePercent, three.ScorePercent);
        Assert.Contains("second touchdown", one.Criteria.Single(c => c.Id == "contact_stability").Note);
        Assert.Contains("third touchdown", two.Criteria.Single(c => c.Id == "contact_stability").Note);
        Assert.Equal(MetricStatus.GateFailed, one.Criteria.Single(c => c.Id == "contact_stability").Status);
        Assert.Contains("bounce penalty", one.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StallWarningGate_AppliesGeneralMultiplierAndAwardsNoBaselineCredit()
    {
        var (key, challenge) = Load();
        var engine = new ScoreEngine(key);
        var gate = Assert.IsType<AircraftWarningGateConfig>(key.GeneralPenalties!.StallWarning);

        var clean = engine.Evaluate(challenge, CompleteSnapshot(challenge));
        var warnedSnapshot = CompleteSnapshot(challenge);
        warnedSnapshot.StallWarningOccurred = true;
        var warned = engine.Evaluate(challenge, warnedSnapshot);

        Assert.Equal(0.9, gate.MultiplierOnWarning, 6);
        Assert.Equal(Math.Round(100 * gate.MultiplierOnWarning, 1), warned.ScorePercent);
        Assert.Equal(MetricStatus.Informational,
            clean.Criteria.Single(c => c.Id == "stall_warning").Status);
        Assert.Equal(MetricStatus.GateFailed,
            warned.Criteria.Single(c => c.Id == "stall_warning").Status);
        Assert.Null(warned.Criteria.Single(c => c.Id == "stall_warning").PhaseId);
        Assert.Contains("stall-warning penalty", warned.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NativeAircraftWarnings_ApplySeparatelyAndStackOnCombinedScore()
    {
        var (key, challenge) = Load();
        var engine = new ScoreEngine(key);
        var penalties = key.GeneralPenalties!;

        var overspeedSnapshot = CompleteSnapshot(challenge);
        overspeedSnapshot.OverspeedWarningOccurred = true;
        var overspeed = engine.Evaluate(challenge, overspeedSnapshot);

        var bothSnapshot = CompleteSnapshot(challenge);
        bothSnapshot.StallWarningOccurred = true;
        bothSnapshot.OverspeedWarningOccurred = true;
        var both = engine.Evaluate(challenge, bothSnapshot);

        Assert.Equal(0.9, penalties.StallWarning!.MultiplierOnWarning, 6);
        Assert.Equal(0.9, penalties.OverspeedWarning!.MultiplierOnWarning, 6);
        Assert.Equal(90, overspeed.ScorePercent);
        Assert.Equal(81, both.ScorePercent);
        Assert.Equal(MetricStatus.GateFailed,
            overspeed.Criteria.Single(c => c.Id == "overspeed_warning").Status);
        Assert.Equal(2, both.Criteria.Count(c =>
            c.Id is "stall_warning" or "overspeed_warning"
            && c.Status == MetricStatus.GateFailed));
        Assert.Contains("overspeed-warning penalty", both.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.All(both.Criteria.Where(c => c.Id is "stall_warning" or "overspeed_warning"),
            criterion => Assert.True(string.IsNullOrWhiteSpace(criterion.PhaseId)));
    }

    [Fact]
    public void TouchdownPhase_ExcludesPenaltyGates()
    {
        var (key, _) = Load();
        var td = key.Phases.Single(p => p.Id == "touchdown");
        Assert.DoesNotContain(td.Metrics, m => m.Id == "flaps");
        Assert.DoesNotContain(td.Metrics, m => m.Id == "contact_stability");
        Assert.DoesNotContain(td.Metrics, m => m.Id == "excess_speed");
        Assert.DoesNotContain(td.Metrics, m => m.Id == "alignment");
        Assert.Equal(40, td.Metrics.Single(m => m.Id == "touchdown_impact").ImportancePercent);
        Assert.Equal(22, td.Metrics.Single(m => m.Id == "touchdown_point").ImportancePercent);
        Assert.Equal(10, td.Metrics.Single(m => m.Id == "airspeed").ImportancePercent);
        Assert.Equal(8, td.Metrics.Single(m => m.Id == "centerline").ImportancePercent);
        Assert.Equal(6, td.Metrics.Single(m => m.Id == "bank").ImportancePercent);
        Assert.Equal(6, td.Metrics.Single(m => m.Id == "runway_alignment").ImportancePercent);
        Assert.Equal(100, td.Metrics.Sum(m => m.ImportancePercent), 2);
    }

    [Fact]
    public void EvaluationKey_LoadsAndValidates()
    {
        var loaded = new ConfigLoader(FindConfig()).LoadEvaluationKey();
        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        Assert.Equal("landing-evaluation-key", loaded.Key!.Id);
        Assert.Equal(34, loaded.Key.Version);
        Assert.Equal(0, loaded.Key.Timing!.PostTouchdownAlignmentDelaySeconds);
        Assert.Equal(143, loaded.Key.SpeedTarget!.DefaultVappKts);
    }

    [Fact]
    public void FreeFlightEvaluationKey_InheritsAuthoritativeFlapGate()
    {
        var loader = new ConfigLoader(FindConfig());
        var catalog = loader.LoadCatalog();
        var loaded = loader.LoadEvaluationKey(catalog.FreeFlightEvaluationKey);

        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        Assert.Equal("free-flight-evaluation-key", loaded.Key!.Id);
        Assert.Equal(17, loaded.Key.Version);
        Assert.Equal(70, loaded.Key.SpeedTarget!.DefaultVappKts);
        Assert.NotNull(loaded.Key.FreeMode);
        Assert.NotNull(loaded.Key.GeneralPenalties!.Flaps);
    }

    [Fact]
    public void Centerline_UsesSingleCurveParameters()
    {
        var metric = new EvaluationMetric { Evaluator = "centerline", Params = new() { ["tolerance"] = 3, ["zeroAt"] = 22, ["exponent"] = 1.2 } };
        Assert.Equal(1, CenterlineEvaluator.Instance.Evaluate(0, metric), 6);
        Assert.Equal(1, CenterlineEvaluator.Instance.Evaluate(3, metric), 6);
        Assert.Equal(0, CenterlineEvaluator.Instance.Evaluate(22, metric), 6);
        // ~8.5 m (prior harsh zeroAt=10 → ~23%) should now score generously on-runway.
        var mid = CenterlineEvaluator.Instance.Evaluate(8.5, metric);
        Assert.InRange(mid, 0.70, 0.90);
    }

    [Fact]
    public void ApproachPhase_HasFourMetricsWithBankAtTwentyPercent()
    {
        var (key, _) = Load();
        var approach = key.Phases.Single(p => p.Id == "approach");
        Assert.Equal(4, approach.Metrics.Count);
        Assert.Contains(approach.Metrics, m => m.Id == "approach_glideslope");
        Assert.Contains(approach.Metrics, m => m.Id == "approach_vertical_steady");
        Assert.Contains(approach.Metrics, m => m.Id == "approach_lateral_steady");
        Assert.Contains(approach.Metrics, m => m.Id == "approach_bank_stability");
        Assert.Equal(20, approach.Metrics.Single(m => m.Id == "approach_bank_stability").ImportancePercent, 2);
        Assert.Equal(100, approach.Metrics.Sum(m => m.ImportancePercent), 2);
    }

    [Fact]
    public void ApproachGlideslope_AsymmetricWeightedCurve_GivesPartialCredit()
    {
        var (key, _) = Load();
        var metric = key.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "approach_glideslope");
        Assert.Equal("approachGlideslopeWeightedDeviationDeg", metric.Metric);
        Assert.Equal(1.0, TargetEvaluator.Instance.Evaluate(0.20, metric), 6);
        Assert.Equal(1.0, TargetEvaluator.Instance.Evaluate(0.25, metric), 6);
        Assert.Equal(0.0, TargetEvaluator.Instance.Evaluate(1.75, metric), 6);
        var mid = TargetEvaluator.Instance.Evaluate(1.0, metric); // halfway tolerance→zero ≈ 50%
        Assert.InRange(mid, 0.45, 0.55);
    }

    [Fact]
    public void ApproachLateralAndBankCurves_NoLongerGiveRoutineValuesFullCredit()
    {
        var (key, _) = Load();
        var metrics = key.Phases.SelectMany(p => p.Metrics).ToDictionary(m => m.Id);

        var lateral = TargetEvaluator.Instance.Evaluate(0.0095, metrics["approach_lateral_steady"]);
        var bank = TargetEvaluator.Instance.Evaluate(1.54, metrics["approach_bank_stability"]);

        Assert.InRange(lateral, 0.92, 0.94);
        Assert.InRange(bank, 0.85, 0.87);
    }

    [Fact]
    public void Hierarchy_ApproachUsesFourMetricsNotSinglePath()
    {
        var (key, challenge) = Load();
        var snap = CompleteSnapshot(challenge);
        var result = new ScoreEngine(key).Evaluate(challenge, snap);
        Assert.True(result.IsRanked, string.Join("; ", result.IncompleteReasons));
        var approachIds = result.Criteria
            .Where(c => c.PhaseId == "approach" && c.Status == MetricStatus.Scored)
            .Select(c => c.Id)
            .OrderBy(x => x)
            .ToList();
        Assert.Equal(
            new[]
            {
                "approach_bank_stability",
                "approach_glideslope",
                "approach_lateral_steady",
                "approach_vertical_steady"
            },
            approachIds);
        Assert.Contains(result.Criteria, c => c.Id == "stall_warning" && string.IsNullOrWhiteSpace(c.PhaseId));
        Assert.Contains(result.Criteria, c => c.Id == "automation" && string.IsNullOrWhiteSpace(c.PhaseId));
    }

    [Fact]
    public void RolloutPath_SoftCurve_GivesCreditNearRunwayEdge()
    {
        var (key, _) = Load();
        var metric = key.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "rollout_path");
        var nearEdge = TargetEvaluator.Instance.Evaluate(18.9, metric);
        Assert.InRange(nearEdge, 0.60, 0.80);
    }
}
