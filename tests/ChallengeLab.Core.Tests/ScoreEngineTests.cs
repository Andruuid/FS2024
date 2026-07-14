using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Tests;

public class ScoreEngineTests
{
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
    public void Piecewise_A330TouchdownVs_Curve()
    {
        var criterion = new CriterionConfig
        {
            Evaluator = "piecewise",
            Points = new List<ScorePoint>
            {
                new() { V = -600, S = 0.00 },
                new() { V = -500, S = 0.10 },
                new() { V = -400, S = 0.40 },
                new() { V = -320, S = 0.70 },
                new() { V = -250, S = 0.90 },
                new() { V = -180, S = 1.00 },
                new() { V = -100, S = 1.00 },
                new() { V = -60, S = 0.90 },
                new() { V = -20, S = 0.35 },
                new() { V = 0, S = 0.15 },
                new() { V = 50, S = 0.00 }
            }
        };

        var ideal = PiecewiseEvaluator.Instance.Evaluate(-150, criterion);
        var softEdge = PiecewiseEvaluator.Instance.Evaluate(-80, criterion);
        var firmEdge = PiecewiseEvaluator.Instance.Evaluate(-220, criterion);
        var butter = PiecewiseEvaluator.Instance.Evaluate(-20, criterion);
        var hard = PiecewiseEvaluator.Instance.Evaluate(-400, criterion);
        var veryHard = PiecewiseEvaluator.Instance.Evaluate(-500, criterion);

        Assert.Equal(1.0, ideal, 3);
        Assert.InRange(softEdge, 0.90, 1.0);   // between -100 and -60
        Assert.InRange(firmEdge, 0.90, 1.0);   // between -250 and -180
        Assert.InRange(butter, 0.30, 0.45);    // float penalty
        Assert.InRange(hard, 0.35, 0.45);      // hard landing ~40%
        Assert.True(veryHard < hard);
        Assert.True(ideal > butter);
        Assert.True(ideal > hard);
    }

    [Fact]
    public void Easy_OmitsStrictOnlyCriteria()
    {
        var engine = new ScoreEngine();
        var challenge = LoadChallenge();
        var profile = LoadProfile();
        var snap = FirmCrosswindSnapshot();

        var easy = engine.Evaluate(challenge, profile, snap, DifficultyLevel.Easy);
        var strict = engine.Evaluate(challenge, profile, snap, DifficultyLevel.Strict);

        Assert.Contains(easy.Criteria, c => c.Id == "approach_path" && !c.Applied);
        Assert.Contains(strict.Criteria, c => c.Id == "approach_path" && c.Applied);
        Assert.True(easy.Criteria.Count(c => c.Applied) < strict.Criteria.Count(c => c.Applied));
    }

    [Fact]
    public void FirmLanding_ScoresHigherThanButter_OnCrosswindProfile()
    {
        var engine = new ScoreEngine();
        var challenge = LoadChallenge();
        var profile = LoadProfile();

        var firm = engine.Evaluate(challenge, profile, FirmCrosswindSnapshot(), DifficultyLevel.Easy);
        var butter = engine.Evaluate(challenge, profile, ButterSnapshot(), DifficultyLevel.Easy);

        Assert.True(firm.ScorePercent > butter.ScorePercent,
            $"Expected firm {firm.ScorePercent} > butter {butter.ScorePercent}");
        Assert.True(firm.ScorePercent >= 70);
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
        // Still above 50 kt — should not settle
        session.Ingest(Sample(onGround: true, gsKts: 55, vs: 0, lat: 41.2935, lon: 2.0835, g: 1.0, timestamp: t0.AddSeconds(1.5)));
        // Under 50 kt — first low-GS sample starts hold timer
        session.Ingest(Sample(onGround: true, gsKts: 45, vs: 0, lat: 41.293, lon: 2.083, g: 1.0, timestamp: t0.AddSeconds(2)));
        // Still under 50 kt after hold → settled
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
        AirspeedAtTouchdownKts = 140, // ~ target 138 (VAPP 143 − 5)
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

        var engine = new ScoreEngine();
        var result = engine.Evaluate(challenge, profile, session.Snapshot, DifficultyLevel.Easy);
        Assert.Contains(result.Criteria, c => c.Id == "ground_track" && c.Applied);
        Assert.DoesNotContain(result.Criteria, c => c.Id == "crab" && c.Applied);
    }

    [Fact]
    public void GearDown_DoesNotInflateScore_GearUpAppliesHeavyPenalty()
    {
        var engine = new ScoreEngine();
        var challenge = LoadChallenge();
        challenge.RequireGearDown = true;
        var profile = LoadProfile();
        profile.GearUpScoreMultiplier = 0.1;

        var gearDown = FirmCrosswindSnapshot();
        gearDown.GearDownAtTouchdown = true;
        var gearUp = FirmCrosswindSnapshot();
        gearUp.GearDownAtTouchdown = false;

        var ok = engine.Evaluate(challenge, profile, gearDown, DifficultyLevel.Easy);
        var bad = engine.Evaluate(challenge, profile, gearUp, DifficultyLevel.Easy);

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
        var engine = new ScoreEngine();
        var challenge = LoadChallenge();
        challenge.RequireGearDown = false; // water / belly landing
        var profile = LoadProfile();

        var gearUp = FirmCrosswindSnapshot();
        gearUp.GearDownAtTouchdown = false;
        var withGear = FirmCrosswindSnapshot();
        withGear.GearDownAtTouchdown = true;

        var a = engine.Evaluate(challenge, profile, gearUp, DifficultyLevel.Easy);
        var b = engine.Evaluate(challenge, profile, withGear, DifficultyLevel.Easy);

        Assert.False(a.GearUpPenaltyApplied);
        Assert.Equal(b.ScorePercent, a.ScorePercent);
    }

    [Fact]
    public void RolloutWeave_SteadyPathScoresBetterThanSTurns()
    {
        var engine = new ScoreEngine();
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

        var steadyScore = engine.Evaluate(challenge, profile, steady, DifficultyLevel.Easy);
        var weaveScore = engine.Evaluate(challenge, profile, weave, DifficultyLevel.Easy);

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
        var engine = new ScoreEngine();
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

        var onScore = engine.Evaluate(challenge, profile, onTarget, DifficultyLevel.Easy);
        var hotScore = engine.Evaluate(challenge, profile, hot, DifficultyLevel.Easy);

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
    public void CenterlineEvaluator_StrictIsTighterThanEasy()
    {
        var criterion = new CriterionConfig
        {
            Evaluator = "centerline",
            Params = new Dictionary<string, double>
            {
                ["easyTolerance"] = 4,
                ["easyZeroAt"] = 18,
                ["easyExponent"] = 1.7,
                ["strictTolerance"] = 1.5,
                ["strictZeroAt"] = 10,
                ["strictExponent"] = 1.3
            }
        };

        // 3 m: Easy still 100%, Strict already past full-score band
        var easy3 = CenterlineEvaluator.Instance.Evaluate(3, criterion, DifficultyLevel.Easy);
        var strict3 = CenterlineEvaluator.Instance.Evaluate(3, criterion, DifficultyLevel.Strict);
        Assert.Equal(1.0, easy3, 3);
        Assert.True(strict3 < 1.0, "Strict should not give 100% at 3 m off center");

        // 8 m: both penalized, Strict harsher
        var easy8 = CenterlineEvaluator.Instance.Evaluate(8, criterion, DifficultyLevel.Easy);
        var strict8 = CenterlineEvaluator.Instance.Evaluate(8, criterion, DifficultyLevel.Strict);
        Assert.True(strict8 < easy8);
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
