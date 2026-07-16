using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class NoseGearImpactGateTests
{
    [Fact]
    public void ExactModerateAndSevereThresholds_AreInclusive()
    {
        var cfg = Config();

        var moderate = NoseGearImpactCalculator.Analyze(
            SingleContact(preG: 1.05, postG: 1.30), 10, cfg);
        var severe = NoseGearImpactCalculator.Analyze(
            SingleContact(preG: 1.20, postG: 1.70), 10, cfg);
        var belowModerate = NoseGearImpactCalculator.Analyze(
            SingleContact(preG: 1.05, postG: 1.299), 10, cfg);
        var belowSevere = NoseGearImpactCalculator.Analyze(
            SingleContact(preG: 1.201, postG: 1.70), 10, cfg);

        Assert.Equal(NoseGearImpactSeverity.Moderate, moderate.WorstEvent!.Severity);
        Assert.Equal(0.95, moderate.WorstEvent.AppliedMultiplier, 6);
        Assert.Equal(NoseGearImpactSeverity.Severe, severe.WorstEvent!.Severity);
        Assert.Equal(0.90, severe.WorstEvent.AppliedMultiplier, 6);
        Assert.Equal(NoseGearImpactSeverity.Pass, belowModerate.WorstEvent!.Severity);
        Assert.Equal(NoseGearImpactSeverity.Moderate, belowSevere.WorstEvent!.Severity);
    }

    [Fact]
    public void GentleModerateAndSevereLowerings_AreSeparatedByRobustDeltaG()
    {
        var cfg = Config();
        var gentle = NoseGearImpactCalculator.Analyze(SingleContact(1.0, 1.01), 10, cfg);
        var moderate = NoseGearImpactCalculator.Analyze(SingleContact(1.0, 1.40), 10, cfg);
        var severe = NoseGearImpactCalculator.Analyze(SingleContact(1.0, 1.80), 10, cfg);

        Assert.Equal(NoseGearImpactSeverity.Pass, gentle.WorstEvent!.Severity);
        Assert.Equal(NoseGearImpactSeverity.Moderate, moderate.WorstEvent!.Severity);
        Assert.Equal(NoseGearImpactSeverity.Severe, severe.WorstEvent!.Severity);
    }

    [Fact]
    public void ObservedLaPazPatternIsSevere_WhileObservedGentlePatternPasses()
    {
        var gentle = NoseGearImpactCalculator.Analyze(
            SingleContact(0.972, 0.981), 10, Config());
        var laPaz = SingleContact(1.059, 1.761);
        var contactIndex = laPaz.FindIndex(s => Math.Abs(s.TimeSeconds - 10) < 0.0001);
        laPaz[contactIndex] = laPaz[contactIndex] with { GForce = 2.496 };
        var hard = NoseGearImpactCalculator.Analyze(laPaz, 10, Config());

        Assert.Equal(NoseGearImpactSeverity.Pass, gentle.WorstEvent!.Severity);
        Assert.Equal(NoseGearImpactSeverity.Severe, hard.WorstEvent!.Severity);
        Assert.Equal(2.496, hard.WorstEvent.RawPeakG, 3);
        Assert.True(hard.WorstEvent.DeltaG >= 0.5);
    }

    [Fact]
    public void IsolatedFiniteSpikeIsRejected_AndSpikeOutsideWindowIsIgnored()
    {
        var cfg = Config();
        var isolated = SingleContact(1, 1);
        var spikeIndex = isolated.FindIndex(s => Math.Abs(s.TimeSeconds - 10.30) < 0.0001);
        isolated[spikeIndex] = isolated[spikeIndex] with { GForce = 2.5 };

        var isolatedResult = NoseGearImpactCalculator.Analyze(isolated, 10, cfg);
        Assert.Equal(2.5, isolatedResult.WorstEvent!.RawPeakG, 3);
        Assert.Equal(NoseGearImpactSeverity.Pass, isolatedResult.WorstEvent.Severity);
        Assert.InRange(isolatedResult.WorstEvent.RobustPeakG, 0.99, 1.01);

        var outside = SingleContact(1, 1);
        outside.Add(Sample(11, 3.0, noseOnGround: true));
        var outsideResult = NoseGearImpactCalculator.Analyze(outside, 10, cfg);
        Assert.Equal(NoseGearImpactSeverity.Pass, outsideResult.WorstEvent!.Severity);
        Assert.InRange(outsideResult.WorstEvent.RawPeakG, 0.99, 1.01);
    }

    [Fact]
    public void NoseBounceRecontact_UsesWorstEventOnce()
    {
        var cfg = Config();
        var samples = MultiContactSamples(chatterSeconds: 0.20);
        var result = NoseGearImpactCalculator.Analyze(samples, 10, cfg);

        Assert.True(result.CoverageSufficient, result.DegradedReason);
        Assert.Equal(2, result.Events.Count);
        Assert.Equal(NoseGearImpactSeverity.Moderate, result.Events[0].Severity);
        Assert.Equal(NoseGearImpactSeverity.Severe, result.Events[1].Severity);
        Assert.Same(result.Events[1], result.WorstEvent);
        Assert.Equal(0.9, result.WorstEvent!.AppliedMultiplier, 6);
    }

    [Fact]
    public void NoseContactChatterShorterThanDebounce_DoesNotCreateAnotherImpact()
    {
        var result = NoseGearImpactCalculator.Analyze(
            MultiContactSamples(chatterSeconds: 0.04), 10, Config());

        Assert.True(result.CoverageSufficient, result.DegradedReason);
        Assert.Single(result.Events);
        Assert.Equal(10, result.Events[0].ContactTimeSeconds, 3);
    }

    [Fact]
    public void MultipleContactPointTransitions_CorroborateDynamicNoseMapping()
    {
        var result = NoseGearImpactCalculator.Analyze(
            SingleContact(1, 1.1, compression: true), 10, Config());

        Assert.True(result.CoverageSufficient, result.DegradedReason);
        var impact = Assert.Single(result.Events);
        Assert.True(impact.CompressionTelemetryAvailable);
        Assert.True(impact.CompressionCorroborated);
        Assert.False(impact.CompressionFallbackUsed);
        Assert.Equal(new[] { 4, 5 }, impact.CorrelatedContactPointIndices);
        Assert.Equal(0.12, impact.PeakCompression!.Value, 3);
        Assert.Equal(0.12, impact.CompressionRise!.Value, 3);
    }

    [Fact]
    public void MissingCompressionUsesRankedGFallback()
    {
        var result = NoseGearImpactCalculator.Analyze(
            SingleContact(1, 1.4), 10, Config());

        Assert.True(result.CoverageSufficient, result.DegradedReason);
        Assert.True(result.CompressionFallbackUsed);
        Assert.True(result.WorstEvent!.CompressionFallbackUsed);
        Assert.Equal(NoseGearImpactSeverity.Moderate, result.WorstEvent.Severity);
    }

    [Fact]
    public void MissingNoseContactOrUsableG_MakesAnalysisUnavailable()
    {
        var noNose = SingleContact(1, 1.4)
            .Select(s => s with { NoseGearContactAvailable = false, NoseOnGround = false })
            .ToList();
        var noG = SingleContact(1, 1.4)
            .Select(s => s with { GForceAvailable = false })
            .ToList();

        var noNoseResult = NoseGearImpactCalculator.Analyze(noNose, 10, Config());
        var noGResult = NoseGearImpactCalculator.Analyze(noG, 10, Config());

        Assert.False(noNoseResult.CoverageSufficient);
        Assert.Contains("contact mapping", noNoseResult.DegradedReason,
            StringComparison.OrdinalIgnoreCase);
        Assert.False(noGResult.CoverageSufficient);
        Assert.Contains("G telemetry", noGResult.DegradedReason,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LandingSession_EndToEndProducesWorstImpactBeforeScoringSettles()
    {
        var loader = new ConfigLoader(FindConfig());
        var key = loader.LoadEvaluationKey().Key!;
        var challenge = loader.LoadChallenge("challenges/barcelona-crosswind-final.json");
        var settings = key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false,
            SettledHoldSeconds = 0,
            OperationalGates = new OperationalGateSessionSettings(
                NoseGearImpact: key.Gates!.NoseGearImpact)
        };
        var session = new LandingSession(challenge, settings);
        session.Arm();

        for (var i = 0; i < 25; i++)
            session.Ingest(SessionSample(0.75 + i / 100.0,
                leftMain: false, rightMain: false, nose: false, g: 1, groundSpeed: 120));
        for (var i = 0; i < 20; i++)
            session.Ingest(SessionSample(1 + i / 100.0,
                leftMain: true, rightMain: true, nose: false, g: 1, groundSpeed: 100));
        for (var i = 0; i <= 80; i++)
        {
            var time = 1.20 + i / 100.0;
            var g = time <= 1.60 ? 1.8 : 1.0;
            session.Ingest(SessionSample(time,
                leftMain: true, rightMain: true, nose: true, g, groundSpeed: 100));
        }
        session.Ingest(SessionSample(2.01,
            leftMain: true, rightMain: true, nose: true, g: 1, groundSpeed: 40));

        Assert.True(session.IsComplete);
        var analysis = Assert.IsType<NoseGearImpactAnalysis>(
            session.Snapshot.GateObservations.NoseGearImpact);
        Assert.True(analysis.CoverageSufficient, analysis.DegradedReason);
        Assert.Equal(NoseGearImpactSeverity.Severe, analysis.WorstEvent!.Severity);
        Assert.True(analysis.CompressionFallbackUsed);
    }

    private static NoseGearImpactGateConfig Config() => new()
    {
        PreContactWindowSeconds = 0.25,
        PostContactWindowSeconds = 0.75,
        FilterCutoffHz = 10,
        PeakQuantile = 0.99,
        MinimumPostContactSamples = 8,
        ModerateDeltaG = 0.25,
        ModeratePeakG = 1.30,
        ModerateMultiplier = 0.95,
        SevereDeltaG = 0.50,
        SeverePeakG = 1.70,
        SevereMultiplier = 0.90,
        RecontactDebounceSeconds = 0.08,
        CompressionNoiseThreshold = 0.02
    };

    private static List<LandingTelemetrySample> SingleContact(
        double preG,
        double postG,
        bool compression = false)
    {
        var samples = new List<LandingTelemetrySample>();
        for (var i = -25; i <= 75; i++)
        {
            var offset = i / 100.0;
            var onGround = offset >= 0;
            IReadOnlyDictionary<int, bool>? contact = null;
            IReadOnlyDictionary<int, double>? strut = null;
            if (compression)
            {
                contact = new Dictionary<int, bool> { [4] = onGround, [5] = onGround, [7] = false };
                strut = new Dictionary<int, double>
                {
                    [4] = onGround ? 0.12 : 0,
                    [5] = onGround ? 0.08 : 0,
                    [7] = 0
                };
            }
            samples.Add(Sample(
                10 + offset,
                onGround ? postG : preG,
                onGround,
                contact,
                strut,
                compression));
        }
        return samples;
    }

    private static List<LandingTelemetrySample> MultiContactSamples(double chatterSeconds)
    {
        var samples = new List<LandingTelemetrySample>();
        for (var i = -25; i <= 190; i++)
        {
            var time = 10 + i / 100.0;
            var firstContact = time >= 10;
            var airborneStart = 10.80;
            var secondContact = airborneStart + chatterSeconds;
            var nose = firstContact && !(time >= airborneStart && time < secondContact);
            var g = time is >= 10 and <= 10.40 ? 1.40
                : time >= secondContact && time <= secondContact + 0.40 ? 1.80
                : 1.0;
            samples.Add(Sample(time, g, nose));
        }
        return samples;
    }

    private static LandingTelemetrySample Sample(
        double time,
        double g,
        bool noseOnGround,
        IReadOnlyDictionary<int, bool>? contact = null,
        IReadOnlyDictionary<int, double>? compression = null,
        bool contactCoverage = false) => new(
        TimeSeconds: time,
        AglFeet: 0,
        GroundSpeedKts: 100,
        VerticalSpeedFpm: 0,
        GForce: g,
        LeftMainOnGround: true,
        RightMainOnGround: true,
        NoseOnGround: noseOnGround,
        MainGearContactsAvailable: true,
        NoseGearContactAvailable: true,
        GForceAvailable: true,
        ContactPointOnGroundByIndex: contact,
        ContactPointCompressionByIndex: compression,
        ContactPointTelemetryAvailable: contactCoverage);

    private static TelemetrySample SessionSample(
        double time,
        bool leftMain,
        bool rightMain,
        bool nose,
        double g,
        double groundSpeed) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(time),
        SimulationTimeSeconds = time,
        Latitude = 41.3,
        Longitude = 2.1,
        AglFeet = leftMain || rightMain ? 0 : 100,
        RadioHeightFeet = leftMain || rightMain ? 0 : 100,
        RadioHeightAvailable = true,
        AirspeedKts = 120,
        GroundSpeedKts = groundSpeed,
        VerticalSpeedFpm = 0,
        GForce = g,
        GForceAvailable = true,
        SimOnGround = leftMain || rightMain,
        GearOnGroundByIndex = new Dictionary<int, bool>
        {
            [0] = nose,
            [1] = leftMain,
            [2] = rightMain
        },
        IsGearWheels = true,
        GearHandlePosition = 1,
        FlapsHandleIndex = 3
    };

    private static string FindConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "config", "catalog.json")))
                return Path.Combine(directory.FullName, "config");
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("config not found");
    }
}
