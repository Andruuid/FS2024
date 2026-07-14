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
    public double CrabAngleAtFlareDeg { get; set; }
    public bool GearDownAtTouchdown { get; set; } = true;
    public int FlapsIndexAtTouchdown { get; set; }
    public double VerticalSpeedAtTouchdownFpm { get; set; }
    public double AirspeedAtTouchdownKts { get; set; }
    public double BankAtTouchdownDeg { get; set; }
    public double PitchAtTouchdownDeg { get; set; }
    public List<TelemetrySample> ApproachSamples { get; } = new();
    public List<TelemetrySample> RolloutSamples { get; } = new();
}
