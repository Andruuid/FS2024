namespace ChallengeLab.Core.Config;

/// <summary>Phase-weighted landing evaluation key (Approach / Touchdown / Rollout).
/// Loaded from config JSON at app startup — edit the file to finetune without code changes.</summary>
public sealed class LandingEvaluationKey
{
    public string Id { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Description { get; set; } = "";
    public string Formula { get; set; } = "";
    public EvaluationSettle? Settle { get; set; }
    public EvaluationTiming? Timing { get; set; }
    public EvaluationSpeedTarget? SpeedTarget { get; set; }
    public List<EvaluationPhase> Phases { get; set; } = new();
    public GeneralPenaltyConfig? GeneralPenalties { get; set; }
    public LandingContactMapping ContactMapping { get; set; } = new();
    public FreeModeScoringPolicy? FreeMode { get; set; }

    public LandingSessionSettings ToSessionSettings()
    {
        var flare = Phases.SelectMany(p => p.Metrics)
            .FirstOrDefault(m => m.Id.Equals("flare_efficiency", StringComparison.OrdinalIgnoreCase));
        var approachPathMaxDistNm = FreeMode?.EvaluationStart?.ApproachPathMaxDistNm
                                    ?? Timing!.ApproachPathMaxDistNm;
        return new LandingSessionSettings(
        Settle!.GroundSpeedKts,
        Settle.HoldSeconds,
        Timing!.PostTouchdownAlignmentDelaySeconds,
        Timing.FlareAglFeet,
        Timing.PostArmIgnoreSeconds,
        Timing.RequireAirborneBeforeTouchdown,
        Timing.MinAirborneAglFeet,
        Timing.MinAirborneSamples,
        Timing.ApproachPathMinDistNm,
        approachPathMaxDistNm,
        Timing.ImpactPreWindowSeconds,
        Timing.ImpactWindowSeconds,
        Timing.ImpactFilterCutoffHz,
        Timing.ImpactPeakQuantile,
        Timing.MinImpactSamples,
        Timing.BounceMinAirborneSeconds,
        Timing.BounceWindowSeconds,
        SpeedTarget!.DefaultVappKts,
        SpeedTarget.TouchdownOffsetKts,
        SpeedTarget.Vs0Factor,
        ContactMapping,
        flare?.Params.GetValueOrDefault("entryVerticalSpeedFpm", -80) ?? -80,
        flare?.Params.GetValueOrDefault("minSustainSeconds", 0.15) ?? 0.15)
        {
            OperationalGates = new OperationalGateSessionSettings(
                GeneralPenalties?.SpoilerDeployment,
                GeneralPenalties?.ManualBraking,
                GeneralPenalties?.Automation,
                GeneralPenalties?.PauseUsage,
                GeneralPenalties?.SimulationRate,
                GeneralPenalties?.CockpitView,
                GeneralPenalties?.NoseGearImpact,
                GeneralPenalties?.Rollout,
                GeneralPenalties?.ReverseThrust)
        };
    }
}

/// <summary>
/// Free Flight keeps the authoritative landing key's structure and changes only how
/// unavailable data and aircraft-specific applicability are handled.
/// </summary>
public sealed class FreeModeScoringPolicy
{
    public double UnavailableMetricScorePercent { get; set; } = 50;
    public double MissingGatePenaltyFraction { get; set; } = 0.5;
    public FreeFlightEvaluationStartPolicy? EvaluationStart { get; set; }

    public double MissingGateMultiplier(double configuredFailureMultiplier) =>
        1 - MissingGatePenaltyFraction * (1 - configuredFailureMultiplier);
}

/// <summary>
/// Free Flight begins measuring one continuous landing attempt shortly before the
/// nominal glideslope reaches a configured height above the runway.
/// </summary>
public sealed class FreeFlightEvaluationStartPolicy
{
    public double HeightAboveRunwayFeet { get; set; } = 2_000;
    public double LeadSeconds { get; set; } = 5;

