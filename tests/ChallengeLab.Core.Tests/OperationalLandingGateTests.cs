using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using System.Text.Json;

namespace ChallengeLab.Core.Tests;

public sealed class OperationalLandingGateTests
{
    [Fact]
    public void ShippedProfile_EnablesOperationalGatesOnlyForChallengeCareer()
    {
        var loader = new ConfigLoader(FindConfig());
        var challenge = loader.LoadEvaluationKey();
        var free = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey);

        Assert.True(challenge.IsValid, string.Join("; ", challenge.Errors));
        Assert.Equal(18, challenge.Key!.Version);
        var touchdown = challenge.Key.Phases.Single(p => p.Id == "touchdown").Penalties!;
        var approach = challenge.Key.Phases.Single(p => p.Id == "approach").Penalties!;
        var rollout = challenge.Key.Phases.Single(p => p.Id == "rollout").Penalties!;
        Assert.NotNull(touchdown.SpoilerDeployment);
        Assert.NotNull(touchdown.NoseGearImpact);
        Assert.NotNull(approach.Automation);
        Assert.NotNull(rollout.ManualBraking);
        Assert.NotNull(rollout.Rollout);
        Assert.NotNull(challenge.Key.GeneralPenalties!.PauseUsage);
        Assert.NotNull(challenge.Key.GeneralPenalties.SimulationRate);
        Assert.Equal(0.8, rollout.Rollout!.MultiplierOnFail, 6);

