using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Scoring;

/// <summary>Human-readable explanations for every scored metric (always filled for reports).</summary>
public static class MetricExplanations
{
    public static string For(
        EvaluationMetric metric,
        LandingSnapshot snapshot,
        ChallengeConfig challenge,
        double score01,
        double? raw)
    {
        var baseNote = !string.IsNullOrWhiteSpace(metric.Note)
            ? metric.Note.Trim()
            : DefaultCatalog(metric.Id, metric.DisplayName);

        var measured = FormatMeasured(metric, snapshot, challenge, raw);
        var how = HowItIsScored(metric);
        var verdict = ScoreVerdict(score01);

        return string.Join(" ",
            new[] { baseNote, measured, how, verdict }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public static string DefaultCatalog(string id, string displayName) => id switch
    {
        "touchdown_vs" =>
            "Vertical speed at main-gear touchdown. A330 ideal is about −150 fpm (−100…−180). " +
            "A firm plant is preferred; ultra-soft “butter” floats and hard landings both score poorly.",
        "touchdown_impact" =>
            "Initial touchdown impact combines official touchdown vertical speed and robust filtered peak G. Later bounce impacts are excluded.",
        "touchdown_point" =>
            "Longitudinal position of the first accepted main-gear touchdown, measured from the selected landing threshold.",
        "flare_efficiency" =>
            "Flare efficiency measures sustained float distance, duration, and positive vertical speed before first main-gear contact.",
        "contact_stability" =>
            "Contact stability measures valid post-touchdown airborne intervals and secondary impacts. One-main-first contact is not a bounce.",
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
            "Safety gate: gear-down is required baseline and awards no points. " +
            "Gear-up applies a heavy overall score cut (~90%) unless the challenge allows gear-up landings.",
        "flaps" =>
            "Safety gate like gear: landing flaps are required baseline and award no points. " +
            "Flaps not set (or outside the landing band) multiplies the overall score.",
        "approach_path" =>
            "Legacy single-metric approach path (RMS altitude error vs 3°). Replaced by average glideslope + steadiness.",
        "approach_glideslope" =>
            "Time-weighted mean absolute altitude error versus the runway's nominal glideslope path on short final (∫|e|dt / T). " +
            "Default angle is 3°; challenges set the angle; free flight prefers a curated steep-approach catalog, then VASI/PAPI. " +
            "The path meets runway elevation at a normalized unflared aim point 1,000 ft past the landing threshold. " +
            "High and low deviations cannot cancel one another.",
        "approach_vertical_steady" =>
            "Vertical path steadiness: reversal-only excess variation of altitude error per second. " +
            "A smooth one-way capture is removed; pumping above and below the path is penalized.",
        "approach_lateral_steady" =>
            "Lateral path steadiness on short final: reversal-only weave per metre flown. " +
            "A smooth centerline intercept is removed; repeated S-turns are penalized.",
        "approach_bank_stability" =>
            "Bank angle stability on short final: time-weighted mean absolute bank (∫|φ|dt / T). " +
            "Wings level scores high; sustained bank and left/right rocking score low.",
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
        "reverse_thrust" =>
            "Operational gate for timely reverse selection, challenge-specific reverse restrictions, and complete low-speed stow.",
        "crab" =>
            "Legacy crab-at-flare metric (replaced by rollout alignment).",
        _ => $"{displayName}: part of the configurable landing evaluation."
    };

    private static string FormatMeasured(
        EvaluationMetric metric,
        LandingSnapshot snap,
        ChallengeConfig challenge,
        double? raw)
    {
        var id = metric.Id;
        return id switch
        {
            "touchdown_vs" =>
                $"Measured: {snap.VerticalSpeedAtTouchdownFpm:0} fpm " +
                $"(absolute sink rate {Math.Abs(snap.VerticalSpeedAtTouchdownFpm):0} fpm).",
            "touchdown_point" when snap.Touchdown is not null
                                   && TouchdownPointCalculator.TryCalculate(
                                       challenge.Runway,
                                       snap.Touchdown,
                                       out var touchdownPoint,
                                       out _) =>
                FormatTouchdownPoint(touchdownPoint),
            "peak_g" =>
                $"Measured: {snap.PeakGForce:0.00} G.",
            "centerline" =>
                $"Measured: {Math.Abs(snap.TouchdownLateralOffsetM):0.0} m from centerline.",
            "alignment" =>
                $"Measured: {Math.Abs(snap.TouchdownHeadingErrorDeg):0.0}° heading error at touchdown.",
            "airspeed" =>
                $"Measured: {snap.AirspeedAtTouchdownKts:0.0} kt IAS vs optimal target {snap.TargetTouchdownIasKts:0.0} kt " +
                $"(VAPP {snap.VappKts:0.0}, {snap.SpeedTargetSource}) · delta {Signed(snap.TouchdownIasErrorKts)} kt.",
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
            "approach_glideslope" =>
                $"Measured: mean |path error| {snap.ApproachGlideslopeMeanAbsFt:0} ft " +
                $"(window {snap.ApproachMetricDurationSec:0.0}s, n={snap.ApproachPathSampleCount}).",
            "approach_vertical_steady" =>
                $"Measured: vertical excess variation {snap.ApproachVerticalVariationFtPerSec:0.00} ft/s " +
                $"(window {snap.ApproachMetricDurationSec:0.0}s).",
            "approach_lateral_steady" =>
                $"Measured: reversal-only approach weave {snap.ApproachLateralWeaveIndex:0.000} m/m " +
                $"over {snap.ApproachLateralDistanceM:0} m.",
            "approach_bank_stability" =>
                $"Measured: mean |bank| {snap.ApproachBankMeanAbsDeg:0.0}° " +
                $"(window {snap.ApproachMetricDurationSec:0.0}s, n={snap.ApproachPathSampleCount}).",
            "post_td_alignment" =>
                $"Measured: mean heading error {snap.PostTouchdownAlignmentMeanDeg:0.0}° after TD+2 s " +
                $"(peak {snap.PostTouchdownAlignmentPeakDeg:0.0}°, n={snap.PostTouchdownAlignmentSampleCount}).",
            "rollout_path" =>
                $"Measured: mean |offset| {snap.RolloutLateralMeanM:0.0} m over {snap.RolloutDistanceM:0} m of rollout.",
            "rollout_weave" =>
                $"Measured: weave index {snap.RolloutWeaveIndex:0.000} m/m over {snap.RolloutDistanceM:0} m.",
            "max_centerline" =>
                $"Measured: peak rollout offset {snap.RolloutLateralPeakM:0.0} m.",
            _ when raw is not null && metric.Unit is not null =>
                $"Measured: {raw:0.##} {metric.Unit}.",
            _ when raw is not null =>
                $"Measured: {raw:0.##}.",
            _ => ""
        };
    }

    private static string HowItIsScored(EvaluationMetric metric)
    {
        var eval = metric.Evaluator.ToLowerInvariant();
        if (eval == "centerline" || metric.Id == "centerline")
        {
            var (t, z, p) = CenterlineEvaluator.ResolveParams(metric);
            return $"Scoring: 100% within ±{t:0.#} m, falls to 0% at {z:0.#} m (curve exponent {p:0.#}).";
        }

        if (eval == "piecewise")
            return "Scoring: multi-zone curve (piecewise) — each point maps measured value → metric score 0–100%.";

        if (eval == "upperboundbands" && metric.Points is { Count: > 0 })
        {
            var bands = string.Join(", ", metric.Points.Select(point =>
                $"≤{point.V:0.##} {metric.Unit ?? "units"} → {point.S:0.##}%"));
            return $"Scoring: absolute early/late error bands ({bands}); beyond the final band → 0%.";
        }

        if (eval == "target" && metric.Params.Count > 0)
        {
            var ideal = Get(metric.Params, "ideal", 0);
            var tol = Get(metric.Params, "tolerance", 1);
            var max = Get(metric.Params, "maxError", tol * 3);
            return $"Scoring: full score near {ideal:0.##} (±{tol:0.##}), zero by {max:0.##} {metric.Unit ?? "units"} error.";
        }

        if (eval == "range" && metric.Params.Count > 0)
        {
            var min = Get(metric.Params, "min", 0);
            var max = Get(metric.Params, "max", 0);
            return $"Scoring: full score in [{min:0.##} … {max:0.##}] {metric.Unit ?? ""}.";
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

    private static string FormatTouchdownPoint(TouchdownPointMeasurement point)
    {
        var onTarget = point.AbsoluteErrorFeet < 0.05;
        var signedError = onTarget ? "0.0" : Signed(point.SignedErrorFeet);
        var direction = onTarget ? "on target" : point.SignedErrorFeet < 0 ? "early" : "late";
        return $"Measured: touchdown {point.ActualDistanceFeet:0.0} ft from threshold; " +
               $"perfect point {point.PerfectDistanceFeet:0.0} ft; " +
               $"error {signedError} ft ({direction}).";
    }

    private static string Signed(double v) => v >= 0 ? $"+{v:0.0}" : $"{v:0.0}";

    private static double Get(Dictionary<string, double> p, string key, double fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;
}
