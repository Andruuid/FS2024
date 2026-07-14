using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Tests;

public class ScoreEngineTests
{
    private static ScoreEngine CreateEngine()
    {
        var root = FindRepoConfig();
        var loader = new ConfigLoader(root);
        return new ScoreEngine(loader.LoadEvaluationKey());
    }

    private static ScoringProfileConfig LoadProfile()
    {
        var root = FindRepoConfig();
        var loader = new ConfigLoader(root);
        return loader.LoadScoringProfile("scoring/profiles/hardcore-crosswind-landing.json");
    }

    private static ChallengeConfig LoadChallenge()
    {
        var root = FindRepoConfig();
        var loader = new ConfigLoader(root);
        return loader.LoadChallenge("challenges/barcelona-crosswind-final.json");
    }

    private static string FindRepoConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var config = Path.Combine(dir.FullName, "config");
            if (Directory.Exists(config) && File.Exists(Path.Combine(config, "catalog.json")))
                return config;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate config/ from test output.");
    }

    [Fact]
    public void Piecewise_AcceptsPercentScale_0to100()
    {
        // Preferred JSON: s is metric score percent (0â€“100), same as the report UI.
        var criterion = new CriterionConfig
        {
            Evaluator = "piecewise",
            Points = new List<ScorePoint>
            {
                new() { V = -600, S = 70 },
                new() { V = -500, S = 75 },
                new() { V = -400, S = 80 },
                new() { V = -320, S = 82 },
                new() { V = -250, S = 90 },
                new() { V = -180, S = 95 },
                new() { V = -100, S = 100 },
                new() { V = -60, S = 95 },
                new() { V = -20, S = 90 },
                new() { V = 0, S = 85 },
                new() { V = 50, S = 50 }
            }
        };

        var ideal = PiecewiseEvaluator.Instance.Evaluate(-100, criterion);
        var firm = PiecewiseEvaluator.Instance.Evaluate(-180, criterion);
        var hard = PiecewiseEvaluator.Instance.Evaluate(-400, criterion);
        var climb = PiecewiseEvaluator.Instance.Evaluate(50, criterion); // rare / bounce
        var butter = PiecewiseEvaluator.Instance.Evaluate(-20, criterion);

        Assert.Equal(1.0, ideal, 3);          // s:100 â†’ 1.0 internal
        Assert.Equal(0.95, firm, 3);         // s:95
        Assert.Equal(0.80, hard, 3);         // s:80
        Assert.Equal(0.50, climb, 3);        // s:50 â€” yes, possible on curve
        Assert.Equal(0.90, butter, 3);
        Assert.True(ideal > hard);
    }

    [Fact]
    public void Piecewise_LegacyFractionScale_StillWorks()
    {
        var criterion = new CriterionConfig
        {
            Evaluator = "piecewise",
            Points = new List<ScorePoint>
            {
                new() { V = -600, S = 0.00 },
                new() { V = -100, S = 1.00 },
                new() { V = 50, S = 0.00 }
            }
        };

        Assert.Equal(1.0, PiecewiseEvaluator.Instance.Evaluate(-100, criterion), 3);
        Assert.Equal(0.0, PiecewiseEvaluator.Instance.Evaluate(-600, criterion), 3);
    }

    [Fact]
    public void AllMetrics_AreScored_IncludingApproachAndMaxCenterline()
    {
        var engine = CreateEngine();
        var challenge = LoadChallenge();
        var profile = LoadProfile();
        var result = engine.Evaluate(challenge, profile, FirmCrosswindSnapshot());

        Assert.Contains(result.Criteria, c => c.Id == "approach_path" && c.Applied);
        Assert.Contains(result.Criteria, c => c.Id == "max_centerline" && c.Applied);
        Assert.Contains(result.Criteria, c => c.Id == "touchdown_vs" && c.Applied);
    }

    [Fact]
    public void FirmLanding_ScoresHigherThanButter_OnCrosswindProfile()
    {
        var engine = CreateEngine();
        var challenge = LoadChallenge();
        var profile = LoadProfile();

        var firm = engine.Evaluate(challenge, profile, FirmCrosswindSnapshot());
        var butter = engine.Evaluate(challenge, profile, ButterSnapshot());

        Assert.True(firm.ScorePercent > butter.ScorePercent,
            $"Expected firm {firm.ScorePercent} > butter {butter.ScorePercent}");
        Assert.True(firm.ScorePercent >= 70);
    }

    [Fact]
    public void Hierarchical_Example_Touchdown95_Approach60_Rollout90_Is86()
    {
        // Structural check: phase weights 70/25/5 → (95*0.7)+(60*0.25)+(90*0.05)=86
        var expected = 95 * 0.70 + 60 * 0.25 + 90 * 0.05;
        Assert.Equal(86.0, expected, 3);

        var engine = CreateEngine();
        var challenge = LoadChallenge();
        var profile = LoadProfile();
        var result = engine.Evaluate(challenge, profile, FirmCrosswindSnapshot());
        Assert.NotEmpty(result.PhaseScores);
        Assert.Contains(result.PhaseScores, p => p.PhaseId == "touchdown" && p.WeightPercent == 70);
        Assert.Contains(result.PhaseScores, p => p.PhaseId == "approach" && p.WeightPercent == 25);
        Assert.Contains(result.PhaseScores, p => p.PhaseId == "rollout" && p.WeightPercent == 5);
        Assert.DoesNotContain(result.Criteria, c => c.Id == "peak_g");
    }

    [Fact]
    public void LandingResult_Summary_IsHierarchicalBreakdown()
    {
        var engine = CreateEngine();
        var challenge = LoadChallenge();
        var profile = LoadProfile();
        var result = engine.Evaluate(challenge, profile, FirmCrosswindSnapshot());

        Assert.NotNull(result.Summary);
        Assert.StartsWith("Total Grade ", result.Summary);
        Assert.Contains("Touchdown ", result.Summary);
        Assert.Contains("-vSpeed ", result.Summary);
        Assert.Contains("-airspeed ", result.Summary);
        Assert.Contains("Approach ", result.Summary);
        Assert.Contains("-steadiness ", result.Summary);
        Assert.Contains("Rollout ", result.Summary);

        // Criteria carry phase ids for rebuild
        Assert.Contains(result.Criteria, c => c.Id == "touchdown_vs" && c.PhaseId == "touchdown");
        Assert.Contains(result.Criteria, c => c.Id == "approach_path" && c.PhaseId == "approach");
    }

    [Fact]
    public void EvaluationKey_LoadsFromRepoJson_AtStartup()
    {
        var root = FindRepoConfig();
        var loader = new ConfigLoader(root);
        var (key, path, error) = loader.LoadEvaluationKeyWithPath();

        Assert.Null(error);
        Assert.NotNull(key);
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal("landing-evaluation-key", key!.Id);
        Assert.NotEmpty(key.Phases);
        Assert.Equal(100, key.Phases.Sum(p => p.WeightPercent), 1);
        Assert.NotNull(key.Settle);
        Assert.Equal(50, key.Settle!.GroundSpeedKts);
        Assert.NotNull(key.Timing);
        Assert.Equal(3, key.Timing!.GroundTrackWindowBeforeSeconds);
        Assert.NotNull(key.Gates?.Gear);
        Assert.Equal(0.1, key.Gates!.Gear!.MultiplierOnFail);
    }

    [Fact]
    public void EvaluationKey_ApplyToProfile_OverlaysSettleTimingAndGear()
    {
        var root = FindRepoConfig();
        var loader = new ConfigLoader(root);
        var key = loader.LoadEvaluationKey();
        Assert.NotNull(key);

        var profile = new ScoringProfileConfig
        {
            SettledGroundSpeedKts = 99,
            SettledHoldSeconds = 9,
            GroundTrackWindowBeforeSeconds = 9,
            GroundTrackWindowAfterSeconds = 9,
            PostTouchdownAlignmentDelaySeconds = 9,
            FlareAglFeet = 9,
            GearUpScoreMultiplier = 0.99
        };

        key!.ApplyToProfile(profile);

        Assert.Equal(key.Settle!.GroundSpeedKts, profile.SettledGroundSpeedKts);
        Assert.Equal(key.Settle.HoldSeconds, profile.SettledHoldSeconds);
        Assert.Equal(key.Timing!.GroundTrackWindowBeforeSeconds, profile.GroundTrackWindowBeforeSeconds);
        Assert.Equal(key.Timing.GroundTrackWindowAfterSeconds, profile.GroundTrackWindowAfterSeconds);
        Assert.Equal(key.Timing.PostTouchdownAlignmentDelaySeconds, profile.PostTouchdownAlignmentDelaySeconds);
        Assert.Equal(key.Timing.FlareAglFeet, profile.FlareAglFeet);
        Assert.Equal(key.Gates!.Gear!.MultiplierOnFail, profile.GearUpScoreMultiplier);
    }

    [Fact]
    public void GradeThresholds()
    {
        Assert.Equal("S", ScoreResult.GradeFromPercent(96));
        Assert.Equal("A", ScoreResult.GradeFromPercent(85));
        Assert.Equal("B", ScoreResult.GradeFromPercent(70));
        Assert.Equal("F", ScoreResult.GradeFromPercent(10));
    }

    [Fact]
    public void LandingSession_SettlesBelow50Knots()
    {
        var challenge = LoadChallenge();
        var profile = LoadProfile();
        profile.SettledHoldSeconds = 0.05;
        profile.SettledGroundSpeedKts = 50;
        var session = new LandingSession(challenge, profile);
        session.Arm();

        var settled = false;
        session.SettledReady += (_, _) => settled = true;

        var t0 = DateTimeOffset.UtcNow;
        // Approach sample in air
        session.Ingest(Sample(onGround: false, gsKts: 140, vs: -700, lat: 41.32, lon: 2.12, timestamp: t0));
        // Touchdown
        session.Ingest(Sample(onGround: true, gsKts: 130, vs: -120, lat: 41.294, lon: 2.084, g: 1.25, timestamp: t0.AddSeconds(1)));
        // Still above 50 kt â€” should not settle
        session.Ingest(Sample(onGround: true, gsKts: 55, vs: 0, lat: 41.2935, lon: 2.0835, g: 1.0, timestamp: t0.AddSeconds(1.5)));
        // Under 50 kt â€” first low-GS sample starts hold timer
        session.Ingest(Sample(onGround: true, gsKts: 45, vs: 0, lat: 41.293, lon: 2.083, g: 1.0, timestamp: t0.AddSeconds(2)));
        // Still under 50 kt after hold â†’ settled
        session.Ingest(Sample(onGround: true, gsKts: 40, vs: 0, lat: 41.292, lon: 2.082, g: 1.0, timestamp: t0.AddSeconds(2.2)));

        Assert.True(settled);
        Assert.Equal(LandingPhase.Scored, session.Phase);
        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.InRange(session.Snapshot.VerticalSpeedAtTouchdownFpm, -150, -100);
    }

    private static LandingSnapshot FirmCrosswindSnapshot() => new()
    {
        VerticalSpeedAtTouchdownFpm = -150,
        PeakGForce = 1.25,
        TouchdownLateralOffsetM = 2,
        MaxLateralOffsetM = 5,
        TouchdownHeadingErrorDeg = 1.5,
        AirspeedAtTouchdownKts = 140, // ~ target 138 (VAPP 143 âˆ’ 5)
        VappKts = 143,
        TargetTouchdownIasKts = 138,
        TouchdownIasErrorKts = 2,
        ExcessSpeedOverVappKts = 0,
        SpeedTargetSource = "test",
        BankAtTouchdownDeg = 0.8,
        PitchAtTouchdownDeg = 4,
        GearDownAtTouchdown = true,
        FlapsIndexAtTouchdown = 4,
        ApproachPathRms = 60,
        RolloutHeadingVariance = 1.5,
        CrabAngleAtFlareDeg = 2,
        GroundTrackErrorMeanDeg = 1.5,
        GroundTrackErrorRmsDeg = 2.0,
        GroundTrackErrorPeakDeg = 3.0,
        GroundTrackSampleCount = 20,
        PostTouchdownAlignmentMeanDeg = 1.0,
        PostTouchdownAlignmentRmsDeg = 1.2,
        PostTouchdownAlignmentPeakDeg = 2.0,
        PostTouchdownAlignmentSampleCount = 15,
        RolloutLateralMeanM = 2.0,
        RolloutLateralPeakM = 4.0,
        RolloutWeaveIndex = 0.01,
        RolloutDistanceM = 800,
        RolloutPathSampleCount = 40
    };

    private static LandingSnapshot ButterSnapshot() => new()
    {
        VerticalSpeedAtTouchdownFpm = -18,
        PeakGForce = 1.02,
        TouchdownLateralOffsetM = 2,
        MaxLateralOffsetM = 5,
        TouchdownHeadingErrorDeg = 1.5,
        AirspeedAtTouchdownKts = 140,
        VappKts = 143,
        TargetTouchdownIasKts = 138,
        TouchdownIasErrorKts = 2,
        ExcessSpeedOverVappKts = 0,
        SpeedTargetSource = "test",
        BankAtTouchdownDeg = 0.8,
        PitchAtTouchdownDeg = 4,
        GearDownAtTouchdown = true,
        FlapsIndexAtTouchdown = 4,
        ApproachPathRms = 60,
        RolloutHeadingVariance = 1.5,
        CrabAngleAtFlareDeg = 2,
        GroundTrackErrorMeanDeg = 1.5,
        GroundTrackErrorRmsDeg = 2.0,
        GroundTrackErrorPeakDeg = 3.0,
        GroundTrackSampleCount = 20,
        PostTouchdownAlignmentMeanDeg = 1.0,
        PostTouchdownAlignmentRmsDeg = 1.2,
        PostTouchdownAlignmentPeakDeg = 2.0,
        PostTouchdownAlignmentSampleCount = 15,
        RolloutLateralMeanM = 2.0,
        RolloutLateralPeakM = 4.0,
        RolloutWeaveIndex = 0.01,
        RolloutDistanceM = 800,
        RolloutPathSampleCount = 40
    };

    [Fact]
    public void GroundTrackWindow_ScoresPathNotCrab()
    {
        var challenge = LoadChallenge();
        challenge.Runway.HeadingTrueDeg = 246;
        var profile = LoadProfile();
        profile.GroundTrackWindowBeforeSeconds = 3;
        profile.GroundTrackWindowAfterSeconds = 3;
        profile.SettledHoldSeconds = 0.05;
        profile.SettledGroundSpeedKts = 50;

        var session = new LandingSession(challenge, profile);
        session.Arm();
        var t0 = DateTimeOffset.UtcNow;
        var runway = 246.0;

        // 3 s of approach: CG track aligned with runway (slight noise)
        for (var i = 0; i < 10; i++)
        {
            var t = t0.AddSeconds(i * 0.3);
            session.Ingest(Sample(
                onGround: false, gsKts: 140, vs: -500,
                lat: 41.30 + i * 0.00005, lon: 2.09 - i * 0.00008,
                g: 1.0, timestamp: t, track: runway + 1.0));
        }

        var td = t0.AddSeconds(3.0);
        session.Ingest(Sample(onGround: true, gsKts: 130, vs: -140,
            lat: 41.295, lon: 2.085, g: 1.2, timestamp: td, track: runway));

        // 3 s rollout on track
        for (var i = 1; i <= 10; i++)
        {
            session.Ingest(Sample(onGround: true, gsKts: Math.Max(20, 100 - i * 8), vs: 0,
                lat: 41.295 - i * 0.00004, lon: 2.085 - i * 0.00006,
                g: 1.0, timestamp: td.AddSeconds(i * 0.3), track: runway + 0.5));
        }

        // Settle
        session.Ingest(Sample(onGround: true, gsKts: 40, vs: 0,
            lat: 41.293, lon: 2.082, g: 1.0, timestamp: td.AddSeconds(3.5), track: runway));
        session.Ingest(Sample(onGround: true, gsKts: 35, vs: 0,
            lat: 41.292, lon: 2.081, g: 1.0, timestamp: td.AddSeconds(3.7), track: runway));

        Assert.True(session.Snapshot.GroundTrackSampleCount > 0);
        Assert.True(session.Snapshot.GroundTrackErrorMeanDeg < 5,
            $"Expected low track error, got {session.Snapshot.GroundTrackErrorMeanDeg}");

        var engine = CreateEngine();
        var result = engine.Evaluate(challenge, profile, session.Snapshot);
        Assert.Contains(result.Criteria, c => c.Id == "ground_track" && c.Applied);
        Assert.DoesNotContain(result.Criteria, c => c.Id == "crab" && c.Applied);
    }

    [Fact]
    public void GearDown_DoesNotInflateScore_GearUpAppliesHeavyPenalty()
    {
        var engine = CreateEngine();
        var challenge = LoadChallenge();
        challenge.RequireGearDown = true;
        var profile = LoadProfile();
        profile.GearUpScoreMultiplier = 0.1;

        var gearDown = FirmCrosswindSnapshot();
        gearDown.GearDownAtTouchdown = true;
        var gearUp = FirmCrosswindSnapshot();
        gearUp.GearDownAtTouchdown = false;

        var ok = engine.Evaluate(challenge, profile, gearDown);
        var bad = engine.Evaluate(challenge, profile, gearUp);

        // Gear OK must never be "strongest" free points
        Assert.DoesNotContain(ok.Criteria, c => c.Id == "gear" && c.Applied && c.Weight > 0);
        Assert.False(ok.GearUpPenaltyApplied);
        Assert.DoesNotContain(ok.Summary ?? "", "Gear down (100%)");

        // Gear up: score roughly 10% of pre-gate score
        Assert.True(bad.GearUpPenaltyApplied);
        Assert.InRange(bad.ScorePercent, ok.ScorePercent * 0.05, ok.ScorePercent * 0.15 + 0.5);
        Assert.Contains(bad.Criteria, c => c.Id == "gear" && c.DisplayName.Contains("UP", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("GEAR UP", bad.Summary ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GearUp_NoPenalty_WhenChallengeDoesNotRequireGear()
    {
        var engine = CreateEngine();
        var challenge = LoadChallenge();
        challenge.RequireGearDown = false; // water / belly landing
        var profile = LoadProfile();

        var gearUp = FirmCrosswindSnapshot();
        gearUp.GearDownAtTouchdown = false;
        var withGear = FirmCrosswindSnapshot();
        withGear.GearDownAtTouchdown = true;

        var a = engine.Evaluate(challenge, profile, gearUp);
        var b = engine.Evaluate(challenge, profile, withGear);

        Assert.False(a.GearUpPenaltyApplied);
        Assert.Equal(b.ScorePercent, a.ScorePercent);
    }

    [Fact]
    public void RolloutWeave_SteadyPathScoresBetterThanSTurns()
    {
        var engine = CreateEngine();
        var challenge = LoadChallenge();
        var profile = LoadProfile();

        var steady = FirmCrosswindSnapshot();
        steady.RolloutLateralMeanM = 3.0;
        steady.RolloutWeaveIndex = 0.01; // almost no left/right
        steady.PostTouchdownAlignmentMeanDeg = 1.0;

        var weave = FirmCrosswindSnapshot();
        weave.RolloutLateralMeanM = 3.0; // same average offset
        weave.RolloutWeaveIndex = 0.18;  // S-turns
        weave.PostTouchdownAlignmentMeanDeg = 8.0; // swinging heading

        var steadyScore = engine.Evaluate(challenge, profile, steady);
        var weaveScore = engine.Evaluate(challenge, profile, weave);

        var steadyWeave = steadyScore.Criteria.First(c => c.Id == "rollout_weave");
        var weaveWeave = weaveScore.Criteria.First(c => c.Id == "rollout_weave");
        var steadyAlign = steadyScore.Criteria.First(c => c.Id == "post_td_alignment");
        var weaveAlign = weaveScore.Criteria.First(c => c.Id == "post_td_alignment");

        Assert.True(steadyWeave.ScorePercent > weaveWeave.ScorePercent,
            $"Steady weave {steadyWeave.ScorePercent} should beat S-turns {weaveWeave.ScorePercent}");
        Assert.True(steadyAlign.ScorePercent > weaveAlign.ScorePercent);
        Assert.True(steadyScore.ScorePercent > weaveScore.ScorePercent);
    }

    [Fact]
    public void TouchdownIas_ScoredRelativeToVappTarget()
    {
        var engine = CreateEngine();
        var challenge = LoadChallenge();
        var profile = LoadProfile();

        var onTarget = FirmCrosswindSnapshot();
        onTarget.AirspeedAtTouchdownKts = 138;
        onTarget.TargetTouchdownIasKts = 138;
        onTarget.TouchdownIasErrorKts = 0;
        onTarget.ExcessSpeedOverVappKts = 0;

        var hot = FirmCrosswindSnapshot();
        hot.AirspeedAtTouchdownKts = 158;
        hot.VappKts = 143;
        hot.TargetTouchdownIasKts = 138;
        hot.TouchdownIasErrorKts = 20; // +20 vs target
        hot.ExcessSpeedOverVappKts = 15; // +15 over VAPP

        var onScore = engine.Evaluate(challenge, profile, onTarget);
        var hotScore = engine.Evaluate(challenge, profile, hot);

        var iasOn = onScore.Criteria.First(c => c.Id == "airspeed");
        var iasHot = hotScore.Criteria.First(c => c.Id == "airspeed");
        var excessHot = hotScore.Criteria.First(c => c.Id == "excess_speed");

        Assert.True(iasOn.ScorePercent >= 99, $"on-target IAS should be ~100%, got {iasOn.ScorePercent}");
        Assert.True(iasHot.ScorePercent < iasOn.ScorePercent);
        Assert.True(excessHot.ScorePercent < 50, $"excess +15 over VAPP should be penalized, got {excessHot.ScorePercent}");
    }

    [Fact]
    public void SpeedTarget_UsesChallengeVappMinusFive()
    {
        var challenge = LoadChallenge();
        var profile = LoadProfile();
        challenge.AircraftSetup.VappKts = 143;
        var (vapp, target, source) = SpeedTargetCalculator.Resolve(challenge, profile);
        Assert.Equal(143, vapp);
        Assert.Equal(138, target);
        Assert.Contains("challenge", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CenterlineScore_MatchesRecommendedCurve()
    {
        // Normal: t=3, z=15, p=1.5
        Assert.Equal(100.0, CenterlineScore.Calculate(0), 1);
        Assert.Equal(100.0, CenterlineScore.Calculate(3), 1);
        Assert.InRange(CenterlineScore.Calculate(4), 97, 99);
        Assert.InRange(CenterlineScore.Calculate(5), 92, 95);
        Assert.InRange(CenterlineScore.Calculate(6), 86, 90);
        Assert.InRange(CenterlineScore.Calculate(8), 70, 76);
        Assert.InRange(CenterlineScore.Calculate(10), 52, 58);
        Assert.InRange(CenterlineScore.Calculate(12), 32, 38);
        Assert.InRange(CenterlineScore.Calculate(14), 10, 16);
        Assert.Equal(0.0, CenterlineScore.Calculate(15), 1);
        Assert.Equal(0.0, CenterlineScore.Calculate(20), 1);
    }

    [Fact]
    public void CenterlineEvaluator_UsesSingleCurveParams()
    {
        var criterion = new CriterionConfig
        {
            Evaluator = "centerline",
            Params = new Dictionary<string, double>
            {
                ["tolerance"] = 1.5,
                ["zeroAt"] = 10,
                ["exponent"] = 1.3
            }
        };

        Assert.Equal(1.0, CenterlineEvaluator.Instance.Evaluate(0, criterion), 3);
        Assert.Equal(1.0, CenterlineEvaluator.Instance.Evaluate(1.5, criterion), 3);
        Assert.True(CenterlineEvaluator.Instance.Evaluate(3, criterion) < 1.0);
        Assert.Equal(0.0, CenterlineEvaluator.Instance.Evaluate(10, criterion), 3);
    }

    private static TelemetrySample Sample(
        bool onGround, double gsKts, double vs, double lat, double lon, double g = 1.0,
        DateTimeOffset? timestamp = null, double? track = null) => new()
    {
        Timestamp = timestamp ?? DateTimeOffset.UtcNow,
        Latitude = lat,
        Longitude = lon,
        AltitudeFeet = onGround ? 14 : 800,
        AglFeet = onGround ? 0 : 800,
        HeadingTrueDeg = 246,
        GroundTrackTrueDeg = track ?? 246,
        PitchDeg = 3,
        BankDeg = 0,
        AirspeedKts = gsKts,
        GroundSpeedKts = gsKts,
        VerticalSpeedFpm = vs,
        GForce = g,
        SimOnGround = onGround,
        GearHandlePosition = 1,
        FlapsHandleIndex = 4,
        WindDirectionDeg = 340,
        WindVelocityKts = 28,
        RadioHeightFeet = onGround ? 0 : 800
    };
}

