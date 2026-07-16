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
    }
}
