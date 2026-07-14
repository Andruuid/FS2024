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
    public EvaluationGates? Gates { get; set; }

    public LandingSessionSettings ToSessionSettings() => new(
        Settle!.GroundSpeedKts,
        Settle.HoldSeconds,
        Timing!.GroundTrackWindowBeforeSeconds,
        Timing.GroundTrackWindowAfterSeconds,
        Timing.PostTouchdownAlignmentDelaySeconds,
        Timing.FlareAglFeet,
        Timing.PostArmIgnoreSeconds,
        Timing.RequireAirborneBeforeTouchdown,
        Timing.MinAirborneAglFeet,
        Timing.MinAirborneSamples,
        Timing.ApproachPathMinDistNm,
        Timing.ApproachPathMaxDistNm,
        SpeedTarget!.DefaultVappKts,
        SpeedTarget.TouchdownOffsetKts,
        SpeedTarget.Vs0Factor);
}

public sealed record LandingSessionSettings(
    double SettledGroundSpeedKts,
    double SettledHoldSeconds,
    double GroundTrackWindowBeforeSeconds,
    double GroundTrackWindowAfterSeconds,
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
    double Vs0Factor);

public sealed class EvaluationSettle
{
    public double GroundSpeedKts { get; set; } = 50;
    public double HoldSeconds { get; set; } = 1.0;
}

public sealed class EvaluationTiming
{
    public double GroundTrackWindowBeforeSeconds { get; set; } = 3;
    public double GroundTrackWindowAfterSeconds { get; set; } = 3;
    public double PostTouchdownAlignmentDelaySeconds { get; set; } = 2;
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
    public double ApproachPathMaxDistNm { get; set; } = 3.0;
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
}

public sealed class EvaluationGates
{
    public GearGateConfig? Gear { get; set; }
}

public sealed class GearGateConfig
{
    public double MultiplierOnFail { get; set; } = 0.1;
    public string? PenaltyDescription { get; set; }
}
