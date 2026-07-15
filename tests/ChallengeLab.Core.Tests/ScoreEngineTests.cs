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

    private static LandingSnapshot CompleteSnapshot(bool gearDown = true, int flapsIndex = 3) => new()
    {
        Touchdown = new TelemetrySample { Timestamp = DateTimeOffset.UtcNow, SimOnGround = true },
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
        ApproachPathRms = 1,
        ApproachPathSampleCount = 3,
        ApproachGlideslopeMeanAbsFt = 20,
        ApproachVerticalVariationFtPerSec = 1.5,
        ApproachLateralWeaveIndex = 0.01,
        ApproachLateralDistanceM = 2000,
        ApproachMetricDurationSec = 45,
        GroundTrackSampleCount = 4,
        GroundTrackBeforeSegmentCount = 1,
        GroundTrackAfterSegmentCount = 1,
        GroundTrackErrorMeanDeg = 1,
        GroundTrackErrorRmsDeg = 1,
        GroundTrackErrorPeakDeg = 2,
        PostTouchdownAlignmentSampleCount = 2,
        PostTouchdownAlignmentMeanDeg = 1,
        PostTouchdownAlignmentRmsDeg = 1,
        PostTouchdownAlignmentPeakDeg = 2,
        RolloutPathSampleCount = 3,
        RolloutPathSegmentCount = 2,
        RolloutDistanceM = 10,
        RolloutLateralMeanM = 1,
        RolloutLateralPeakM = 2,
        RolloutWeaveIndex = .01
    };

    [Fact]
    public void Piecewise_UsesPercentScale_IncludingOnePercent()
    {
        var metric = new EvaluationMetric { Evaluator = "piecewise", Points = new() { new() { V = 0, S = 1 }, new() { V = 1, S = 100 } } };
        Assert.Equal(.01, PiecewiseEvaluator.Instance.Evaluate(0, metric), 6);
        Assert.Equal(1, PiecewiseEvaluator.Instance.Evaluate(1, metric), 6);
    }

    [Fact]
    public void TouchdownVs_ExtremeHardLanding_ScoresZeroNotSeventy()
    {
        var (key, _) = Load();
        var metric = key.Phases
            .SelectMany(p => p.Metrics)
            .Single(m => m.Id == "touchdown_vs");

        // Catastrophic sink rates must not clamp to the old −600→70% end score.
        Assert.Equal(0, PiecewiseEvaluator.Instance.Evaluate(-9805, metric), 6);
        Assert.Equal(0, PiecewiseEvaluator.Instance.Evaluate(-2000, metric), 6);
        Assert.True(PiecewiseEvaluator.Instance.Evaluate(-1200, metric) < 0.2);
        Assert.Equal(0.70, PiecewiseEvaluator.Instance.Evaluate(-600, metric), 6);
        Assert.Equal(1.0, PiecewiseEvaluator.Instance.Evaluate(-100, metric), 6);
    }

    [Fact]
    public void Hierarchy_WeightsAreAppliedExactlyOnce()
    {
        var (key, challenge) = Load();
        var result = new ScoreEngine(key).Evaluate(challenge, CompleteSnapshot());
        Assert.True(result.IsRanked, string.Join("; ", result.IncompleteReasons));
        Assert.NotNull(result.ScorePercent);
        Assert.Equal(86.0, 95 * .70 + 60 * .25 + 90 * .05, 6);
        Assert.Equal(70, result.PhaseScores.Single(p => p.PhaseId == "touchdown").WeightPercent);
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
            ApproachGlideslopeMeanAbsFt = 350, // zero on soft curve
            ApproachVerticalVariationFtPerSec = 25, // zero
            ApproachLateralWeaveIndex = 0.20 // zero
        };
        var preview = new ScoreEngine(key).EvaluatePreview(challenge, snap);
        Assert.True(preview.IsPreview);
        // Approach phase ~0% × 25% weight → overall ≈ 75% (TD 70 + roll 5 assumed perfect).
        Assert.InRange(preview.ScorePercent!.Value, 74.0, 76.0);
        Assert.True(preview.ScorePercent < 100);
    }

    [Fact]
    public void Preview_DoesNotWriteAsRankedForHighscorePath()
    {
        var (key, challenge) = Load();
        var preview = new ScoreEngine(key).EvaluatePreview(challenge, CompleteSnapshot());
        Assert.True(preview.IsPreview);
        Assert.False(preview.IsRanked);
        // Same numbers as final would produce, but flagged preview so UI/store can refuse.
        var final = new ScoreEngine(key).Evaluate(challenge, CompleteSnapshot());
        Assert.Equal(final.ScorePercent, preview.ScorePercent);
    }

    [Fact]
    public void GearGate_AppliesMultiplierOnlyWhenRequired()
    {
        var (key, challenge) = Load();
        var engine = new ScoreEngine(key);
        var down = engine.Evaluate(challenge, CompleteSnapshot(true));
        var up = engine.Evaluate(challenge, CompleteSnapshot(false));
        Assert.True(down.IsRanked && up.IsRanked);
        Assert.Equal(Math.Round(down.ScorePercent!.Value * key.Gates!.Gear!.MultiplierOnFail, 1), up.ScorePercent!.Value, 6);
        challenge.RequireGearDown = false;
        var free = engine.Evaluate(challenge, CompleteSnapshot(false));
        Assert.Equal(down.ScorePercent, free.ScorePercent);
    }

    [Fact]
    public void FlapsGate_IsInformationalWhenSet_PenaltyWhenNot()
    {
        var (key, challenge) = Load();
        Assert.NotNull(key.Gates?.Flaps);
        var engine = new ScoreEngine(key);

        var ok = engine.Evaluate(challenge, CompleteSnapshot(flapsIndex: 3));
        Assert.True(ok.IsRanked);
        Assert.False(ok.FlapsPenaltyApplied);
        var flapsOk = ok.Criteria.Single(c => c.Id == "flaps");
        Assert.Equal(MetricStatus.Informational, flapsOk.Status);
        Assert.Null(flapsOk.ScorePercent); // no phase points

        var bare = engine.Evaluate(challenge, CompleteSnapshot(flapsIndex: 0));
        Assert.True(bare.IsRanked);
        Assert.True(bare.FlapsPenaltyApplied);
        Assert.Equal(
            Math.Round(ok.ScorePercent!.Value * key.Gates!.Flaps!.MultiplierOnFail, 1),
            bare.ScorePercent!.Value,
            6);
        Assert.DoesNotContain(ok.Criteria, c =>
            c.Id == "flaps" && c.PhaseId == "touchdown");
    }

    [Fact]
    public void TouchdownPhase_NoLongerIncludesFlapsMetric()
    {
        var (key, _) = Load();
        var td = key.Phases.Single(p => p.Id == "touchdown");
        Assert.DoesNotContain(td.Metrics, m => m.Id == "flaps");
        Assert.Equal(100, td.Metrics.Sum(m => m.ImportancePercent), 2);
    }

    [Fact]
    public void EvaluationKey_LoadsAndValidates()
    {
        var loaded = new ConfigLoader(FindConfig()).LoadEvaluationKey();
        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        Assert.Equal("landing-evaluation-key", loaded.Key!.Id);
        Assert.Equal(7, loaded.Key.Version);
        Assert.Equal(143, loaded.Key.SpeedTarget!.DefaultVappKts);
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
    public void ApproachPhase_HasThreeEqualWeightMetrics()
    {
        var (key, _) = Load();
        var approach = key.Phases.Single(p => p.Id == "approach");
        Assert.Equal(3, approach.Metrics.Count);
        Assert.Contains(approach.Metrics, m => m.Id == "approach_glideslope");
        Assert.Contains(approach.Metrics, m => m.Id == "approach_vertical_steady");
        Assert.Contains(approach.Metrics, m => m.Id == "approach_lateral_steady");
        Assert.Equal(100, approach.Metrics.Sum(m => m.ImportancePercent), 2);
    }

    [Fact]
    public void ApproachGlideslope_SoftCurve_GivesPartialCreditForMeanAbsoluteError()
    {
        var (key, _) = Load();
        var metric = key.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "approach_glideslope");
        Assert.Equal(1.0, TargetEvaluator.Instance.Evaluate(20, metric), 6);
        Assert.Equal(1.0, TargetEvaluator.Instance.Evaluate(40, metric), 6);
        Assert.Equal(0.0, TargetEvaluator.Instance.Evaluate(350, metric), 6);
        var mid = TargetEvaluator.Instance.Evaluate(195, metric); // halfway tol→max ≈ 50%
        Assert.InRange(mid, 0.45, 0.55);
    }

    [Fact]
    public void Hierarchy_ApproachUsesThreeMetricsNotSinglePath()
    {
        var (key, challenge) = Load();
        var snap = CompleteSnapshot();
        var result = new ScoreEngine(key).Evaluate(challenge, snap);
        Assert.True(result.IsRanked, string.Join("; ", result.IncompleteReasons));
        var approachIds = result.Criteria
            .Where(c => c.PhaseId == "approach")
            .Select(c => c.Id)
            .OrderBy(x => x)
            .ToList();
        Assert.Equal(
            new[] { "approach_glideslope", "approach_lateral_steady", "approach_vertical_steady" },
            approachIds);
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