    /// <summary>
    /// Upper analysis bound for Free Flight. The session is created at the dynamic
    /// start point, so this only prevents those post-start samples from being filtered.
    /// </summary>
    public double ApproachPathMaxDistNm { get; set; } = 15;
}

public sealed record LandingSessionSettings(
    double SettledGroundSpeedKts,
    double SettledHoldSeconds,
    double PostTouchdownAlignmentDelaySeconds,
    double FlareAglFeet,
    double PostArmIgnoreSeconds,
    bool RequireAirborneBeforeTouchdown,
    double MinAirborneAglFeet,
    int MinAirborneSamples,
    double ApproachPathMinDistNm,
    double ApproachPathMaxDistNm,
    double ImpactPreWindowSeconds,
    double ImpactWindowSeconds,
    double ImpactFilterCutoffHz,
    double ImpactPeakQuantile,
    int MinImpactSamples,
    double BounceMinAirborneSeconds,
    double BounceWindowSeconds,
    double DefaultVappKts,
    double TouchdownOffsetKts,
    double Vs0Factor,
    LandingContactMapping ContactMapping,
    double FlareEntryVerticalSpeedFpm,
    double FlareMinSustainSeconds)
{
    public OperationalGateSessionSettings OperationalGates { get; init; } = new();

    /// <summary>
    /// Optional TITLE→VAPP table. When null, <see cref="AircraftVappCatalog.Default"/> is used.
    /// </summary>
    public AircraftVappCatalog? AircraftVappCatalog { get; init; }

    /// <summary>Compatibility constructor for existing callers that use the pre-v9 settings shape.</summary>
    public LandingSessionSettings(
        double SettledGroundSpeedKts,
        double SettledHoldSeconds,
        double PostTouchdownAlignmentDelaySeconds,
        double FlareAglFeet,
        double PostArmIgnoreSeconds,
        bool RequireAirborneBeforeTouchdown,
        double MinAirborneAglFeet,
        int MinAirborneSamples,
        double ApproachPathMinDistNm,
        double ApproachPathMaxDistNm,
        double DefaultVappKts,
        double TouchdownOffsetKts,
        double Vs0Factor)
        : this(
            SettledGroundSpeedKts, SettledHoldSeconds,
            PostTouchdownAlignmentDelaySeconds, FlareAglFeet, PostArmIgnoreSeconds,
            RequireAirborneBeforeTouchdown, MinAirborneAglFeet, MinAirborneSamples,
            ApproachPathMinDistNm, ApproachPathMaxDistNm,
            0.25, 0.75, 10, 0.99, 8, 0.08, 3,
            DefaultVappKts, TouchdownOffsetKts, Vs0Factor,
            new LandingContactMapping(), -80, 0.15)
    {
    }
}

public sealed record OperationalGateSessionSettings(
    SpoilerDeploymentGateConfig? SpoilerDeployment = null,
    ManualBrakingGateConfig? ManualBraking = null,
    AutomationGateConfig? Automation = null,
    PauseUsageGateConfig? PauseUsage = null,
    SimulationRateGateConfig? SimulationRate = null,
    CockpitViewGateConfig? CockpitView = null,
    NoseGearImpactGateConfig? NoseGearImpact = null,
    RolloutGateConfig? Rollout = null,
    ReverseThrustGateConfig? ReverseThrust = null)
{
    public bool Enabled => SpoilerDeployment is not null
                           || ManualBraking is not null
                           || Automation is not null
                           || PauseUsage is not null
                           || SimulationRate is not null
                           || CockpitView is not null
                           || NoseGearImpact is not null
                           || Rollout is not null
                           || ReverseThrust is not null;
}

public sealed class EvaluationSettle
{
    public double GroundSpeedKts { get; set; } = 50;
    public double HoldSeconds { get; set; } = 1.0;
}

public sealed class EvaluationTiming
{
    public double PostTouchdownAlignmentDelaySeconds { get; set; }
    public double FlareAglFeet { get; set; } = 50;

    /// <summary>Seconds after Arm() during which touchdown cannot be captured (seeds ground state).</summary>
    public double PostArmIgnoreSeconds { get; set; } = 4;

    /// <summary>Require airborne samples after arm before a ground edge counts as touchdown.</summary>
    public bool RequireAirborneBeforeTouchdown { get; set; } = true;

    /// <summary>Minimum AGL/radio height (ft) to count a sample as airborne for the gate.</summary>
    public double MinAirborneAglFeet { get; set; } = 80;

    /// <summary>Minimum airborne samples after arm before touchdown capture is allowed.</summary>
    public int MinAirborneSamples { get; set; } = 8;

    /// <summary>
    /// Short-final lower bound (NM from threshold) for approach path RMS.
    /// Samples closer than this (over threshold / float) are excluded.
    /// </summary>
    public double ApproachPathMinDistNm { get; set; } = 0.2;

    /// <summary>
    /// Short-final upper bound (NM from threshold) for approach path RMS.
    /// High intermediate approach / spawn is excluded so the metric reflects final path only.
    /// </summary>
    public double ApproachPathMaxDistNm { get; set; } = 4.5;

    public double ImpactPreWindowSeconds { get; set; } = 0.25;
    public double ImpactWindowSeconds { get; set; } = 0.75;
    public double ImpactFilterCutoffHz { get; set; } = 10.0;
    public double ImpactPeakQuantile { get; set; } = 0.99;
    public int MinImpactSamples { get; set; } = 8;
    public double BounceMinAirborneSeconds { get; set; } = 0.08;
    public double BounceWindowSeconds { get; set; } = 3.0;
}

/// <summary>
/// Logical landing-gear mapping over the indexed GEAR IS ON GROUND telemetry.
/// MSFS conventional-gear defaults are center/nose 0, left 1, right 2.
/// </summary>
public sealed class LandingContactMapping
{
    public int LeftMainGearIndex { get; set; } = 1;
    public int RightMainGearIndex { get; set; } = 2;
    public int NoseGearIndex { get; set; } = 0;

