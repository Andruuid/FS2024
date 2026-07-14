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
    public void BandEvaluator_PrefersFirmOverButter()
    {
        var criterion = new CriterionConfig
        {
            Evaluator = "band",
            Params = new Dictionary<string, double>
            {
                ["peakMin"] = -180,
                ["peakMax"] = -80,
                ["zeroScoreBelow"] = -420,
                ["zeroScoreAbove"] = -15
            }
        };

        var firm = BandEvaluator.Instance.Evaluate(-120, criterion);
        var butter = BandEvaluator.Instance.Evaluate(-20, criterion);
        var hard = BandEvaluator.Instance.Evaluate(-400, criterion);

        Assert.True(firm > 0.9);
        Assert.True(butter < firm);
        Assert.True(hard < firm);
        Assert.True(butter < 0.3);
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
    public void LandingSession_SettlesBelow50Kmh()
    {
        var challenge = LoadChallenge();
        var profile = LoadProfile();
        profile.SettledHoldSeconds = 0.05;
        var session = new LandingSession(challenge, profile);
        session.Arm();

        var settled = false;
        session.SettledReady += (_, _) => settled = true;

        var t0 = DateTimeOffset.UtcNow;
        // Approach sample in air
        session.Ingest(Sample(onGround: false, gsKts: 140, vs: -700, lat: 41.32, lon: 2.12, timestamp: t0));
        // Touchdown
        session.Ingest(Sample(onGround: true, gsKts: 130, vs: -120, lat: 41.294, lon: 2.084, g: 1.25, timestamp: t0.AddSeconds(1)));
        // Slow rollout — first low-GS sample starts hold timer
        session.Ingest(Sample(onGround: true, gsKts: 20, vs: 0, lat: 41.293, lon: 2.083, g: 1.0, timestamp: t0.AddSeconds(2)));
        // Still slow after hold duration → settled
        session.Ingest(Sample(onGround: true, gsKts: 15, vs: 0, lat: 41.292, lon: 2.082, g: 1.0, timestamp: t0.AddSeconds(2.2)));

        Assert.True(settled);
        Assert.Equal(LandingPhase.Scored, session.Phase);
        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.InRange(session.Snapshot.VerticalSpeedAtTouchdownFpm, -150, -100);
    }

    private static LandingSnapshot FirmCrosswindSnapshot() => new()
    {
        VerticalSpeedAtTouchdownFpm = -120,
        PeakGForce = 1.25,
        TouchdownLateralOffsetM = 2,
        MaxLateralOffsetM = 5,
        TouchdownHeadingErrorDeg = 1.5,
        AirspeedAtTouchdownKts = 142,
        BankAtTouchdownDeg = 0.8,
        PitchAtTouchdownDeg = 4,
        GearDownAtTouchdown = true,
        FlapsIndexAtTouchdown = 4,
        ApproachPathRms = 60,
        RolloutHeadingVariance = 1.5,
        CrabAngleAtFlareDeg = 2
    };

    private static LandingSnapshot ButterSnapshot() => new()
    {
        VerticalSpeedAtTouchdownFpm = -18,
        PeakGForce = 1.02,
        TouchdownLateralOffsetM = 2,
        MaxLateralOffsetM = 5,
        TouchdownHeadingErrorDeg = 1.5,
        AirspeedAtTouchdownKts = 142,
        BankAtTouchdownDeg = 0.8,
        PitchAtTouchdownDeg = 4,
        GearDownAtTouchdown = true,
        FlapsIndexAtTouchdown = 4,
        ApproachPathRms = 60,
        RolloutHeadingVariance = 1.5,
        CrabAngleAtFlareDeg = 2
    };

    private static TelemetrySample Sample(
        bool onGround, double gsKts, double vs, double lat, double lon, double g = 1.0,
        DateTimeOffset? timestamp = null) => new()
    {
        Timestamp = timestamp ?? DateTimeOffset.UtcNow,
        Latitude = lat,
        Longitude = lon,
        AltitudeFeet = onGround ? 14 : 800,
        AglFeet = onGround ? 0 : 800,
        HeadingTrueDeg = 246,
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
