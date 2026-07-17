using System.Text.Json;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class FreeModeScoringParityTests
{
    [Fact]
    public void FreeV12_IsAStructuralOverlayOfLandingV26()
    {
        var loader = new ConfigLoader(FindConfig());
        var catalog = loader.LoadCatalog();
        var normal = loader.LoadEvaluationKey(catalog.EvaluationKey).Key!;
        var free = loader.LoadEvaluationKey(catalog.FreeFlightEvaluationKey).Key!;

        Assert.Equal(26, normal.Version);
        Assert.Equal(12, free.Version);
        Assert.NotNull(free.FreeMode);
        Assert.Equal(50, free.FreeMode!.UnavailableMetricScorePercent);
        Assert.Equal(0.5, free.FreeMode.MissingGatePenaltyFraction);
        Assert.Equal(70, free.SpeedTarget!.DefaultVappKts);
        Assert.Equal(normal.SpeedTarget!.TouchdownOffsetKts, free.SpeedTarget.TouchdownOffsetKts);
        Assert.Equal(normal.SpeedTarget.Vs0Factor, free.SpeedTarget.Vs0Factor);

        AssertJsonEqual(normal.Settle, free.Settle);
        AssertJsonEqual(normal.Timing, free.Timing);
        AssertJsonEqual(normal.ContactMapping, free.ContactMapping);
        AssertJsonEqual(normal.GeneralPenalties, free.GeneralPenalties);
        AssertJsonEqual(normal.Phases, free.Phases);
    }

    [Fact]
    public void Overlay_RejectsStructuralOverrides()
    {
        var source = Path.Combine(FindConfig(), "scoring", "profiles", "landing-evaluation-key.json");
        var directory = Path.Combine(Path.GetTempPath(), "ChallengeLabTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.Copy(source, Path.Combine(directory, "base.json"));
            var overlay = """
                          {
                            "id": "bad-free",
                            "version": 8,
                            "inherits": "base.json",
                            "freeMode": {
                              "unavailableMetricScorePercent": 50,
                              "missingGatePenaltyFraction": 0.5
                            },
                            "phases": []
                          }
                          """;
            var path = Path.Combine(directory, "overlay.json");
            File.WriteAllText(path, overlay);

            var loaded = new ConfigLoader(directory).LoadEvaluationKey(path);
            Assert.False(loaded.IsValid);
            Assert.Contains(loaded.Errors, error =>
                error.Contains("unmapped", StringComparison.OrdinalIgnoreCase)
                || error.Contains("phases", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FreeOverlay_IgnoresChallengeSpecificReverseExceptions()
    {
        var loader = new ConfigLoader(FindConfig());
        var free = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey).Key!;
        var arctic = loader.LoadChallenge("challenges/career-arctic-ice-runway-rescue.json");

        var effective = EffectiveEvaluationProfileBuilder.Build(free, arctic).Key;
        var reverse = effective.Phases.Single(phase => phase.Id == "rollout")
            .Penalties!.ReverseThrust!;

        Assert.Equal(ReverseThrustPolicies.Required, reverse.Policy);
        Assert.Null(reverse.ExceptionReason);
    }

    [Fact]
    public void CompletelyUnavailableFreeLanding_IsRankedAndUsesDocumentedFallbacks()
    {
        var loader = new ConfigLoader(FindConfig());
        var free = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey).Key!;
        var challenge = MinimalFreeChallenge();
        var snapshot = new LandingSnapshot { StallWarningCoverageAvailable = false };

        var result = new ScoreEngine(free).Evaluate(challenge, snapshot);

        Assert.True(result.IsRanked, string.Join("; ", result.IncompleteReasons));
        Assert.NotNull(result.ScorePercent);
        Assert.All(free.Phases.SelectMany(phase => phase.Metrics), metric =>
        {
            var criterion = result.Criteria.Single(item => item.Id == metric.Id);
            Assert.Equal(MetricStatus.Assumed, criterion.Status);
            Assert.Equal(50, criterion.ScorePercent);
        });

        var expected = new Dictionary<string, double>
        {
            [FreeFlightGateIds.ContactStability] = 0.95,
            [FreeFlightGateIds.Gear] = 0.55,
            [FreeFlightGateIds.Flaps] = 0.90,
            [FreeFlightGateIds.Spoilers] = 0.95,
            [FreeFlightGateIds.NoseGearImpact] = 0.975,
            [FreeFlightGateIds.StallWarning] = 0.75,
            [FreeFlightGateIds.Automation] = 0.95,
            [FreeFlightGateIds.ManualBraking] = 0.95,
            [FreeFlightGateIds.RolloutDistance] = 0.90,
            [FreeFlightGateIds.ReverseThrust] = 0.95,
            [FreeFlightGateIds.PauseUsage] = 0.975,
            [FreeFlightGateIds.SimulationRate] = 0.90,
            [FreeFlightGateIds.CockpitView] = 0.975
        };
        foreach (var (id, multiplier) in expected)
        {
            var criterion = result.Criteria.Single(item => item.Id == id);
            Assert.Equal(MetricStatus.Assumed, criterion.Status);
            Assert.Equal(multiplier, criterion.AppliedMultiplier!.Value, 6);
        }
    }

    [Fact]
    public void FreePreview_IsOptimisticUntilFinalThenUsesAssumedFallbacks()
    {
        var loader = new ConfigLoader(FindConfig());
        var free = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey).Key!;
        var challenge = MinimalFreeChallenge();
        var snapshot = new LandingSnapshot { StallWarningCoverageAvailable = false };
        var engine = new ScoreEngine(free);

        var preview = engine.EvaluatePreview(challenge, snapshot);
        var final = engine.Evaluate(challenge, snapshot);

        Assert.Equal(100, preview.ScorePercent);
        Assert.False(preview.IsRanked);
        Assert.DoesNotContain(preview.Criteria, item => item.Status == MetricStatus.Assumed);
        Assert.True(final.IsRanked);
        Assert.Contains(final.Criteria, item => item.Status == MetricStatus.Assumed);
        Assert.True(final.ScorePercent < preview.ScorePercent);
    }

    [Fact]
    public void FreeFlightFlapsGate_AcceptsHandleIndexBeyondFrozenPositionCount()
    {
        // Regression: FLAPS NUM HANDLE POSITIONS can freeze low (e.g. 4 → band max 3)
        // while FLAPS HANDLE INDEX at touchdown is a deeper detent (e.g. 4). That must
        // count as landing flaps, not "Flaps not set".
        var loader = new ConfigLoader(FindConfig());
        var free = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey).Key!;
        var challenge = MinimalFreeChallenge();
        challenge.FreeFlightCapabilities = FreeFlightCapabilityResolver.Freeze(
            new TelemetrySample
            {
                IsGearWheels = true,
                IsGearRetractable = true,
                FlapsHandlePositionCount = 4,
                SpoilersAvailable = true,
                AutopilotAvailable = true,
                ThrottleLowerLimitPercent = -20
            }, false);
        var snapshot = FullyMeasuredSnapshot();
        snapshot.FlapsIndexAtTouchdown = 4;

        var result = new ScoreEngine(free).Evaluate(challenge, snapshot);
        Assert.False(result.FlapsPenaltyApplied);
        var flaps = result.Criteria.Single(c => c.Id == FreeFlightGateIds.Flaps);
        Assert.Equal(MetricStatus.Informational, flaps.Status);
        Assert.Equal(1, flaps.AppliedMultiplier);
        Assert.Equal(4, flaps.RawValue);
        Assert.Contains("[2…4]", flaps.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void FreeFlightFlapsGate_StillPenalizesBelowLandingMinIndex()
    {
        var loader = new ConfigLoader(FindConfig());
        var free = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey).Key!;
        var challenge = MinimalFreeChallenge();
        challenge.FreeFlightCapabilities = FreeFlightCapabilityResolver.Freeze(
            new TelemetrySample
            {
                IsGearWheels = true,
                IsGearRetractable = true,
                FlapsHandlePositionCount = 4,
                SpoilersAvailable = true,
                AutopilotAvailable = true,
                ThrottleLowerLimitPercent = -20
            }, false);
        var snapshot = FullyMeasuredSnapshot();
        snapshot.FlapsIndexAtTouchdown = 1;

        var result = new ScoreEngine(free).Evaluate(challenge, snapshot);
        Assert.True(result.FlapsPenaltyApplied);
        var flaps = result.Criteria.Single(c => c.Id == FreeFlightGateIds.Flaps);
        Assert.Equal(MetricStatus.GateFailed, flaps.Status);
        Assert.Equal(0.8, flaps.AppliedMultiplier);
        Assert.Contains("outside landing band [2…3]", flaps.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void FullyMeasuredCapableLanding_ScoresIdenticallyInChallengeAndFree()
    {
        var loader = new ConfigLoader(FindConfig());
        var catalog = loader.LoadCatalog();
        var normal = loader.LoadEvaluationKey(catalog.EvaluationKey).Key!;
        var free = loader.LoadEvaluationKey(catalog.FreeFlightEvaluationKey).Key!;
        var challenge = MinimalFreeChallenge();
        challenge.FreeFlightCapabilities = FreeFlightCapabilityResolver.Freeze(CapableJet(), false);
        var snapshot = FullyMeasuredSnapshot();

        var normalResult = new ScoreEngine(normal).Evaluate(challenge, snapshot);
        var freeResult = new ScoreEngine(free).Evaluate(challenge, snapshot);

        Assert.True(normalResult.IsRanked, string.Join("; ", normalResult.IncompleteReasons));
        Assert.True(freeResult.IsRanked, string.Join("; ", freeResult.IncompleteReasons));
        Assert.Equal(normalResult.ScorePercent, freeResult.ScorePercent);
        Assert.Equal(
            normalResult.Criteria.Select(item => (item.Id, item.Status, item.ScorePercent, item.AppliedMultiplier)),
            freeResult.Criteria.Select(item => (item.Id, item.Status, item.ScorePercent, item.AppliedMultiplier)));
    }

    [Theory]
    [MemberData(nameof(CapabilityCases))]
    public void CapabilityMatrix_FreezesExpectedNotApplicableGates(
        TelemetrySample sample,
        bool waterRunway,
        string[] notApplicable,
        string[] applicable)
    {
        var context = FreeFlightCapabilityResolver.Freeze(sample, waterRunway);
        foreach (var id in notApplicable)
            Assert.Equal(FreeFlightGateApplicability.NotApplicable,
                context.DecisionFor(id)!.Applicability);
        foreach (var id in applicable)
            Assert.Equal(FreeFlightGateApplicability.Applicable,
                context.DecisionFor(id)!.Applicability);
    }

    public static IEnumerable<object[]> CapabilityCases()
    {
        yield return
        [
            CapableJet(), false, Array.Empty<string>(),
            new[] { FreeFlightGateIds.Gear, FreeFlightGateIds.Flaps, FreeFlightGateIds.Spoilers,
                FreeFlightGateIds.Automation, FreeFlightGateIds.ReverseThrust,
                FreeFlightGateIds.ContactStability, FreeFlightGateIds.NoseGearImpact,
                FreeFlightGateIds.ManualBraking }
        ];
        yield return
        [
            new TelemetrySample
            {
                IsGearWheels = true, IsGearRetractable = false, FlapsHandlePositionCount = 4,
                SpoilersAvailable = false, AutopilotAvailable = false, ThrottleLowerLimitPercent = 0
            },
            false,
            new[] { FreeFlightGateIds.Gear, FreeFlightGateIds.Spoilers,
                FreeFlightGateIds.Automation, FreeFlightGateIds.ReverseThrust },
            new[] { FreeFlightGateIds.Flaps, FreeFlightGateIds.ContactStability,
                FreeFlightGateIds.NoseGearImpact, FreeFlightGateIds.ManualBraking }
        ];
        yield return
        [
            new TelemetrySample
            {
                IsGearWheels = true, IsGearRetractable = true, IsTailDragger = true,
                FlapsHandlePositionCount = 1, SpoilersAvailable = false,
                AutopilotAvailable = false, ThrottleLowerLimitPercent = 0
            },
            false,
            new[] { FreeFlightGateIds.Flaps, FreeFlightGateIds.Spoilers,
                FreeFlightGateIds.Automation, FreeFlightGateIds.ReverseThrust,
                FreeFlightGateIds.ContactStability, FreeFlightGateIds.NoseGearImpact,
                FreeFlightGateIds.ManualBraking },
            new[] { FreeFlightGateIds.Gear }
        ];
        yield return
        [
            new TelemetrySample
            {
                IsGearFloats = true, IsGearWheels = false, IsGearRetractable = false,
                FlapsHandlePositionCount = 1, SpoilersAvailable = false,
                AutopilotAvailable = false, ThrottleLowerLimitPercent = 0
            },
            true,
            new[] { FreeFlightGateIds.Gear, FreeFlightGateIds.Flaps,
                FreeFlightGateIds.Spoilers, FreeFlightGateIds.Automation,
                FreeFlightGateIds.ReverseThrust, FreeFlightGateIds.ContactStability,
                FreeFlightGateIds.NoseGearImpact, FreeFlightGateIds.ManualBraking },
            Array.Empty<string>()
        ];
    }

    [Fact]
    public void ExplicitlyUnsupportedGates_AreNeutralNotApplicableCriteria()
    {
        var loader = new ConfigLoader(FindConfig());
        var free = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey).Key!;
        var challenge = MinimalFreeChallenge();
        challenge.FreeFlightCapabilities = FreeFlightCapabilityResolver.Freeze(
            new TelemetrySample
            {
                IsGearWheels = true,
                IsGearRetractable = false,
                FlapsHandlePositionCount = 1,
                SpoilersAvailable = false,
                AutopilotAvailable = false,
                ThrottleLowerLimitPercent = 0
            }, false);
        challenge.RequireGearDown = false;

        var result = new ScoreEngine(free).Evaluate(
            challenge, new LandingSnapshot { StallWarningCoverageAvailable = false });

        foreach (var id in new[]
                 {
                     FreeFlightGateIds.Gear, FreeFlightGateIds.Flaps, FreeFlightGateIds.Spoilers,
                     FreeFlightGateIds.Automation, FreeFlightGateIds.ReverseThrust
                 })
        {
            var criterion = result.Criteria.Single(item => item.Id == id);
            Assert.Equal(MetricStatus.NotApplicable, criterion.Status);
            Assert.Equal(1, criterion.AppliedMultiplier);
        }
        Assert.True(result.IsRanked);
        Assert.NotNull(result.ScorePercent);
    }

    [Fact]
    public void AssumedCriteriaAndCapabilities_RoundTripHighscoreAndFlightTape()
    {
        var loader = new ConfigLoader(FindConfig());
        var free = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey).Key!;
        var challenge = MinimalFreeChallenge();
        challenge.FreeFlightCapabilities = FreeFlightCapabilityResolver.Freeze(CapableJet(), false);
        var result = new ScoreEngine(free).Evaluate(
            challenge, new LandingSnapshot { StallWarningCoverageAvailable = false });

        var root = Path.Combine(Path.GetTempPath(), "ChallengeLabTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var highscores = new HighscoreStore(Path.Combine(root, "highscores.json"));
            highscores.Add(result);
            var reloaded = new HighscoreStore(Path.Combine(root, "highscores.json")).Entries.Single();
            Assert.NotNull(reloaded.Diagnostics?.FreeFlightCapabilities);
            Assert.Contains(reloaded.Criteria, item =>
                item.Status == MetricStatus.Assumed && item.AppliedMultiplier is < 1);

            var tapes = new FlightTapeStore(Path.Combine(root, "tapes"));
            var path = tapes.Save(challenge,
                [new TelemetrySample { SimulationTimeSeconds = 0 }], result, "FreeFlight");
            var tape = tapes.Load(path);
            Assert.NotNull(tape.FreeFlightCapabilities);
            Assert.NotNull(tape.Challenge!.FreeFlightCapabilities);
            Assert.Contains(tape.OriginalCriteria, item =>
                item.Status == MetricStatus.Assumed && item.AppliedMultiplier is < 1);

            var tracePath = new LandingTraceStore(Path.Combine(root, "traces"))
                .Save(result, new LandingSnapshot());
            using var trace = JsonDocument.Parse(File.ReadAllText(tracePath));
            Assert.True(trace.RootElement.TryGetProperty("FreeFlightCapabilities", out _));
            Assert.Contains(trace.RootElement.GetProperty("Metrics").EnumerateArray(), item =>
                item.GetProperty("Status").GetString() == nameof(MetricStatus.Assumed)
                && item.TryGetProperty("AppliedMultiplier", out var multiplier)
                && multiplier.GetDouble() < 1);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HistoricalFreeV7Highscore_RemainsInItsOriginalBucket()
    {
        var root = Path.Combine(Path.GetTempPath(), "ChallengeLabTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "highscores.json");
            File.WriteAllText(path, """
                [{
                  "Id":"11111111-1111-1111-1111-111111111111",
                  "Utc":"2026-01-01T00:00:00+00:00",
                  "ChallengeId":"free-test-01",
                  "ChallengeTitle":"Historical Free",
                  "ScorePercent":82.5,
                  "Grade":"B",
                  "EvaluationKeyId":"free-flight-evaluation-key",
                  "EvaluationKeyVersion":7,
                  "ScoringProfileHash":"legacyv7hash",
                  "RankedBucketId":"free-test-01|free-flight-evaluation-key|v7|legacyv7hash",
                  "Phases":[],
                  "Criteria":[]
                }]
                """);

            var entry = new HighscoreStore(path).Entries.Single();
            Assert.Equal(7, entry.EvaluationKeyVersion);
            Assert.Equal("free-test-01|free-flight-evaluation-key|v7|legacyv7hash",
                entry.EffectiveRankedBucketId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static TelemetrySample CapableJet() => new()
    {
        IsGearWheels = true,
        IsGearRetractable = true,
        FlapsHandlePositionCount = 6,
        SpoilersAvailable = true,
        AutopilotAvailable = true,
        ThrottleLowerLimitPercent = -20
    };

    private static LandingSnapshot FullyMeasuredSnapshot()
    {
        var snapshot = new LandingSnapshot
        {
            Touchdown = new TelemetrySample
            {
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(10),
                SimulationTimeSeconds = 10,
                Latitude = 0.003,
                Longitude = 0,
                SimOnGround = true
            },
            GearDownAtTouchdown = true,
            FlapsIndexAtTouchdown = 3,
            VerticalSpeedAtTouchdownFpm = -100,
            AirspeedAtTouchdownKts = 65,
            VappKts = 70,
            TargetTouchdownIasKts = 65,
            TouchdownIasErrorKts = 0,
            PeakGForce = 1.15,
            TouchdownLateralOffsetM = 1,
            CrabAngle = new CrabAngleAnalysis(true, 0.5, 1.5, 3, 0.5, 0.5, 20, null),
            BankAtTouchdownDeg = 0.5,
            ApproachPathSampleCount = 5,
            ApproachGlideslopeMeanAbsFt = 20,
            ApproachVerticalVariationFtPerSec = 1,
            ApproachLateralWeaveIndex = 0.01,
            ApproachBankMeanAbsDeg = 1,
            ApproachLateralDistanceM = 2000,
            ApproachMetricDurationSec = 40,
            PostTouchdownAlignmentSampleCount = 3,
            PostTouchdownAlignmentMeanDeg = 1,
            RolloutPathSampleCount = 3,
            RolloutPathSegmentCount = 2,
            RolloutDistanceM = 1000,
            RolloutLateralMeanM = 2,
            RolloutWeaveIndex = 0.01,
            RolloutLateralPeakM = 3,
            InitialImpact = new ImpactAnalysis(
                true, false, 10, -100, "PLANE TOUCHDOWN NORMAL VELOCITY",
                1.15, 1.15, 12, 1, null),
            FloatAnalysis = new FloatAnalysis(true, false, 9, 0, 0, 0, 0, null),
            ContactStability = new ContactStabilityAnalysis(
                true, Array.Empty<BounceEvent>(), 0, 0, null),
            StallWarningCoverageAvailable = true
        };

        var observations = snapshot.GateObservations;
        observations.MonitoringStarted = true;
        observations.PauseCoverageAvailable = true;
        observations.SimulationRateCoverageAvailable = true;
        observations.MinimumSimulationRate = 1;
        observations.CameraStateCoverageAvailable = true;
        observations.CockpitViewExitCount = 0;
        observations.RadioHeightCoverageAvailable = true;
        observations.HeadingAltitudeAutomationCoverageAvailable = true;
        observations.FullAutomationCoverageAvailable = true;
        observations.HeadingAltitudeThresholdObserved = true;
        observations.FullAutomationThresholdObserved = true;
        observations.SpoilerTelemetryCoverageAvailable = true;
        observations.MainGearTouchdownTimeSeconds = 10;
        observations.FirstSpoilerDeploymentTimeSeconds = 11;
        observations.NoseGearContactCoverageAvailable = true;
        observations.ManualBrakeTelemetryCoverageAvailable = true;
        observations.NoseGearTouchdownTimeSeconds = 11;
        observations.FirstSimultaneousBrakingTimeSeconds = 12;
        observations.NoseGearImpact = PassingNoseImpact();
        observations.RolloutDistanceEvaluated = true;
        observations.RemainingRunwayMetersAtSettleSpeed = 1000;
        observations.RequiredRemainingRunwayMeters = 450;
        observations.RunwayLengthMeters = 3000;
        observations.GroundSpeedKtsAtRolloutCheck = 49;
        observations.ReverseThrustTelemetryCoverageAvailable = true;
        observations.OperatingEnginesCapturedAtTouchdown = true;
        observations.EngineCountAtTouchdown = 2;
        observations.OperatingEngineIndicesAtTouchdown.AddRange([1, 2]);
        observations.FirstReverseSelectionTimeSecondsByEngine[1] = 12;
        observations.FirstReverseSelectionTimeSecondsByEngine[2] = 12;
        observations.ReverseThrustStowEvaluated = true;
        observations.ReverseThrustStowCoverageAvailable = true;
        observations.ReverseThrustStowedAtThreshold = true;
        observations.GroundSpeedKtsAtReverseStowCheck = 59;
        return snapshot;
    }

    private static NoseGearImpactAnalysis PassingNoseImpact()
    {
        var impact = new NoseGearImpactEvent
        {
            ContactTimeSeconds = 11,
            MedianPreContactG = 1,
            RawPeakG = 1.1,
            RobustPeakG = 1.1,
            DeltaG = 0.1,
            ValidPostContactSamples = 8,
            Severity = NoseGearImpactSeverity.Pass,
            AppliedMultiplier = 1
        };
        return new NoseGearImpactAnalysis
        {
            CoverageSufficient = true,
            NoseGearContactCoverageAvailable = true,
            GForceCoverageAvailable = true,
            Events = { impact },
            WorstEvent = impact
        };
    }

    private static ChallengeConfig MinimalFreeChallenge() => new()
    {
        Id = "free-test-01",
        Mode = ChallengeMode.FreeFlight.ToConfigKey(),
        Title = "Free test",
        RequireGearDown = true,
        Runway = new RunwayConfig
        {
            AirportIcao = "TEST",
            RunwayId = "01",
            ThresholdLatitude = 0,
            ThresholdLongitude = 0,
            HeadingTrueDeg = 0,
            LengthM = 3000,
            WidthM = 45
        }
    };

    private static void AssertJsonEqual<T>(T expected, T actual)
    {
        var options = new JsonSerializerOptions { WriteIndented = false };
        Assert.Equal(JsonSerializer.Serialize(expected, options), JsonSerializer.Serialize(actual, options));
    }

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
