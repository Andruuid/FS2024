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
        Assert.Equal(15, challenge.Key!.Version);
        Assert.NotNull(challenge.Key.Gates!.SpoilerDeployment);
        Assert.NotNull(challenge.Key.Gates.ManualBraking);
        Assert.NotNull(challenge.Key.Gates.Automation);
        Assert.NotNull(challenge.Key.Gates.PauseUsage);
        Assert.NotNull(challenge.Key.Gates.SimulationRate);

        Assert.True(free.IsValid, string.Join("; ", free.Errors));
        Assert.Null(free.Key!.Gates!.SpoilerDeployment);
        Assert.Null(free.Key.Gates.ManualBraking);
        Assert.Null(free.Key.Gates.Automation);
        Assert.Null(free.Key.Gates.PauseUsage);
        Assert.Null(free.Key.Gates.SimulationRate);
    }

    [Fact]
    public void OperationalGateConfiguration_RejectsInvalidThresholdsAndMultipliers()
    {
        var (key, _) = LoadChallengeProfile();
        key.Gates!.SpoilerDeployment!.MinimumSurfacePosition = 1.1;
        key.Gates.ManualBraking!.DeadlineSecondsAfterNoseTouchdown = -1;
        key.Gates.Automation!.HeadingAltitudeOffRadioHeightFeet = 500;
        key.Gates.Automation.AllAutomationOffRadioHeightFeet = 1000;
        key.Gates.PauseUsage!.MultiplierOnFail = 0;
        key.Gates.SimulationRate!.MinimumAllowedRate = 0;

        var errors = EvaluationKeyValidator.Validate(key);
        Assert.Contains(errors, error => error.Contains("minimumSurfacePosition"));
        Assert.Contains(errors, error => error.Contains("deadlineSecondsAfterNoseTouchdown"));
        Assert.Contains(errors, error => error.Contains("headingAltitudeOffRadioHeightFeet"));
        Assert.Contains(errors, error => error.Contains("pauseUsage.multiplierOnFail"));
        Assert.Contains(errors, error => error.Contains("simulationRate.minimumAllowedRate"));
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
        Assert.Equal(Math.Round(clean.ScorePercent!.Value * 0.9, 1), failed.ScorePercent);
        Assert.Single(failed.Criteria, c => c.Id == "automation");
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

        var result = new ScoreEngine(key).Evaluate(challenge, snapshot);
        var expected = Math.Round(result.ScoreBeforeGatesPercent!.Value
                                  * 0.9 * 0.9 * 0.9 * 0.95 * 0.8, 1);
        Assert.Equal(expected, result.ScorePercent);
        Assert.Equal(5, result.Criteria.Count(c => c.Status == MetricStatus.GateFailed
                                                   && c.Id is "spoiler_deployment" or "manual_braking" or "automation" or "pause_usage" or "simulation_rate"));
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
            c.Id is "spoiler_deployment" or "manual_braking" or "automation" or "pause_usage" or "simulation_rate");
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
            Assert.Contains(root.GetProperty("Metrics").EnumerateArray(), metric =>
                metric.GetProperty("Id").GetString() == "pause_usage"
                && metric.GetProperty("Status").GetString() == nameof(MetricStatus.GateFailed));
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
        return snapshot;
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