    /// <summary>
    /// True only when a challenge/aircraft profile deliberately supplied this mapping.
    /// The conventional 1/2/0 fallback must not be assumed for incompatible gear layouts.
    /// </summary>
    public bool IsAircraftSpecific { get; set; }
}

public sealed class EvaluationSpeedTarget
{
    public double DefaultVappKts { get; set; } = 143;
    public double TouchdownOffsetKts { get; set; } = 5;
    public double Vs0Factor { get; set; } = 1.3;
}

public sealed class EvaluationPhase
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double WeightPercent { get; set; }
    public string? Note { get; set; }
    public List<EvaluationMetric> Metrics { get; set; } = new();
}

public sealed class EvaluationMetric
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double ImportancePercent { get; set; }
    public string? Unit { get; set; }
    public string Evaluator { get; set; } = "target";
    public string Metric { get; set; } = "";
    public string? Note { get; set; }
    public string SampleAt { get; set; } = "touchdown";
    public Dictionary<string, double> Params { get; set; } = new();
    public List<ScorePoint>? Points { get; set; }

    /// <summary>Named piecewise curves used by composite landing evaluators.</summary>
    public Dictionary<string, List<ScorePoint>> Curves { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Attempt-wide penalties applied after all phase metric contributions are summed.
/// Every configured gate multiplies the combined ranked score.
/// </summary>
public sealed class GeneralPenaltyConfig
{
    public ContactStabilityGateConfig? ContactStability { get; set; }
    public AircraftWarningGateConfig? StallWarning { get; set; }
    public AircraftWarningGateConfig? OverspeedWarning { get; set; }
    public GearGateConfig? Gear { get; set; }
    public FlapsGateConfig? Flaps { get; set; }
    public SpoilerDeploymentGateConfig? SpoilerDeployment { get; set; }
    public ManualBrakingGateConfig? ManualBraking { get; set; }
    public AutomationGateConfig? Automation { get; set; }
    public NoseGearImpactGateConfig? NoseGearImpact { get; set; }
    public RolloutGateConfig? Rollout { get; set; }
    public ReverseThrustGateConfig? ReverseThrust { get; set; }
    public PauseUsageGateConfig? PauseUsage { get; set; }
    public SimulationRateGateConfig? SimulationRate { get; set; }
    public CockpitViewGateConfig? CockpitView { get; set; }
}

/// <summary>
/// Penalty-only gate latched when groundspeed first falls below the settle threshold.
/// Remaining runway must be at least <c>max(400 m, 15% of runway length)</c>.
/// </summary>
public sealed class RolloutGateConfig
{
    public double MultiplierOnFail { get; set; } = 0.8;
    public string? PenaltyDescription { get; set; }

    /// <summary>
    /// Minimum remaining runway required at the settle groundspeed threshold.
    /// Floor of 400 m or 15% of runway length, whichever is greater.
    /// </summary>
    public static double RequiredRemainingAt50Knots(double runwayLengthMeters)
        => Math.Max(400, runwayLengthMeters * 0.15);
}

public static class ReverseThrustPolicies
{
    public const string Required = "required";
    public const string OptionalIdleOnly = "optional_idle_only";
    public const string Prohibited = "prohibited";

    public static bool IsSupported(string? policy) => policy is not null &&
        (policy.Equals(Required, StringComparison.OrdinalIgnoreCase)
         || policy.Equals(OptionalIdleOnly, StringComparison.OrdinalIgnoreCase)
         || policy.Equals(Prohibited, StringComparison.OrdinalIgnoreCase));

    public static string Normalize(string policy) => policy.Trim().ToLowerInvariant();
}

/// <summary>
/// Penalty-only reverse-thrust procedure gate. Reverse must normally be selected by the
/// touchdown deadline and completely stowed by the configured low-speed threshold.
/// </summary>
public sealed class ReverseThrustGateConfig
{
    public string Policy { get; set; } = ReverseThrustPolicies.Required;
    public string? ExceptionReason { get; set; }
    public double DeadlineSecondsAfterTouchdown { get; set; } = 4.0;
    public double MinimumNozzlePosition { get; set; } = 0.01;
    public double PoweredReverseThrottleThresholdPercent { get; set; } = -1.0;
    public double StowGroundSpeedKts { get; set; } = 60.0;
    public double MultiplierOnFail { get; set; } = 0.9;
    public string? PenaltyDescription { get; set; }
}

/// <summary>
/// Graded penalty gate for the aircraft-G transient at verified nose-gear contact.
/// Contact-point compression corroborates the event but is not treated as a force value.
/// </summary>
public sealed class NoseGearImpactGateConfig
{
    public double PreContactWindowSeconds { get; set; } = 0.25;
    public double PostContactWindowSeconds { get; set; } = 0.75;
    public double FilterCutoffHz { get; set; } = 10.0;
    public double PeakQuantile { get; set; } = 0.99;
    public int MinimumPostContactSamples { get; set; } = 8;
    public double ModerateDeltaG { get; set; } = 0.25;
    public double ModeratePeakG { get; set; } = 1.30;
    public double ModerateMultiplier { get; set; } = 0.95;
    public double SevereDeltaG { get; set; } = 0.50;
    public double SeverePeakG { get; set; } = 1.70;
    public double SevereMultiplier { get; set; } = 0.90;
    public double RecontactDebounceSeconds { get; set; } = 0.08;
    public double CompressionNoiseThreshold { get; set; } = 0.02;
    public string? PenaltyDescription { get; set; }
}

public sealed class SpoilerDeploymentGateConfig
{
    public double MinimumSurfacePosition { get; set; } = 0.15;
    public double DeadlineSecondsAfterTouchdown { get; set; } = 2.0;
    public double MultiplierOnFail { get; set; } = 0.9;
    public string? PenaltyDescription { get; set; }
}

public sealed class ManualBrakingGateConfig
{
    public double PedalPressThreshold { get; set; } = 0.05;
    public double DeadlineSecondsAfterNoseTouchdown { get; set; } = 4.0;
    public double MultiplierOnFail { get; set; } = 0.9;
    public string? PenaltyDescription { get; set; }
}

public sealed class AutomationGateConfig
{
    public double HeadingAltitudeOffRadioHeightFeet { get; set; } = 2000;
    public double AllAutomationOffRadioHeightFeet { get; set; } = 1000;
    public double MultiplierOnFail { get; set; } = 0.9;
    public string? PenaltyDescription { get; set; }
}

public sealed class PauseUsageGateConfig
{
    public double MultiplierOnFail { get; set; } = 0.95;
    public string? PenaltyDescription { get; set; }
}

public sealed class SimulationRateGateConfig
{
    public double MinimumAllowedRate { get; set; } = 0.99;
    public double MultiplierOnFail { get; set; } = 0.8;
    public string? PenaltyDescription { get; set; }
}

/// <summary>
/// General penalty stacked once per cockpit → non-cockpit camera transition before touchdown.
/// </summary>
public sealed class CockpitViewGateConfig
{
    /// <summary>
    /// Multiplier applied to the combined score for each exit from cockpit view (CAMERA STATE 2).
    /// Two exits apply this factor squared, and so on.
    /// </summary>
    public double MultiplierPerSwitch { get; set; } = 0.95;
    public string? PenaltyDescription { get; set; }
}

/// <summary>MSFS CAMERA STATE values used by Challenge Lab gates.</summary>
public static class CameraStates
{
    /// <summary>Pilot/cockpit camera (MSFS CAMERA STATE = 2).</summary>
    public const int Cockpit = 2;

    public static bool IsCockpit(int cameraState) => cameraState == Cockpit;
}

/// <summary>
/// Penalty-only gate latched by an aircraft-native warning (stall or overspeed)
/// at any time during an armed attempt.
/// </summary>
public sealed class AircraftWarningGateConfig
{
    public double MultiplierOnWarning { get; set; } = 0.9;
    public string? PenaltyDescription { get; set; }
}

/// <summary>
/// Penalty-only bounce gate. The initial landing is not a bounce; each valid
/// airborne/recontact cycle after it increments the bounce count.
/// </summary>
public sealed class ContactStabilityGateConfig
{
    public double OneBounceMultiplier { get; set; } = 0.9;
    public double TwoOrMoreBouncesMultiplier { get; set; } = 0.8;
    public string? PenaltyDescription { get; set; }
}

public sealed class GearGateConfig
{
    public double MultiplierOnFail { get; set; } = 0.1;
    public string? PenaltyDescription { get; set; }
}

/// <summary>
/// Flaps safety gate (like gear): correct setting awards no points;
/// out of range multiplies the owning phase score.
/// </summary>
public sealed class FlapsGateConfig
{
    /// <summary>Minimum flaps handle index for a valid landing config (inclusive).</summary>
    public double MinIndex { get; set; } = 2;

    /// <summary>Maximum flaps handle index for a valid landing config (inclusive).</summary>
    public double MaxIndex { get; set; } = 5;

    public double MultiplierOnFail { get; set; } = 0.5;
    public string? PenaltyDescription { get; set; }
}
