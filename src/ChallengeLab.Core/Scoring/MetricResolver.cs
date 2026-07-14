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
            // IAS − target (VAPP−5). Negative = slow, positive = fast.
            "touchdowniaserrorkts" or "ias_error_kts" or "touchdown_ias_error" => snap.TouchdownIasErrorKts,
            // |IAS − target|
            "touchdowniasabserrorkts" or "ias_abs_error_kts" => Math.Abs(snap.TouchdownIasErrorKts),
            // max(0, IAS − VAPP) — excess energy / float risk
            "excessspeedovervappkts" or "excess_over_vapp" or "excess_ias_kts" => snap.ExcessSpeedOverVappKts,
            "vappkts" or "vapp" => snap.VappKts,
            "targettouchdowniaskts" or "target_ias" => snap.TargetTouchdownIasKts,
            "flapsindex" or "flaps_index" => snap.FlapsIndexAtTouchdown,
            "geardown" or "gear_down" => snap.GearDownAtTouchdown ? 1.0 : 0.0,
            "bankattouchdowndeg" or "bank_deg" => Math.Abs(snap.BankAtTouchdownDeg),
            "pitchattouchdowndeg" or "pitch_deg" => snap.PitchAtTouchdownDeg,
            "approachpathrms" or "approach_rms" => snap.ApproachPathRms,
            "rolloutstability" or "rollout_heading_var" => snap.RolloutHeadingVariance,
            // Ground-track path of CG vs runway (replaces wind-dependent crab scoring)
            "groundtrackerrormeandeg" or "ground_track_error" or "track_error_mean" => snap.GroundTrackErrorMeanDeg,
            "groundtrackerrorrmsdeg" or "track_error_rms" => snap.GroundTrackErrorRmsDeg,
            "groundtrackerrorpeakdeg" or "track_error_peak" => snap.GroundTrackErrorPeakDeg,
            // Post TD+2s heading alignment until ~50 kt
            "posttouchdownalignmentmeandeg" or "post_td_alignment" or "rollout_heading_error" => snap.PostTouchdownAlignmentMeanDeg,
            "posttouchdownalignmentrmsdeg" or "post_td_alignment_rms" => snap.PostTouchdownAlignmentRmsDeg,
            "posttouchdownalignmentpeakdeg" or "post_td_alignment_peak" => snap.PostTouchdownAlignmentPeakDeg,
            // Distance-integrated centerline path after touchdown
            "rolloutlateralmeanm" or "rollout_lateral_mean" or "path_mean_offset_m" => snap.RolloutLateralMeanM,
            "rolloutlateralpeakm" or "rollout_lateral_peak" => snap.RolloutLateralPeakM,
            "rolloutweaveindex" or "rollout_weave" or "weave_index" => snap.RolloutWeaveIndex,
            "rolloutdistancem" or "rollout_distance_m" => snap.RolloutDistanceM,
            "crabangledeg" or "crab_deg" => Math.Abs(snap.CrabAngleAtFlareDeg), // legacy / diagnostics only
            "peakbankdeg" or "peak_bank" => snap.PeakAbsBankDeg,
            _ => null
        };
    }

    /// <summary>Human-readable raw value for reports (includes target context for speed metrics).</summary>
    public static string? FormatRawDisplay(string metric, LandingSnapshot snap, double? raw, string? unit)
    {
        var m = metric.ToLowerInvariant();
        if (m is "touchdowniaserrorkts" or "ias_error_kts" or "touchdown_ias_error")
        {
            var sign = snap.TouchdownIasErrorKts >= 0 ? "+" : "";
            return $"{snap.AirspeedAtTouchdownKts:0.0} kt  (target {snap.TargetTouchdownIasKts:0.0}, VAPP {snap.VappKts:0.0}, err {sign}{snap.TouchdownIasErrorKts:0.0})";
        }

        if (m is "excessspeedovervappkts" or "excess_over_vapp" or "excess_ias_kts")
            return $"+{snap.ExcessSpeedOverVappKts:0.0} kt over VAPP {snap.VappKts:0.0}  (IAS {snap.AirspeedAtTouchdownKts:0.0})";

        if (m is "touchdownairspeedkts" or "touchdown_ias")
            return $"{snap.AirspeedAtTouchdownKts:0.0} kt  (target {snap.TargetTouchdownIasKts:0.0})";

        if (raw is null) return null;
        return unit is null ? $"{raw:0.##}" : $"{raw:0.##} {unit}";
    }
}
