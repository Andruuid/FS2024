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
    public double ApproachPathRms { get; set; }
    public double RolloutHeadingVariance { get; set; }
    /// <summary>Legacy flare heading-vs-runway (not scored; crab is wind-dependent).</summary>
    public double CrabAngleAtFlareDeg { get; set; }

    /// <summary>
    /// Mean absolute ground-track error vs runway heading over the TD±window (degrees).
    /// Measures where the CG is moving, not nose crab into wind.
    /// </summary>
    public double GroundTrackErrorMeanDeg { get; set; }

    /// <summary>RMS ground-track error over the same window (degrees).</summary>
    public double GroundTrackErrorRmsDeg { get; set; }

    /// <summary>Peak absolute ground-track error in the window (degrees).</summary>
    public double GroundTrackErrorPeakDeg { get; set; }

    /// <summary>How many track samples were used in the window.</summary>
    public int GroundTrackSampleCount { get; set; }

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

    public List<TelemetrySample> ApproachSamples { get; } = new();
    public List<TelemetrySample> RolloutSamples { get; } = new();
}
