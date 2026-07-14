using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Scoring;

/// <summary>Human-readable explanations for every scored metric (always filled for reports).</summary>
public static class MetricExplanations
{
    public static string For(
        CriterionConfig criterion,
        LandingSnapshot snapshot,
        DifficultyLevel level,
        double score01,
        double? raw)
    {
        var baseNote = !string.IsNullOrWhiteSpace(criterion.Note)
            ? criterion.Note.Trim()
            : DefaultCatalog(criterion.Id, criterion.DisplayName);

        var measured = FormatMeasured(criterion, snapshot, raw);
        var how = HowItIsScored(criterion, level);
        var verdict = ScoreVerdict(score01);

        if (!criterion.AppliesTo(level))
        {
            return $"{baseNote} Not evaluated on {level.ToDisplayName()} difficulty.";
        }

        return string.Join(" ",
            new[] { baseNote, measured, how, verdict }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public static string DefaultCatalog(string id, string displayName) => id switch
    {
        "touchdown_vs" =>
            "Vertical speed at main-gear touchdown. A330 ideal is about −150 fpm (−100…−180). " +
            "A firm plant is preferred; ultra-soft “butter” floats and hard landings both score poorly.",
        "peak_g" =>
            "Peak vertical G around touchdown. Moderate G is normal for a firm plant; " +
            "very high G indicates a hard landing that stresses gear and structure.",
        "centerline" =>
            "Absolute lateral distance from runway centerline at main-gear touchdown (metres). " +
            "Position only — nose crab/heading is scored separately.",
        "alignment" =>
            "Fuselage heading versus runway course at touchdown. " +
            "Large error means the aircraft is still crabbed or yawed across the runway.",
        "airspeed" =>
            "Touchdown indicated airspeed versus the calculated target (VAPP − 5 kt). " +
            "Not a fixed number: target follows approach speed for this weight/config.",
        "excess_speed" =>
            "How many knots you are above VAPP at touchdown. " +
            "Extra energy causes long floats and late touchdown even if the landing feels smooth.",
        "bank" =>
            "Wing bank angle at touchdown. Keep wings level to protect engine pods in crosswind.",
        "gear" =>
            "Landing gear must be down at touchdown. Up is a hard fail for this criterion.",
        "flaps" =>
            "Flaps handle index in the expected landing range for this challenge setup.",
        "approach_path" =>
            "How steadily you held the approach path in the last segment before landing (altitude vs 3° path).",
        "ground_track" =>
            "Mean error between ground track (direction the CG moves over the ground) and runway heading, " +
            "from 3 seconds before touchdown to 3 seconds after. Not wind-dependent crab angle.",
        "post_td_alignment" =>
            "From 2 seconds after touchdown until about 50 kt: fuselage should be de-crabbed and " +
            "held parallel to the runway with rudder.",
        "rollout_path" =>
            "Distance-weighted mean centerline offset after touchdown: (∫|d| ds) / distance. " +
            "How close the rundown stays to the paint.",
        "rollout_weave" =>
            "How much you S-turn on rollout: total left/right change in offset per metre traveled. " +
            "A steady rundown scores high; weaving left and right scores low.",
        "max_centerline" =>
            "Largest lateral offset from centerline during the rollout after touchdown.",
        "rollout" =>
            "Heading stability during rollout (legacy variance metric).",
        "crab" =>
            "Legacy crab-at-flare metric (replaced by ground track / rollout alignment).",
        _ => $"{displayName}: part of the configurable landing evaluation."
    };

    private static string FormatMeasured(CriterionConfig criterion, LandingSnapshot snap, double? raw)
    {
        var id = criterion.Id;
        return id switch
        {
            "touchdown_vs" =>
                $"Measured: {snap.VerticalSpeedAtTouchdownFpm:0} fpm.",
            "peak_g" =>
                $"Measured: {snap.PeakGForce:0.00} G.",
            "centerline" =>
                $"Measured: {Math.Abs(snap.TouchdownLateralOffsetM):0.0} m from centerline.",
            "alignment" =>
                $"Measured: {Math.Abs(snap.TouchdownHeadingErrorDeg):0.0}° heading error at touchdown.",
            "airspeed" =>
                $"Measured: {snap.AirspeedAtTouchdownKts:0.0} kt IAS · target {snap.TargetTouchdownIasKts:0.0} kt " +
                $"(VAPP {snap.VappKts:0.0}, {snap.SpeedTargetSource}) · error {Signed(snap.TouchdownIasErrorKts)} kt.",
            "excess_speed" =>
                $"Measured: +{snap.ExcessSpeedOverVappKts:0.0} kt over VAPP ({snap.AirspeedAtTouchdownKts:0.0} vs {snap.VappKts:0.0}).",
            "bank" =>
                $"Measured: {Math.Abs(snap.BankAtTouchdownDeg):0.0}° bank.",
            "gear" =>
                snap.GearDownAtTouchdown ? "Measured: gear down." : "Measured: gear up.",
            "flaps" =>
                $"Measured: flaps index {snap.FlapsIndexAtTouchdown}.",
            "approach_path" =>
                $"Measured: path RMS {snap.ApproachPathRms:0} ft.",
            "ground_track" =>
                $"Measured: mean track error {snap.GroundTrackErrorMeanDeg:0.0}° " +
                $"(peak {snap.GroundTrackErrorPeakDeg:0.0}°, n={snap.GroundTrackSampleCount}).",
            "post_td_alignment" =>
                $"Measured: mean heading error {snap.PostTouchdownAlignmentMeanDeg:0.0}° after TD+2 s " +
                $"(peak {snap.PostTouchdownAlignmentPeakDeg:0.0}°, n={snap.PostTouchdownAlignmentSampleCount}).",
            "rollout_path" =>
                $"Measured: mean |offset| {snap.RolloutLateralMeanM:0.0} m over {snap.RolloutDistanceM:0} m of rollout.",
            "rollout_weave" =>
                $"Measured: weave index {snap.RolloutWeaveIndex:0.000} m/m over {snap.RolloutDistanceM:0} m.",
            "max_centerline" =>
                $"Measured: peak rollout offset {snap.RolloutLateralPeakM:0.0} m.",
            _ when raw is not null && criterion.Unit is not null =>
                $"Measured: {raw:0.##} {criterion.Unit}.",
            _ when raw is not null =>
                $"Measured: {raw:0.##}.",
            _ => ""
        };
    }

    private static string HowItIsScored(CriterionConfig criterion, DifficultyLevel level)
    {
        var eval = criterion.Evaluator.ToLowerInvariant();
        if (eval is "centerline" || criterion.Id == "centerline")
        {
            var (t, z, p) = CenterlineEvaluator.ResolveParams(criterion, level);
            return $"Scoring ({level.ToDisplayName()}): 100% within ±{t:0.#} m, falls to 0% at {z:0.#} m (curve exponent {p:0.#}).";
        }

        if (eval is "piecewise" or "zones" or "curve")
            return "Scoring: multi-zone curve (piecewise) from the challenge profile.";

        if (eval == "target" && criterion.Params.Count > 0)
        {
            var ideal = Get(criterion.Params, "ideal", 0);
            var tol = Get(criterion.Params, "tolerance", 1);
            var max = Get(criterion.Params, "maxError", tol * 3);
            return $"Scoring: full score near {ideal:0.##} (±{tol:0.##}), zero by {max:0.##} {criterion.Unit ?? "units"} error.";
        }

        if (eval == "range" && criterion.Params.Count > 0)
        {
            var min = Get(criterion.Params, "min", 0);
            var max = Get(criterion.Params, "max", 0);
            return $"Scoring: full score in [{min:0.##} … {max:0.##}] {criterion.Unit ?? ""}.";
        }

        if (eval == "boolean")
            return "Scoring: pass/fail.";

        return "";
    }

    private static string ScoreVerdict(double score01) => score01 switch
    {
        >= 0.95 => "Result: excellent.",
        >= 0.85 => "Result: very good.",
        >= 0.70 => "Result: good.",
        >= 0.55 => "Result: acceptable.",
        >= 0.40 => "Result: weak.",
        > 0 => "Result: poor.",
        _ => "Result: failed this criterion."
    };

    private static string Signed(double v) => v >= 0 ? $"+{v:0.0}" : $"{v:0.0}";

    private static double Get(Dictionary<string, double> p, string key, double fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;
}
