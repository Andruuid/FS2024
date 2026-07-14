using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public static class MetricResolver
{
    public static double? Resolve(string metric, LandingSnapshot snap, ChallengeConfig challenge)
    {
        return metric.ToLowerInvariant() switch
        {
            "touchdownverticalspeedfpm" or "touchdown_vs" => snap.VerticalSpeedAtTouchdownFpm,
            "peakg" or "peak_g" or "gforce" => snap.PeakGForce,
            "centerlinedeviationm" or "centerline_m" => Math.Abs(snap.TouchdownLateralOffsetM),
            "maxcenterlinedeviationm" or "max_centerline_m" => Math.Abs(snap.MaxLateralOffsetM),
            "alignmentdeg" or "heading_error_deg" => Math.Abs(snap.TouchdownHeadingErrorDeg),
            "touchdownairspeedkts" or "touchdown_ias" => snap.AirspeedAtTouchdownKts,
            "flapsindex" or "flaps_index" => snap.FlapsIndexAtTouchdown,
            "geardown" or "gear_down" => snap.GearDownAtTouchdown ? 1.0 : 0.0,
            "bankattouchdowndeg" or "bank_deg" => Math.Abs(snap.BankAtTouchdownDeg),
            "pitchattouchdowndeg" or "pitch_deg" => snap.PitchAtTouchdownDeg,
            "approachpathrms" or "approach_rms" => snap.ApproachPathRms,
            "rolloutstability" or "rollout_heading_var" => snap.RolloutHeadingVariance,
            "crabangledeg" or "crab_deg" => Math.Abs(snap.CrabAngleAtFlareDeg),
            "peakbankdeg" or "peak_bank" => snap.PeakAbsBankDeg,
            _ => null
        };
    }
}
