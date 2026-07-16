namespace ChallengeLab.Core.Models;

/// <summary>Captured metrics at key landing moments for scoring.</summary>
public sealed class LandingSnapshot
{
    public TelemetrySample? Touchdown { get; set; }
    public double PeakGForce { get; set; } = 1.0;
    public double PeakAbsBankDeg { get; set; }
    public double MaxLateralOffsetM { get; set; }
    public double TouchdownLateralOffsetM { get; set; }
    public double TouchdownHeadingErrorDeg { get; set; }
    /// <summary>Diagnostic: time-weighted RMS altitude error vs the stabilized path (ft).</summary>
    public double ApproachPathRms { get; set; }
    public int ApproachPathSampleCount { get; set; }

    /// <summary>
    /// Time-weighted mean absolute altitude error vs the nominal glideslope path: ∫|e(t)|dt / T (ft).
    /// Path meets field elevation at the normalized unflared aim point 1,000 ft past the landing threshold.
    /// High and low deviations cannot cancel one another.
    /// </summary>
    public double ApproachGlideslopeMeanAbsFt { get; set; }

    /// <summary>
    /// Vertical steadiness: excess total variation of altitude error per second (ft/s).
    /// The net start-to-end correction is removed so a monotonic capture scores near 0.
    /// </summary>
    public double ApproachVerticalVariationFtPerSec { get; set; }

    /// <summary>
    /// Lateral weave: excess total variation of lateral offset per metre flown (m/m).
    /// The net intercept is removed so only reversals / S-turns add weave.
    /// </summary>
    public double ApproachLateralWeaveIndex { get; set; }

    /// <summary>
    /// Bank stability: time-weighted mean absolute bank on short final (degrees):
    /// ∫|φ(t)|dt / T. Wings level scores near 0; sustained bank and left/right rocking raise the value.
    /// </summary>
    public double ApproachBankMeanAbsDeg { get; set; }

    /// <summary>Ground distance used for approach lateral weave (metres).</summary>
    public double ApproachLateralDistanceM { get; set; }

    /// <summary>Duration of the short-final window used for approach metrics (seconds).</summary>
    public double ApproachMetricDurationSec { get; set; }
    public double RolloutHeadingVariance { get; set; }
    /// <summary>Legacy flare heading-vs-runway (not scored; crab is wind-dependent).</summary>
    public double CrabAngleAtFlareDeg { get; set; }

    /// <summary>
    /// Mean |heading − runway| from TD+2s until GS &lt; settle speed (de-crab / rudder alignment).
    /// </summary>
    public double PostTouchdownAlignmentMeanDeg { get; set; }

    /// <summary>RMS heading error over the same post-TD+2s window.</summary>
    public double PostTouchdownAlignmentRmsDeg { get; set; }

    /// <summary>Peak |heading − runway| in the post-TD+2s window.</summary>
    public double PostTouchdownAlignmentPeakDeg { get; set; }

    public int PostTouchdownAlignmentSampleCount { get; set; }

    /// <summary>
    /// Distance-weighted mean |centerline offset| after touchdown: (∫|d| ds) / S (metres).
    /// Steady offset scores better than the same average with weaving when paired with weave index.
    /// </summary>
    public double RolloutLateralMeanM { get; set; }

    /// <summary>Peak |centerline offset| after touchdown (metres).</summary>
    public double RolloutLateralPeakM { get; set; }

    /// <summary>
    /// Weave index: total variation of lateral offset per metre traveled = Σ|Δd| / S.
    /// High when pilot goes left/right; near 0 for a steady rundown.
    /// </summary>
    public double RolloutWeaveIndex { get; set; }

    /// <summary>Ground distance traveled after touchdown used in the integral (metres).</summary>
    public double RolloutDistanceM { get; set; }

    public int RolloutPathSampleCount { get; set; }
    public int RolloutPathSegmentCount { get; set; }

    public bool GearDownAtTouchdown { get; set; } = true;
    public int FlapsIndexAtTouchdown { get; set; }
    public double VerticalSpeedAtTouchdownFpm { get; set; }
    public double AirspeedAtTouchdownKts { get; set; }
    public double BankAtTouchdownDeg { get; set; }
    public double PitchAtTouchdownDeg { get; set; }

    /// <summary>Resolved approach speed (VAPP) used for scoring.</summary>
    public double VappKts { get; set; }

    /// <summary>Target touchdown IAS = VAPP − offset (typically 5 kt).</summary>
    public double TargetTouchdownIasKts { get; set; }

    /// <summary>IAS − target. Negative = slow, positive = fast.</summary>
    public double TouchdownIasErrorKts { get; set; }

    /// <summary>max(0, IAS − VAPP). Excess energy / float risk.</summary>
    public double ExcessSpeedOverVappKts { get; set; }

    public string SpeedTargetSource { get; set; } = "";

    public ImpactAnalysis? InitialImpact { get; set; }
    public FloatAnalysis? FloatAnalysis { get; set; }
    public ContactStabilityAnalysis? ContactStability { get; set; }
    public bool TouchdownAnalysisComplete { get; set; }
    public bool ContactMappingDegraded { get; set; }

    /// <summary>Latched true if any telemetry sample warned of a stall while armed.</summary>
    public bool StallWarningOccurred { get; set; }
    public bool StallWarningCoverageAvailable { get; set; } = true;

    /// <summary>Operational gate observations for Challenge, Career, and Free Flight.</summary>
    public LandingGateObservations GateObservations { get; } = new();

    public List<TelemetrySample> ApproachSamples { get; } = new();
    public List<TelemetrySample> RolloutSamples { get; } = new();
    public List<LandingTelemetrySample> LandingEventSamples { get; } = new();
}
