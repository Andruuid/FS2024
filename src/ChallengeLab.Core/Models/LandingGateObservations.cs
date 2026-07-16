namespace ChallengeLab.Core.Models;

/// <summary>
/// Latched observations for penalty-only operational landing gates. These values are
/// persisted with traces and converted to report criteria by <c>ScoreEngine</c>.
/// </summary>
public sealed class LandingGateObservations
{
    public bool MonitoringStarted { get; set; }
    public double? MonitoringStartTimeSeconds { get; set; }
    public long? MonitoringStartPauseGeneration { get; set; }

    public bool PauseCoverageAvailable { get; set; }
    public bool PauseViolation { get; set; }

    public bool SimulationRateCoverageAvailable { get; set; }
    public bool ReducedSimulationRateViolation { get; set; }
    public double? MinimumSimulationRate { get; set; }

    /// <summary>
    /// True once at least one finite <c>CAMERA STATE</c> sample was observed while monitoring.
    /// </summary>
    public bool CameraStateCoverageAvailable { get; set; }

    /// <summary>
    /// Count of cockpit → non-cockpit transitions before accepted main-gear touchdown.
    /// Each exit multiplies the combined score by the configured per-switch factor.
    /// </summary>
    public int CockpitViewExitCount { get; set; }

    /// <summary>Most recent finite camera-state enum observed while monitoring.</summary>
    public int? LastCameraState { get; set; }

    public bool RadioHeightCoverageAvailable { get; set; }
    public bool HeadingAltitudeAutomationCoverageAvailable { get; set; }
    public bool FullAutomationCoverageAvailable { get; set; }
    public bool HeadingAltitudeThresholdObserved { get; set; }
    public bool FullAutomationThresholdObserved { get; set; }
    public bool AutomationViolation { get; set; }
    public string? FirstAutomationViolation { get; set; }
    public double? FirstAutomationViolationRadioHeightFeet { get; set; }

    public bool SpoilerTelemetryCoverageAvailable { get; set; }
    public double? FirstSpoilerDeploymentTimeSeconds { get; set; }

    public bool NoseGearContactCoverageAvailable { get; set; }
    public bool ManualBrakeTelemetryCoverageAvailable { get; set; }
    public double? MainGearTouchdownTimeSeconds { get; set; }
    public double? NoseGearTouchdownTimeSeconds { get; set; }
    public bool EarlyOrAirborneBrakeViolation { get; set; }
    public double? FirstSimultaneousBrakingTimeSeconds { get; set; }
    public double? LastNoseGearImpactContactTimeSeconds { get; set; }
    public NoseGearImpactAnalysis? NoseGearImpact { get; set; }

    /// <summary>
    /// True after groundspeed first falls below the settle threshold on the ground
    /// and remaining runway was evaluated against the rollout gate.
    /// </summary>
    public bool RolloutDistanceEvaluated { get; set; }
    public double? GroundSpeedKtsAtRolloutCheck { get; set; }
    public double? RemainingRunwayMetersAtSettleSpeed { get; set; }
    public double? RequiredRemainingRunwayMeters { get; set; }
    public double? RunwayLengthMeters { get; set; }
    public bool RolloutEndOfRunwayViolation { get; set; }

    public bool ReverseThrustTelemetryCoverageAvailable { get; set; }
    public bool OperatingEnginesCapturedAtTouchdown { get; set; }
    public int? EngineCountAtTouchdown { get; set; }
    public List<int> OperatingEngineIndicesAtTouchdown { get; set; } = new();
    public Dictionary<int, double> FirstReverseSelectionTimeSecondsByEngine { get; set; } = new();
    public bool AirborneReverseViolation { get; set; }
    public double? FirstAirborneReverseTimeSeconds { get; set; }
    public bool PoweredReverseViolation { get; set; }
    public double? FirstPoweredReverseTimeSeconds { get; set; }
    public double? FirstPoweredReverseThrottlePercent { get; set; }
    public bool ReverseThrustStowEvaluated { get; set; }
    public bool ReverseThrustStowCoverageAvailable { get; set; }
    public double? GroundSpeedKtsAtReverseStowCheck { get; set; }
    public bool ReverseThrustStowedAtThreshold { get; set; }
    public List<int> EnginesNotStowedAtThreshold { get; set; } = new();
    public bool ReverseApplicationWaivedByLowSpeed { get; set; }

    public void Reset()
    {
        MonitoringStarted = false;
        MonitoringStartTimeSeconds = null;
        MonitoringStartPauseGeneration = null;
        PauseCoverageAvailable = false;
        PauseViolation = false;
        SimulationRateCoverageAvailable = false;
        ReducedSimulationRateViolation = false;
        MinimumSimulationRate = null;
        CameraStateCoverageAvailable = false;
        CockpitViewExitCount = 0;
        LastCameraState = null;
        RadioHeightCoverageAvailable = false;
        HeadingAltitudeAutomationCoverageAvailable = false;
        FullAutomationCoverageAvailable = false;
        HeadingAltitudeThresholdObserved = false;
        FullAutomationThresholdObserved = false;
        AutomationViolation = false;
        FirstAutomationViolation = null;
        FirstAutomationViolationRadioHeightFeet = null;
        SpoilerTelemetryCoverageAvailable = false;
        FirstSpoilerDeploymentTimeSeconds = null;
        NoseGearContactCoverageAvailable = false;
        ManualBrakeTelemetryCoverageAvailable = false;
        MainGearTouchdownTimeSeconds = null;
        NoseGearTouchdownTimeSeconds = null;
        EarlyOrAirborneBrakeViolation = false;
        FirstSimultaneousBrakingTimeSeconds = null;
        LastNoseGearImpactContactTimeSeconds = null;
        NoseGearImpact = null;
        RolloutDistanceEvaluated = false;
        GroundSpeedKtsAtRolloutCheck = null;
        RemainingRunwayMetersAtSettleSpeed = null;
        RequiredRemainingRunwayMeters = null;
        RunwayLengthMeters = null;
        RolloutEndOfRunwayViolation = false;
        ReverseThrustTelemetryCoverageAvailable = false;
        OperatingEnginesCapturedAtTouchdown = false;
        EngineCountAtTouchdown = null;
        OperatingEngineIndicesAtTouchdown.Clear();
        FirstReverseSelectionTimeSecondsByEngine.Clear();
        AirborneReverseViolation = false;
        FirstAirborneReverseTimeSeconds = null;
        PoweredReverseViolation = false;
        FirstPoweredReverseTimeSeconds = null;
        FirstPoweredReverseThrottlePercent = null;
        ReverseThrustStowEvaluated = false;
        ReverseThrustStowCoverageAvailable = false;
        GroundSpeedKtsAtReverseStowCheck = null;
        ReverseThrustStowedAtThreshold = false;
        EnginesNotStowedAtThreshold.Clear();
        ReverseApplicationWaivedByLowSpeed = false;
    }
}