        Assert.True(free.IsValid, string.Join("; ", free.Errors));
        Assert.Null(free.Key!.GeneralPenalties);
        Assert.All(free.Key.Phases, phase =>
        {
            Assert.Null(phase.Penalties?.SpoilerDeployment);
            Assert.Null(phase.Penalties?.ManualBraking);
            Assert.Null(phase.Penalties?.Automation);
            Assert.Null(phase.Penalties?.NoseGearImpact);
            Assert.Null(phase.Penalties?.Rollout);
        });
    }

    [Fact]
    public void OperationalGateConfiguration_RejectsInvalidThresholdsAndMultipliers()
    {
        var (key, _) = LoadChallengeProfile();
        key.Phases.Single(p => p.Id == "touchdown").Penalties!.SpoilerDeployment!
            .MinimumSurfacePosition = 1.1;
        key.Phases.Single(p => p.Id == "rollout").Penalties!.ManualBraking!
            .DeadlineSecondsAfterNoseTouchdown = -1;
        var automation = key.Phases.Single(p => p.Id == "approach").Penalties!.Automation!;
        automation.HeadingAltitudeOffRadioHeightFeet = 500;
        automation.AllAutomationOffRadioHeightFeet = 1000;
        key.GeneralPenalties!.PauseUsage!.MultiplierOnFail = 0;
        key.GeneralPenalties.SimulationRate!.MinimumAllowedRate = 0;
        key.Phases.Single(p => p.Id == "touchdown").Penalties!.NoseGearImpact!
            .SevereDeltaG = 0.1;

        var errors = EvaluationKeyValidator.Validate(key);
        Assert.Contains(errors, error => error.Contains("minimumSurfacePosition"));
        Assert.Contains(errors, error => error.Contains("deadlineSecondsAfterNoseTouchdown"));
        Assert.Contains(errors, error => error.Contains("headingAltitudeOffRadioHeightFeet"));
        Assert.Contains(errors, error => error.Contains("pauseUsage.multiplierOnFail"));
        Assert.Contains(errors, error => error.Contains("simulationRate.minimumAllowedRate"));
        Assert.Contains(errors, error => error.Contains("noseGearImpact.severeDeltaG"));
    }

    [Fact]
    public void PhasePenaltyConfiguration_RejectsPenaltyInWrongPhase()
    {
        var (key, _) = LoadChallengeProfile();
        var approach = key.Phases.Single(p => p.Id == "approach");
        var rollout = key.Phases.Single(p => p.Id == "rollout");
        rollout.Penalties!.StallWarning = approach.Penalties!.StallWarning;

        Assert.Contains(EvaluationKeyValidator.Validate(key), error =>
            error.Contains("stallWarning must belong to phase 'approach'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoseGearImpactConfiguration_ValidatesEveryTimingThresholdAndMultiplierGroup()
    {
        var invalidCases = new (Action<NoseGearImpactGateConfig> Mutate, string Path)[]
        {
            (g => g.PreContactWindowSeconds = -0.1, "preContactWindowSeconds"),
            (g => g.PostContactWindowSeconds = 0, "postContactWindowSeconds"),
            (g => g.FilterCutoffHz = 0, "filterCutoffHz"),
            (g => g.PeakQuantile = 1.1, "peakQuantile"),
            (g => g.MinimumPostContactSamples = 0, "minimumPostContactSamples"),
            (g => g.ModerateDeltaG = -0.1, "moderateDeltaG"),
            (g => g.ModeratePeakG = -0.1, "moderatePeakG"),
            (g => g.SevereDeltaG = 0.1, "severeDeltaG"),
            (g => g.SeverePeakG = 1.0, "severePeakG"),
            (g => g.RecontactDebounceSeconds = 0, "recontactDebounceSeconds"),
            (g => g.CompressionNoiseThreshold = 1.1, "compressionNoiseThreshold"),
            (g => g.ModerateMultiplier = 0, "moderateMultiplier"),
            (g => g.SevereMultiplier = 1, "severeMultiplier")
        };

        foreach (var (mutate, path) in invalidCases)
        {
            var (key, _) = LoadChallengeProfile();
            mutate(key.Phases.Single(p => p.Id == "touchdown").Penalties!.NoseGearImpact!);
            Assert.Contains(EvaluationKeyValidator.Validate(key), error => error.Contains(path));
        }
    }

    [Theory]
    [InlineData(2000, "heading")]
    [InlineData(2000, "altitude")]
    [InlineData(1000, "master")]
    [InlineData(1000, "ap1")]
    [InlineData(1000, "ap2")]
    [InlineData(1000, "autothrust-active")]
    [InlineData(1000, "autothrust-armed")]
    public void Automation_ExactThresholdsLatchViolation(double radioHeight, string activeState)
    {
        var session = CreateSession();
        session.Arm();
        session.Ingest(AirSample(10, radioHeight, activeState));

        var observations = session.Snapshot.GateObservations;
        Assert.True(observations.MonitoringStarted);
        Assert.True(observations.AutomationViolation);
        Assert.Equal(radioHeight, observations.FirstAutomationViolationRadioHeightFeet);
    }

    [Fact]
    public void Automation_AttemptBeginningBelowThresholdIsCheckedImmediately_AndFlightDirectorsAreIgnored()
    {
        var session = CreateSession();
        session.Arm();
        session.Ingest(AirSample(10, 900));

        var observations = session.Snapshot.GateObservations;
        Assert.True(observations.HeadingAltitudeThresholdObserved);
        Assert.True(observations.FullAutomationThresholdObserved);
        Assert.False(observations.AutomationViolation);
    }

    [Fact]
    public void Automation_GoAroundMayReengageAboveLimit_ButMustBeOffAgainBelowIt()
    {
        var session = CreateSession();
        session.Arm();
        session.Ingest(AirSample(10, 2500));
        session.Ingest(AirSample(11, 1500));
        session.Ingest(AirSample(12, 2500, "heading"));
        Assert.False(session.Snapshot.GateObservations.AutomationViolation);

        session.Ingest(AirSample(13, 1999, "heading"));
        Assert.True(session.Snapshot.GateObservations.AutomationViolation);
    }

    [Fact]
    public void Automation_MultipleViolationsApplySinglePointNineFactor()
    {
        var (key, challenge) = LoadChallengeProfile();
        var clean = new ScoreEngine(key).Evaluate(challenge, PassingSnapshot());
        var failedSnapshot = PassingSnapshot();
        failedSnapshot.GateObservations.AutomationViolation = true;
        failedSnapshot.GateObservations.FirstAutomationViolation = "heading hold, AP1, autothrust active";
        failedSnapshot.GateObservations.FirstAutomationViolationRadioHeightFeet = 900;

        var failed = new ScoreEngine(key).Evaluate(challenge, failedSnapshot);
        Assert.Equal(97.5, failed.ScorePercent);
        Assert.Single(failed.Criteria, c => c.Id == "automation");
        Assert.Equal("approach", failed.Criteria.Single(c => c.Id == "automation").PhaseId);
    }

    [Fact]
    public void NoseGearImpact_UsesConfiguredGradeAndWorstEventOnlyOnce()
    {
        var (key, challenge) = LoadChallengeProfile();
        var clean = new ScoreEngine(key).Evaluate(challenge, PassingSnapshot());

        var moderateSnapshot = PassingSnapshot();
        moderateSnapshot.GateObservations.NoseGearImpact =
            ImpactAnalysis(NoseGearImpactSeverity.Moderate, 0.95, 0.3, 1.4);
        var moderate = new ScoreEngine(key).Evaluate(challenge, moderateSnapshot);

        var severeSnapshot = PassingSnapshot();
        var severeAnalysis = ImpactAnalysis(NoseGearImpactSeverity.Severe, 0.9, 0.7, 1.8);
        severeAnalysis.Events.Insert(0, new NoseGearImpactEvent
        {
            ContactTimeSeconds = 10.25,
            MedianPreContactG = 1,
            RawPeakG = 1.4,
            RobustPeakG = 1.4,
            DeltaG = 0.4,
            ValidPostContactSamples = 8,
            Severity = NoseGearImpactSeverity.Moderate,
            AppliedMultiplier = 0.95,
            CompressionFallbackUsed = true
        });
        severeSnapshot.GateObservations.NoseGearImpact = severeAnalysis;
        var severe = new ScoreEngine(key).Evaluate(challenge, severeSnapshot);

        Assert.Equal(96.5, moderate.ScorePercent);
        Assert.Equal(93.0, severe.ScorePercent);
        Assert.Single(severe.Criteria, c => c.Id == "nose_gear_impact");
        var note = severe.Criteria.Single(c => c.Id == "nose_gear_impact").Note;
        Assert.Contains("Severe", note);
        Assert.Contains("main TD+", note);
        Assert.Contains("robust peak", note);
        Assert.Contains("0.9", note);
        Assert.Contains("nose impact penalty", severe.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(12, false)]
    [InlineData(12.001, true)]
    public void Spoilers_UseInclusiveTwoSecondDeadline(double deploymentTime, bool penalized)
    {
        var (key, challenge) = LoadChallengeProfile();
        var snapshot = PassingSnapshot();
        snapshot.GateObservations.FirstSpoilerDeploymentTimeSeconds = deploymentTime;
        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);

        Assert.Equal(penalized ? MetricStatus.GateFailed : MetricStatus.Informational,
            result.Criteria.Single(c => c.Id == "spoiler_deployment").Status);
    }

    [Fact]
    public void Spoilers_OneSurfaceMissingFailsGate()
    {
        var (key, challenge) = LoadChallengeProfile();
        var snapshot = PassingSnapshot();
        snapshot.GateObservations.FirstSpoilerDeploymentTimeSeconds = null;
        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);
        Assert.Equal(MetricStatus.GateFailed,
            result.Criteria.Single(c => c.Id == "spoiler_deployment").Status);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(24, false)]
    [InlineData(24.001, true)]
    public void Brakes_UseInclusiveFourSecondDeadline(double brakeTime, bool penalized)
    {
        var (key, challenge) = LoadChallengeProfile();
        var snapshot = PassingSnapshot();
        snapshot.GateObservations.NoseGearTouchdownTimeSeconds = 20;
        snapshot.GateObservations.FirstSimultaneousBrakingTimeSeconds = brakeTime;
        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);

        Assert.Equal(penalized ? MetricStatus.GateFailed : MetricStatus.Informational,
            result.Criteria.Single(c => c.Id == "manual_braking").Status);
    }

    [Fact]
    public void Brakes_PedalInputBeforeNoseTouchdownOrDuringNoseBounceFails()
    {
        var session = CreateSession();
        session.Arm();
        session.Ingest(AirSample(10, 900));
        session.Ingest(AirSample(11, 100, brakeLeft: 0.06));
        Assert.True(session.Snapshot.GateObservations.EarlyOrAirborneBrakeViolation);

        var bounced = CreateSession();
        bounced.Arm();
        bounced.Ingest(AirSample(10, 100));
        bounced.Ingest(AirSample(11, 10, noseOnGround: true));
        bounced.Ingest(AirSample(12, 10, brakeLeft: 0.06, brakeRight: 0.06));
        Assert.True(bounced.Snapshot.GateObservations.EarlyOrAirborneBrakeViolation);
    }

    [Fact]
    public void Session_DoesNotFinalizeBeforeSpoilerAndBrakeWindowsClose()
    {
        var (key, challenge) = LoadChallengeProfile();
        var settings = key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false,
            SettledHoldSeconds = 1
        };
        var session = new LandingSession(challenge, settings);
        session.Arm();
        session.Ingest(AirSample(0, 100));
        session.Ingest(GroundSample(1, 100, spoilers: 0, brakes: 0));
        session.Ingest(GroundSample(2, 40, spoilers: 0.2, brakes: 0.1));
        session.Ingest(GroundSample(3.1, 40, spoilers: 0.2, brakes: 0.1));
        Assert.False(session.IsComplete);

        session.Ingest(GroundSample(5, 40, spoilers: 0.2, brakes: 0.1));
        Assert.True(session.IsComplete);
    }

    [Fact]
    public void Session_RecordsInitialNoseContactAndOnlyDebouncedRecontactsAfterMainTouchdown()
    {
        var (key, challenge) = LoadChallengeProfile();
        var settings = key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false
        };
        var session = new LandingSession(challenge, settings);
        session.Arm();
        session.Ingest(GearSample(0, leftMain: false, rightMain: false, nose: false));
        session.Ingest(GearSample(1, leftMain: true, rightMain: true, nose: false));
        session.Ingest(GearSample(1.1, leftMain: true, rightMain: true, nose: true));

        var observations = session.Snapshot.GateObservations;
        Assert.Equal(1, observations.MainGearTouchdownTimeSeconds);
        Assert.Equal(1.1, observations.NoseGearTouchdownTimeSeconds);
        Assert.Equal(1.1, observations.LastNoseGearImpactContactTimeSeconds);

        session.Ingest(GearSample(2, leftMain: true, rightMain: true, nose: false));
        session.Ingest(GearSample(2.04, leftMain: true, rightMain: true, nose: true));
        Assert.Equal(1.1, observations.LastNoseGearImpactContactTimeSeconds);

        session.Ingest(GearSample(3, leftMain: true, rightMain: true, nose: false));
        session.Ingest(GearSample(3.1, leftMain: true, rightMain: true, nose: true));
        Assert.Equal(3.1, observations.LastNoseGearImpactContactTimeSeconds);
    }

    [Fact]
    public void Pause_ControlledInitialHoldIsIgnored_ButLaterPauseGenerationFails()
    {
        var session = CreateSession();
        session.Arm();
        session.Ingest(AirSample(10, 1500, paused: true, pauseGeneration: 1));
        Assert.False(session.Snapshot.GateObservations.MonitoringStarted);

        session.Ingest(AirSample(11, 1500, pauseGeneration: 1));
        Assert.True(session.Snapshot.GateObservations.MonitoringStarted);
        Assert.False(session.Snapshot.GateObservations.PauseViolation);

        session.Ingest(AirSample(12, 1400, pauseGeneration: 2));
        Assert.True(session.Snapshot.GateObservations.PauseViolation);
    }

    [Fact]
    public void ActivePauseAfterMonitoringFailsImmediately()
    {
        var session = CreateSession();
        session.Arm();
        session.Ingest(AirSample(10, 1500));
        session.Ingest(AirSample(11, 1400, activePaused: true, pauseGeneration: 1));
        Assert.True(session.Snapshot.GateObservations.PauseViolation);
    }

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(0.99, false)]
    [InlineData(0.989, true)]
    [InlineData(0.5, true)]
    public void SimulationRate_UsesConfiguredTolerance(double rate, bool penalized)
    {
        var session = CreateSession();
        session.Arm();
        session.Ingest(AirSample(10, 1500, simulationRate: rate));
        Assert.Equal(penalized, session.Snapshot.GateObservations.ReducedSimulationRateViolation);
    }

    [Fact]
    public void AllOperationalFailuresStackOnce_AndOnlyFinalScoreIsRounded()
    {
        var (key, challenge) = LoadChallengeProfile();
        var snapshot = PassingSnapshot();
        var obs = snapshot.GateObservations;
        obs.FirstSpoilerDeploymentTimeSeconds = 12.1;
        obs.EarlyOrAirborneBrakeViolation = true;
        obs.AutomationViolation = true;
        obs.FirstAutomationViolation = "AP1 and autothrust active";
        obs.FirstAutomationViolationRadioHeightFeet = 900;
        obs.PauseViolation = true;
        obs.ReducedSimulationRateViolation = true;
        obs.MinimumSimulationRate = 0.5;
        obs.NoseGearImpact = ImpactAnalysis(NoseGearImpactSeverity.Severe, 0.9, 0.7, 1.8);
        obs.RolloutEndOfRunwayViolation = true;
        obs.RemainingRunwayMetersAtSettleSpeed = 200;
        obs.RequiredRemainingRunwayMeters = 525;

        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);
        var expected = Math.Round(
            (70 * 0.9 * 0.9 + 25 * 0.9 + 5 * 0.9 * 0.8)
            * 0.95 * 0.8,
            1);
        Assert.Equal(expected, result.ScorePercent);
        Assert.Equal(7, result.Criteria.Count(c => c.Status == MetricStatus.GateFailed
                                                   && c.Id is "spoiler_deployment" or "manual_braking" or "nose_gear_impact" or "automation" or "pause_usage" or "simulation_rate" or "rollout_distance"));
    }

    [Fact]
    public void GeneralPausePenalty_AppliesToCombinedScoreAndHasNoPhaseOwner()
    {
        var (key, challenge) = LoadChallengeProfile();
        var snapshot = PassingSnapshot();
        snapshot.GateObservations.PauseViolation = true;

        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);

        Assert.Equal(95.0, result.ScorePercent);
        Assert.All(result.PhaseScores, phase => Assert.Equal(100.0, phase.ScorePercent));
        Assert.Null(result.Criteria.Single(c => c.Id == "pause_usage").PhaseId);
    }

    [Fact]
    public void MissingOperationalCoverageMakesChallengeUnranked_ButFreeFlightUnaffected()
    {
        var (key, challenge) = LoadChallengeProfile();
        var missing = PassingSnapshot();
        missing.GateObservations.RadioHeightCoverageAvailable = false;
        var challengeResult = new ScoreEngine(key).Evaluate(challenge, missing);
        Assert.False(challengeResult.IsRanked);
        Assert.Contains(challengeResult.IncompleteReasons,
            reason => reason.Contains("Radio-altitude", StringComparison.OrdinalIgnoreCase));

        var loader = new ConfigLoader(FindConfig());
        var freeKey = loader.LoadEvaluationKey(loader.LoadCatalog().FreeFlightEvaluationKey).Key!;
        var freeResult = new ScoreEngine(freeKey).Evaluate(challenge, PassingSnapshot(includeOperationalCoverage: false));
        Assert.True(freeResult.IsRanked, string.Join("; ", freeResult.IncompleteReasons));
        Assert.DoesNotContain(freeResult.Criteria, c =>
            c.Id is "spoiler_deployment" or "manual_braking" or "nose_gear_impact" or "automation" or "pause_usage" or "simulation_rate" or "rollout_distance");
    }

    [Theory]
    [InlineData(2000, 400)]
    [InlineData(3500, 525)]
    [InlineData(1000, 400)]
    public void RequiredRemainingAt50Knots_UsesFloorOrFifteenPercent(double length, double expected)
        => Assert.Equal(expected, RolloutGateConfig.RequiredRemainingAt50Knots(length), 6);

    [Fact]
    public void Rollout_TooCloseAtSettleSpeedAppliesZeroPointEight()
    {
        var (key, challenge) = LoadChallengeProfile();
        var clean = new ScoreEngine(key).Evaluate(challenge, PassingSnapshot());
        var failedSnapshot = PassingSnapshot();
        failedSnapshot.GateObservations.RolloutEndOfRunwayViolation = true;
        failedSnapshot.GateObservations.RemainingRunwayMetersAtSettleSpeed = 100;
        failedSnapshot.GateObservations.RequiredRemainingRunwayMeters = 525;
        failedSnapshot.GateObservations.RunwayLengthMeters = 3500;
        failedSnapshot.GateObservations.GroundSpeedKtsAtRolloutCheck = 49;

        var failed = new ScoreEngine(key).Evaluate(challenge, failedSnapshot);
        Assert.Equal(99.0, failed.ScorePercent);
        Assert.Equal(MetricStatus.GateFailed,
            failed.Criteria.Single(c => c.Id == "rollout_distance").Status);
        Assert.Equal("rollout", failed.Criteria.Single(c => c.Id == "rollout_distance").PhaseId);
        Assert.Contains("100", failed.Criteria.Single(c => c.Id == "rollout_distance").Note);
    }

    [Fact]
    public void Rollout_LatchesOnFirstSampleBelowSettleGroundspeed()
    {
        var (key, challenge) = LoadChallengeProfile();
        var settings = key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false,
            SettledHoldSeconds = 1
        };
        var session = new LandingSession(challenge, settings);
        session.Arm();

        // Approach then touchdown near threshold (plenty of runway remaining).
        session.Ingest(PositionedSample(0, airborne: true, groundSpeed: 140, alongRunwayMeters: -100));
        session.Ingest(PositionedSample(1, airborne: false, groundSpeed: 120, alongRunwayMeters: 200));
        Assert.False(session.Snapshot.GateObservations.RolloutDistanceEvaluated);

        // Still above settle threshold — no evaluation yet.
        session.Ingest(PositionedSample(2, airborne: false, groundSpeed: 55, alongRunwayMeters: 2800));
        Assert.False(session.Snapshot.GateObservations.RolloutDistanceEvaluated);

        // First frame below settle GS near the far end → violation.
        session.Ingest(PositionedSample(3, airborne: false, groundSpeed: 49, alongRunwayMeters: 3200));
        var obs = session.Snapshot.GateObservations;
        Assert.True(obs.RolloutDistanceEvaluated);
        Assert.True(obs.RolloutEndOfRunwayViolation);
        Assert.Equal(49, obs.GroundSpeedKtsAtRolloutCheck);
        Assert.Equal(3500, obs.RunwayLengthMeters);
        Assert.Equal(525, obs.RequiredRemainingRunwayMeters);
        Assert.InRange(obs.RemainingRunwayMetersAtSettleSpeed!.Value, 295, 305);

        // Later samples must not overwrite the latched evaluation.
        session.Ingest(PositionedSample(4, airborne: false, groundSpeed: 40, alongRunwayMeters: 500));
        Assert.True(session.Snapshot.GateObservations.RolloutEndOfRunwayViolation);
        Assert.InRange(session.Snapshot.GateObservations.RemainingRunwayMetersAtSettleSpeed!.Value, 295, 305);
    }

    [Fact]
    public void Rollout_PassesWhenEnoughRunwayRemainsAtSettleSpeed()
    {
        var (key, challenge) = LoadChallengeProfile();
        var settings = key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false
        };
        var session = new LandingSession(challenge, settings);
        session.Arm();
        session.Ingest(PositionedSample(0, airborne: true, groundSpeed: 140, alongRunwayMeters: -100));
        session.Ingest(PositionedSample(1, airborne: false, groundSpeed: 120, alongRunwayMeters: 400));
        session.Ingest(PositionedSample(2, airborne: false, groundSpeed: 49, alongRunwayMeters: 1000));

        var obs = session.Snapshot.GateObservations;
        Assert.True(obs.RolloutDistanceEvaluated);
        Assert.False(obs.RolloutEndOfRunwayViolation);
        Assert.InRange(obs.RemainingRunwayMetersAtSettleSpeed!.Value, 2490, 2510);
    }

    [Theory]
    [InlineData("Nose-gear contact mapping is unavailable.")]
    [InlineData("Aircraft-G telemetry is unavailable around nose contact.")]
    public void MissingNoseImpactCoverageMakesChallengeUnranked(string reason)
    {
        var (key, challenge) = LoadChallengeProfile();
        var snapshot = PassingSnapshot();
        snapshot.GateObservations.NoseGearImpact = new NoseGearImpactAnalysis
        {
            CoverageSufficient = false,
            DegradedReason = reason
        };

        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);

        Assert.False(result.IsRanked);
        Assert.Null(result.ScorePercent);
        Assert.Contains(result.IncompleteReasons, item => item.Contains(reason));
        Assert.Equal(MetricStatus.Unavailable,
            result.Criteria.Single(c => c.Id == "nose_gear_impact").Status);
    }

    [Fact]
    public void HighscoreRoundTripPersistsOperationalCriteriaAndObservations()
    {
        var (key, challenge) = LoadChallengeProfile();
        var snapshot = PassingSnapshot();
        snapshot.GateObservations.AutomationViolation = true;
        snapshot.GateObservations.FirstAutomationViolation = "autothrust active";
        snapshot.GateObservations.FirstAutomationViolationRadioHeightFeet = 950;
        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);
        var directory = Path.Combine(Path.GetTempPath(), "ChallengeLabGateTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "highscores.json");
        try
        {
            var store = new HighscoreStore(path);
            store.Add(result);
            var reloaded = new HighscoreStore(path).Entries.Single();
            Assert.Contains(reloaded.Criteria, c => c.Id == "automation" && c.Status == MetricStatus.GateFailed);
            Assert.NotNull(reloaded.Diagnostics);
            Assert.True(reloaded.Diagnostics!.OperationalGates.AutomationViolation);
            Assert.Equal(950, reloaded.Diagnostics.OperationalGates.FirstAutomationViolationRadioHeightFeet);
            Assert.NotNull(reloaded.Diagnostics.OperationalGates.NoseGearImpact?.WorstEvent);
            Assert.Contains(reloaded.Criteria, c => c.Id == "nose_gear_impact");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void LandingTracePersistsOperationalObservationsAndGateCriteria()
    {
        var (key, challenge) = LoadChallengeProfile();
        var snapshot = PassingSnapshot();
        snapshot.GateObservations.PauseViolation = true;
        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);
        var directory = Path.Combine(Path.GetTempPath(), "ChallengeLabGateTraces", Guid.NewGuid().ToString("N"));
        try
        {
            var path = new LandingTraceStore(directory).Save(result, snapshot);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.True(root.GetProperty("Snapshot").GetProperty("OperationalGates")
                .GetProperty("PauseViolation").GetBoolean());
            Assert.Equal(0.1, root.GetProperty("Snapshot").GetProperty("OperationalGates")
                .GetProperty("NoseGearImpact").GetProperty("WorstEvent")
                .GetProperty("DeltaG").GetDouble(), 3);
            Assert.Contains(root.GetProperty("Metrics").EnumerateArray(), metric =>
                metric.GetProperty("Id").GetString() == "pause_usage"
                && metric.GetProperty("Status").GetString() == nameof(MetricStatus.GateFailed));
            Assert.Contains(root.GetProperty("Metrics").EnumerateArray(), metric =>
                metric.GetProperty("Id").GetString() == "nose_gear_impact");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private static LandingSession CreateSession()
    {
        var (key, challenge) = LoadChallengeProfile();
        return new LandingSession(challenge, key.ToSessionSettings());
    }

    private static TelemetrySample AirSample(
        double time,
        double radioHeight,
        string? activeAutomation = null,
        double brakeLeft = 0,
        double brakeRight = 0,
        bool noseOnGround = false,
        bool paused = false,
        bool activePaused = false,
        long pauseGeneration = 0,
        double simulationRate = 1) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(time),
        SimulationTimeSeconds = time,
        Latitude = 41.3,
        Longitude = 2.1,
        AglFeet = radioHeight,
        RadioHeightFeet = radioHeight,
        RadioHeightAvailable = true,
        AirspeedKts = 140,
        GroundSpeedKts = 140,
        VerticalSpeedFpm = -500,
        GForce = 1,
        IsGearWheels = true,
        GearOnGroundByIndex = new Dictionary<int, bool>
        {
            [0] = noseOnGround,
            [1] = false,
            [2] = false
        },
        ManualBrakeLeftPosition = brakeLeft,
        ManualBrakeRightPosition = brakeRight,
        SpoilersLeftPosition = 0,
        SpoilersRightPosition = 0,
        AutopilotHeadingHoldActive = activeAutomation == "heading",
        AutopilotAltitudeHoldActive = activeAutomation == "altitude",
        AutopilotMasterActive = activeAutomation == "master",
        AutopilotChannel1Active = activeAutomation == "ap1",
        AutopilotChannel2Active = activeAutomation == "ap2",
        AutothrustActive = activeAutomation == "autothrust-active",
        AutothrustArmed = activeAutomation == "autothrust-armed",
        PauseStateAvailable = true,
        NormalPauseActive = paused,
        ActivePauseActive = activePaused,
        PauseGeneration = pauseGeneration,
        SimulationRate = simulationRate
    };

    private static TelemetrySample GroundSample(
        double time,
        double groundSpeed,
        double spoilers,
        double brakes) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(time),
        SimulationTimeSeconds = time,
        Latitude = 41.3,
        Longitude = 2.1,
        AglFeet = 0,
        RadioHeightFeet = 0,
        RadioHeightAvailable = true,
        AirspeedKts = Math.Max(40, groundSpeed),
        GroundSpeedKts = groundSpeed,
        VerticalSpeedFpm = -100,
        TouchdownNormalVelocityFps = 100.0 / 60.0,
        GForce = 1.2,
        SimOnGround = true,
        IsGearWheels = true,
        GearHandlePosition = 1,
        FlapsHandleIndex = 3,
        GearOnGroundByIndex = new Dictionary<int, bool>
        {
            [0] = true,
            [1] = true,
            [2] = true
        },
        ManualBrakeLeftPosition = brakes,
        ManualBrakeRightPosition = brakes,
        SpoilersLeftPosition = spoilers,
        SpoilersRightPosition = spoilers,
        AutopilotHeadingHoldActive = false,
        AutopilotAltitudeHoldActive = false,
        AutopilotMasterActive = false,
        AutopilotChannel1Active = false,
        AutopilotChannel2Active = false,
        AutothrustActive = false,
        AutothrustArmed = false,
        PauseStateAvailable = true,
        PauseGeneration = 0,
        SimulationRate = 1
    };

    private static TelemetrySample GearSample(
        double time,
        bool leftMain,
        bool rightMain,
        bool nose) => new()
    {
        Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(time),
        SimulationTimeSeconds = time,
        Latitude = 41.3,
        Longitude = 2.1,
        AglFeet = leftMain || rightMain ? 0 : 100,
        RadioHeightFeet = leftMain || rightMain ? 0 : 100,
        RadioHeightAvailable = true,
        AirspeedKts = 120,
        GroundSpeedKts = 100,
        VerticalSpeedFpm = 0,
        GForce = 1,
        GForceAvailable = true,
        SimOnGround = leftMain || rightMain,
        IsGearWheels = true,
        GearHandlePosition = 1,
        FlapsHandleIndex = 3,
        GearOnGroundByIndex = new Dictionary<int, bool>
        {
            [0] = nose,
            [1] = leftMain,
            [2] = rightMain
        },
        ManualBrakeLeftPosition = 0,
        ManualBrakeRightPosition = 0,
        SpoilersLeftPosition = 0,
        SpoilersRightPosition = 0,
        AutopilotHeadingHoldActive = false,
        AutopilotAltitudeHoldActive = false,
        AutopilotMasterActive = false,
        AutopilotChannel1Active = false,
        AutopilotChannel2Active = false,
        AutothrustActive = false,
        AutothrustArmed = false,
        PauseStateAvailable = true,
        SimulationRate = 1
    };

    private static LandingSnapshot PassingSnapshot(bool includeOperationalCoverage = true)
    {
        var snapshot = new LandingSnapshot
        {
            Touchdown = new TelemetrySample
            {
                Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(10),
                SimulationTimeSeconds = 10,
                SimOnGround = true
            },
            GearDownAtTouchdown = true,
            FlapsIndexAtTouchdown = 3,
            VerticalSpeedAtTouchdownFpm = -100,
            AirspeedAtTouchdownKts = 138,
            VappKts = 143,
            TargetTouchdownIasKts = 138,
            TouchdownIasErrorKts = 0,
            PeakGForce = 1.1,
            TouchdownLateralOffsetM = 1,
            ApproachPathSampleCount = 3,
            ApproachGlideslopeMeanAbsFt = 20,
            ApproachVerticalVariationFtPerSec = 1.5,
            ApproachLateralWeaveIndex = 0.01,
            ApproachLateralDistanceM = 2000,
            ApproachMetricDurationSec = 45,
            GroundTrackSampleCount = 4,
            GroundTrackBeforeSegmentCount = 2,
            GroundTrackAfterSegmentCount = 2,
            GroundTrackErrorMeanDeg = 1,
            GroundTrackErrorRmsDeg = 1,
            GroundTrackErrorPeakDeg = 2,
            PostTouchdownAlignmentSampleCount = 2,
            PostTouchdownAlignmentMeanDeg = 1,
            RolloutPathSampleCount = 3,
            RolloutPathSegmentCount = 2,
            RolloutDistanceM = 10,
            RolloutLateralMeanM = 1,
            RolloutLateralPeakM = 2,
            RolloutWeaveIndex = .01,
            InitialImpact = new ImpactAnalysis(
                true, false, 10, -100, "PLANE TOUCHDOWN NORMAL VELOCITY",
                1.2, 1.2, 10, 1.0, null),
            FloatAnalysis = new FloatAnalysis(true, false, 0, 0, 0, 0, 0, null),
            ContactStability = new ContactStabilityAnalysis(
                true, Array.Empty<BounceEvent>(), 0, 0, null),
            TouchdownAnalysisComplete = true
        };

        if (!includeOperationalCoverage)
            return snapshot;

        var obs = snapshot.GateObservations;
        obs.MonitoringStarted = true;
        obs.MonitoringStartTimeSeconds = 0;
        obs.MonitoringStartPauseGeneration = 0;
        obs.PauseCoverageAvailable = true;
        obs.SimulationRateCoverageAvailable = true;
        obs.MinimumSimulationRate = 1;
        obs.RadioHeightCoverageAvailable = true;
        obs.HeadingAltitudeAutomationCoverageAvailable = true;
        obs.FullAutomationCoverageAvailable = true;
        obs.HeadingAltitudeThresholdObserved = true;
        obs.FullAutomationThresholdObserved = true;
        obs.SpoilerTelemetryCoverageAvailable = true;
        obs.MainGearTouchdownTimeSeconds = 10;
        obs.FirstSpoilerDeploymentTimeSeconds = 11;
        obs.NoseGearContactCoverageAvailable = true;
        obs.ManualBrakeTelemetryCoverageAvailable = true;
        obs.NoseGearTouchdownTimeSeconds = 10.5;
        obs.FirstSimultaneousBrakingTimeSeconds = 11;
        obs.NoseGearImpact = ImpactAnalysis(NoseGearImpactSeverity.Pass, 1, 0.1, 1.1);
        obs.RolloutDistanceEvaluated = true;
        obs.GroundSpeedKtsAtRolloutCheck = 45;
        obs.RunwayLengthMeters = 3500;
        obs.RemainingRunwayMetersAtSettleSpeed = 2000;
        obs.RequiredRemainingRunwayMeters = 525;
        obs.RolloutEndOfRunwayViolation = false;
        return snapshot;
    }

    /// <summary>
    /// Places a sample along the Barcelona LFML 31R centerline at the given
    /// distance past the threshold (negative = still on short final).
    /// </summary>
    private static TelemetrySample PositionedSample(
        double time,
        bool airborne,
        double groundSpeed,
        double alongRunwayMeters)
    {
        // Barcelona challenge runway: threshold 43.433483 / 5.219637, heading 313°.
        const double thresholdLat = 43.433483;
        const double thresholdLon = 5.219637;
        const double headingDeg = 313.0;
        const double earthRadius = 6_371_000.0;
        var headingRad = headingDeg * Math.PI / 180.0;
        var north = alongRunwayMeters * Math.Cos(headingRad);
        var east = alongRunwayMeters * Math.Sin(headingRad);
        var lat = thresholdLat + north / earthRadius * 180.0 / Math.PI;
        var lon = thresholdLon + east / (earthRadius * Math.Cos(thresholdLat * Math.PI / 180.0)) * 180.0 / Math.PI;
        var onGround = !airborne;

        return new TelemetrySample
        {
            Timestamp = DateTimeOffset.UnixEpoch.AddSeconds(time),
            SimulationTimeSeconds = time,
            Latitude = lat,
            Longitude = lon,
            AglFeet = airborne ? 100 : 0,
            RadioHeightFeet = airborne ? 100 : 0,
            RadioHeightAvailable = true,
            AirspeedKts = Math.Max(40, groundSpeed),
            GroundSpeedKts = groundSpeed,
            VerticalSpeedFpm = airborne ? -500 : 0,
            TouchdownNormalVelocityFps = onGround ? 100.0 / 60.0 : null,
            GForce = onGround ? 1.2 : 1.0,
            SimOnGround = onGround,
            IsGearWheels = true,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            GearOnGroundByIndex = new Dictionary<int, bool>
            {
                [0] = onGround,
                [1] = onGround,
                [2] = onGround
            },
            ManualBrakeLeftPosition = onGround ? 0.1 : 0,
            ManualBrakeRightPosition = onGround ? 0.1 : 0,
            SpoilersLeftPosition = onGround ? 0.2 : 0,
            SpoilersRightPosition = onGround ? 0.2 : 0,
            AutopilotHeadingHoldActive = false,
            AutopilotAltitudeHoldActive = false,
            AutopilotMasterActive = false,
            AutopilotChannel1Active = false,
            AutopilotChannel2Active = false,
            AutothrustActive = false,
            AutothrustArmed = false,
            PauseStateAvailable = true,
            PauseGeneration = 0,
            SimulationRate = 1
        };
    }

    private static NoseGearImpactAnalysis ImpactAnalysis(
        NoseGearImpactSeverity severity,
        double multiplier,
        double deltaG,
        double peakG)
    {
        var impact = new NoseGearImpactEvent
        {
            ContactTimeSeconds = 10.5,
            MedianPreContactG = peakG - deltaG,
            RawPeakG = peakG,
            RobustPeakG = peakG,
            DeltaG = deltaG,
            ValidPostContactSamples = 8,
            CompressionFallbackUsed = true,
            Severity = severity,
            AppliedMultiplier = multiplier
        };
        return new NoseGearImpactAnalysis
        {
            CoverageSufficient = true,
            NoseGearContactCoverageAvailable = true,
            GForceCoverageAvailable = true,
            CompressionFallbackUsed = true,
            Events = { impact },
            WorstEvent = impact
        };
    }

    private static (LandingEvaluationKey Key, ChallengeConfig Challenge) LoadChallengeProfile()
    {
        var loader = new ConfigLoader(FindConfig());
        var loaded = loader.LoadEvaluationKey();
        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        return (loaded.Key!, loader.LoadChallenge("challenges/barcelona-crosswind-final.json"));
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
