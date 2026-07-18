using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public readonly record struct CompositeMetricEvaluation(
    bool IsAvailable,
    double ScorePercent,
    double RawValue,
    string Unit,
    string Explanation,
    bool IsDegraded,
    string? DegradedReason)
{
    public static CompositeMetricEvaluation Unavailable(string reason) =>
        new(false, 0, 0, "%", "", false, reason);
}

public static class CompositeMetricEvaluator
{
    public static CompositeMetricEvaluation Evaluate(
        EvaluationMetric metric,
        LandingEvaluationKey key,
        LandingSnapshot snapshot,
        LandingResultDiagnostics diagnostics)
    {
        return metric.Evaluator.Trim().ToLowerInvariant() switch
        {
            "landingimpact" => EvaluateImpact(metric, snapshot, diagnostics),
            "flareefficiency" => EvaluateFlare(metric, snapshot, diagnostics),
            "contactstability" => EvaluateContact(metric, key, snapshot, diagnostics),
            "crabangle" => EvaluateCrabAngle(metric, snapshot, diagnostics),
            "runwayalignment" => EvaluateRunwayAlignment(metric, snapshot, diagnostics),
            _ => throw new ArgumentException($"'{metric.Evaluator}' is not a composite evaluator.")
        };
    }

    private static CompositeMetricEvaluation EvaluateImpact(
        EvaluationMetric metric,
        LandingSnapshot snapshot,
        LandingResultDiagnostics diagnostics)
    {
        if (snapshot.InitialImpact is not { Available: true } impact)
            return CompositeMetricEvaluation.Unavailable("Initial touchdown-impact analysis is not complete.");

        var vsScore = Curve(metric, "verticalSpeed", impact.VerticalSpeedFpm);
        var gScore = Curve(metric, "peakG", impact.RobustPeakG);
        var vsWeight = Param(metric, "verticalSpeedWeight");
        var gWeight = Param(metric, "peakGWeight");
        var score = impact.TelemetryDegraded
            ? vsScore
            : CompositeScoringMath.CombineScoresByPenaltyRms(
                (vsScore, vsWeight),
                (gScore, gWeight));

        diagnostics.TouchdownVerticalSpeedFpm = impact.VerticalSpeedFpm;
        diagnostics.TouchdownSinkRateFpm = impact.VerticalSpeedFpm;
        diagnostics.TouchdownNormalVelocityFpm = impact.TouchdownNormalVelocityFpm;
        diagnostics.TouchdownVerticalSpeedSubscore = vsScore;
        diagnostics.TouchdownVerticalSpeedSource = impact.VerticalSpeedSource;
        diagnostics.TouchdownRawPeakG = impact.RawPeakG;
        diagnostics.TouchdownRobustPeakG = impact.RobustPeakG;
        diagnostics.TouchdownPeakGSubscore = gScore;
        diagnostics.TouchdownImpactScore = score;
        diagnostics.ImpactTelemetryDegraded = impact.TelemetryDegraded;

        var degraded = impact.TelemetryDegraded;
        var totalWeight = vsWeight + gWeight;
        var explanation =
            $"Initial main-gear impact only. Sink rate {impact.VerticalSpeedFpm:0} fpm — {vsScore:0.#}%; " +
            $"robust peak {impact.RobustPeakG:0.00} g — {gScore:0.#}% " +
            $"(raw peak {impact.RawPeakG:0.00} g, {impact.ValidPostContactSamples} samples). " +
            (impact.TouchdownNormalVelocityFpm is { } normal
                ? $"MSFS ground-normal rate {normal:0} fpm is retained as an impact diagnostic and is not scored. "
                : "MSFS ground-normal impact rate was unavailable. ") +
            (degraded
                ? "Degraded fallback: vertical-speed subscore only."
                : $"Combined with weighted RMS penalties ({vsWeight / totalWeight:P0} vertical speed / " +
                  $"{gWeight / totalWeight:P0} G effective weights). All later impacts are excluded.");
        return new CompositeMetricEvaluation(
            true, score, score, "%", explanation, degraded, impact.DegradedReason);
    }

    private static CompositeMetricEvaluation EvaluateFlare(
        EvaluationMetric metric,
        LandingSnapshot snapshot,
        LandingResultDiagnostics diagnostics)
    {
        if (snapshot.FloatAnalysis is not { } analysis)
            return CompositeMetricEvaluation.Unavailable("Flare/float analysis is not complete.");

        var distanceScore = Curve(metric, "distance", analysis.DistanceMetres);
        var timeScore = Curve(metric, "time", analysis.DurationSeconds);
        var positiveScore = Curve(metric, "positiveVerticalSpeedSeconds", analysis.PositiveVerticalSpeedSeconds);
        var score = analysis.CoverageSufficient
            ? CompositeScoringMath.CombineScoresByPenaltyRms(
                (distanceScore, Param(metric, "distanceWeight")),
                (timeScore, Param(metric, "timeWeight")),
                (positiveScore, Param(metric, "positiveVerticalSpeedWeight")))
            : 100;

        diagnostics.FloatDetected = analysis.Detected;
        diagnostics.FloatStartTimeSeconds = analysis.StartTimeSeconds;
        diagnostics.FloatSeconds = analysis.DurationSeconds;
        diagnostics.FloatDistanceM = analysis.DistanceMetres;
        diagnostics.PositiveVerticalSpeedSeconds = analysis.PositiveVerticalSpeedSeconds;
        diagnostics.MaximumPositiveVerticalSpeedFpm = analysis.MaximumPositiveVerticalSpeedFpm;
        diagnostics.FloatDistanceSubscore = distanceScore;
        diagnostics.FloatTimeSubscore = timeScore;
        diagnostics.PositiveVerticalSpeedSubscore = positiveScore;
        diagnostics.FlareEfficiencyScore = score;
        diagnostics.FlareTelemetryDegraded = !analysis.CoverageSufficient;

        var explanation = analysis.Detected
            ? $"Float distance {analysis.DistanceMetres:0} m — {distanceScore:0.#}%; " +
              $"duration {analysis.DurationSeconds:0.0} s — {timeScore:0.#}%; positive vertical speed " +
              $"{analysis.PositiveVerticalSpeedSeconds:0.00} s — {positiveScore:0.#}% " +
              $"(maximum +{analysis.MaximumPositiveVerticalSpeedFpm:0} fpm). IAS and excess speed are not reused."
            : analysis.CoverageSufficient
                ? "No sustained float or balloon was detected below flare height; score 100%."
                : "Flare coverage was insufficient; neutral 100% is shown only as an unranked degraded fallback.";
        return new CompositeMetricEvaluation(
            true, score, score, "%", explanation,
            !analysis.CoverageSufficient, analysis.DegradedReason);
    }

    private static CompositeMetricEvaluation EvaluateContact(
        EvaluationMetric metric,
        LandingEvaluationKey key,
        LandingSnapshot snapshot,
        LandingResultDiagnostics diagnostics)
    {
        if (snapshot.ContactStability is not { } analysis)
            return CompositeMetricEvaluation.Unavailable("Contact-stability analysis is not complete.");
        if (!analysis.CoverageSufficient)
        {
            diagnostics.ContactTelemetryDegraded = true;
            diagnostics.ContactStabilityScore = 100;
            return new CompositeMetricEvaluation(
                true, 100, 100, "%",
                "Independent main-gear coverage was insufficient; neutral 100% is shown only as an unranked degraded fallback.",
                true, analysis.DegradedReason);
        }

        diagnostics.BounceCount = analysis.BounceCount;
        diagnostics.MaximumBounceAirborneSeconds = analysis.MaximumAirborneDurationSeconds;
        if (analysis.BounceCount == 0)
        {
            diagnostics.ContactStabilityScore = 100;
            return new CompositeMetricEvaluation(
                true, 100, 100, "%", "No valid bounce detected. Contact stability score 100%.",
                false, null);
        }

        var impactMetric = key.Phases.SelectMany(p => p.Metrics)
            .Single(m => m.Id.Equals("touchdown_impact", StringComparison.OrdinalIgnoreCase));
        var secondary = new List<(BounceEvent Event, double Score)>();
        foreach (var bounce in analysis.Bounces)
        {
            var vsScore = Curve(impactMetric, "verticalSpeed", bounce.VerticalSpeedAtRecontactFpm);
            var gScore = Curve(impactMetric, "peakG", bounce.RobustPeakGAtRecontact);
            var impactScore = bounce.ImpactTelemetryDegraded
                ? vsScore
                : CompositeScoringMath.CombineScoresByPenaltyRms(
                    (vsScore, Param(impactMetric, "verticalSpeedWeight")),
                    (gScore, Param(impactMetric, "peakGWeight")));
            secondary.Add((bounce, impactScore));
            diagnostics.Bounces.Add(new ScoredBounceDiagnostic
            {
                AirborneStartTimeSeconds = bounce.AirborneStartTimeSeconds,
                RecontactTimeSeconds = bounce.RecontactTimeSeconds,
                AirborneDurationSeconds = bounce.AirborneDurationSeconds,
                VerticalSpeedAtRecontactFpm = bounce.VerticalSpeedAtRecontactFpm,
                RobustPeakGAtRecontact = bounce.RobustPeakGAtRecontact,
                SecondaryImpactScore = impactScore
            });
        }

        var worst = secondary.MinBy(x => x.Score);
        var countScore = Curve(metric, "count", analysis.BounceCount);
        var durationScore = Curve(metric, "maxAirborneDuration", analysis.MaximumAirborneDurationSeconds);
        var score = CompositeScoringMath.CombineScoresByPenaltyRms(
            (countScore, Param(metric, "countWeight")),
            (durationScore, Param(metric, "maxAirborneDurationWeight")),
            (worst.Score, Param(metric, "worstSecondaryImpactWeight")));
        var degraded = secondary.Any(x => x.Event.ImpactTelemetryDegraded);

        diagnostics.WorstSecondaryTouchdownVerticalSpeedFpm = worst.Event.VerticalSpeedAtRecontactFpm;
        diagnostics.BounceCountSubscore = countScore;
        diagnostics.MaximumBounceAirborneDurationSubscore = durationScore;
        diagnostics.WorstSecondaryRawPeakG = worst.Event.RawPeakGAtRecontact;
        diagnostics.WorstSecondaryRobustPeakG = worst.Event.RobustPeakGAtRecontact;
        diagnostics.WorstSecondaryImpactScore = worst.Score;
        diagnostics.ContactStabilityScore = score;
        diagnostics.ContactTelemetryDegraded = degraded;

        var explanation =
            $"{analysis.BounceCount} valid bounce{(analysis.BounceCount == 1 ? "" : "s")} — {countScore:0.#}%; " +
            $"maximum airborne {analysis.MaximumAirborneDurationSeconds:0.00} s — {durationScore:0.#}%; " +
            $"worst secondary impact {worst.Score:0.#}% " +
            $"({worst.Event.VerticalSpeedAtRecontactFpm:0} fpm, {worst.Event.RobustPeakGAtRecontact:0.00} g).";
        return new CompositeMetricEvaluation(
            true, score, score, "%", explanation, degraded,
            degraded ? analysis.DegradedReason : null);
    }

    private static CompositeMetricEvaluation EvaluateCrabAngle(
        EvaluationMetric metric,
        LandingSnapshot snapshot,
        LandingResultDiagnostics diagnostics)
    {
        if (snapshot.CrabAngle is not { CoverageSufficient: true } analysis)
            return CompositeMetricEvaluation.Unavailable(
                snapshot.CrabAngle?.DegradedReason
                ?? "Crab-angle analysis is not complete.");

        var touchdownScore = Curve(metric, "touchdown", analysis.TouchdownErrorDeg);
        var threeSecondScore = Curve(
            metric, "threeSecondIntegral", analysis.IntegratedDeviationDegSeconds);
        var touchdownWeight = Param(metric, "touchdownWeight");
        var threeSecondWeight = Param(metric, "threeSecondWeight");
        var totalWeight = touchdownWeight + threeSecondWeight;
        var score = (touchdownScore * touchdownWeight
                     + threeSecondScore * threeSecondWeight) / totalWeight;

        diagnostics.CrabAngleTouchdownDeg = analysis.TouchdownErrorDeg;
        diagnostics.CrabAngleTouchdownSubscore = touchdownScore;
        diagnostics.CrabAngleThreeSecondIntegralDegSeconds =
            analysis.IntegratedDeviationDegSeconds;
        diagnostics.CrabAngleThreeSecondSubscore = threeSecondScore;
        diagnostics.CrabAngleScore = score;
        diagnostics.CrabAngleTelemetryDegraded = false;

        var explanation =
            $"Crab at main-gear touchdown {analysis.TouchdownErrorDeg:0.0}° — {touchdownScore:0.#}%; " +
            $"absolute deviation integrated through TD+3 s {analysis.IntegratedDeviationDegSeconds:0.00} °·s " +
            $"(mean {analysis.MeanAbsoluteDeviationDeg:0.00}°, peak {analysis.PeakDeviationDeg:0.0}°) — " +
            $"{threeSecondScore:0.#}%. Combined {touchdownWeight / totalWeight:P0} touchdown / " +
            $"{threeSecondWeight / totalWeight:P0} three-second control.";

        return new CompositeMetricEvaluation(
            true,
            score,
            analysis.TouchdownErrorDeg,
            "deg",
            explanation,
            false,
            null);
    }

    private static CompositeMetricEvaluation EvaluateRunwayAlignment(
        EvaluationMetric metric,
        LandingSnapshot snapshot,
        LandingResultDiagnostics diagnostics)
    {
        if (snapshot.RunwayAlignment is not { CoverageSufficient: true } analysis)
            return CompositeMetricEvaluation.Unavailable(
                snapshot.RunwayAlignment?.DegradedReason
                ?? "Runway-alignment analysis is not complete.");

        var headingTouchdownScore = Curve(
            metric, "touchdown", Math.Abs(analysis.TouchdownHeadingErrorDeg));
        var trackTouchdownScore = Curve(
            metric, "touchdown", Math.Abs(analysis.TouchdownTrackErrorDeg));
        var touchdownScore = CompositeScoringMath.CombineScoresByPenaltyRms(
            (headingTouchdownScore, 0.5),
            (trackTouchdownScore, 0.5));

        var headingThreeSecondScore = Curve(
            metric, "threeSecondIntegral", analysis.IntegratedHeadingDeviationDegSeconds);
        var trackThreeSecondScore = Curve(
            metric, "threeSecondIntegral", analysis.IntegratedTrackDeviationDegSeconds);
        var threeSecondScore = CompositeScoringMath.CombineScoresByPenaltyRms(
            (headingThreeSecondScore, 0.5),
            (trackThreeSecondScore, 0.5));

        var touchdownWeight = Param(metric, "touchdownWeight");
        var threeSecondWeight = Param(metric, "threeSecondWeight");
        var totalWeight = touchdownWeight + threeSecondWeight;
        var score = (touchdownScore * touchdownWeight
                     + threeSecondScore * threeSecondWeight) / totalWeight;

        diagnostics.RunwayAlignmentHeadingTouchdownDeg = analysis.TouchdownHeadingErrorDeg;
        diagnostics.RunwayAlignmentTrackTouchdownDeg = analysis.TouchdownTrackErrorDeg;
        diagnostics.TouchdownTrueCrabAngleDeg = analysis.TouchdownTrueCrabAngleDeg;
        diagnostics.TouchdownGroundTrackTrueDeg = snapshot.TouchdownGroundTrackTrueDeg;
        diagnostics.TouchdownGroundTrackSource = analysis.GroundTrackSource;
        diagnostics.RunwayAlignmentHeadingTouchdownSubscore = headingTouchdownScore;
        diagnostics.RunwayAlignmentTrackTouchdownSubscore = trackTouchdownScore;
        diagnostics.RunwayAlignmentHeadingThreeSecondIntegralDegSeconds =
            analysis.IntegratedHeadingDeviationDegSeconds;
        diagnostics.RunwayAlignmentTrackThreeSecondIntegralDegSeconds =
            analysis.IntegratedTrackDeviationDegSeconds;
        diagnostics.RunwayAlignmentHeadingThreeSecondSubscore = headingThreeSecondScore;
        diagnostics.RunwayAlignmentTrackThreeSecondSubscore = trackThreeSecondScore;
        diagnostics.RunwayAlignmentScore = score;
        diagnostics.RunwayAlignmentTelemetryDegraded = false;

        var explanation =
            $"At main-gear touchdown: heading error {Signed(analysis.TouchdownHeadingErrorDeg)}° " +
            $"({headingTouchdownScore:0.#}%), track error {Signed(analysis.TouchdownTrackErrorDeg)}° " +
            $"({trackTouchdownScore:0.#}%); true crab heading−track {Signed(analysis.TouchdownTrueCrabAngleDeg)}° " +
            $"(informational, track: {analysis.GroundTrackSource}). Through TD+3 s: heading " +
            $"{analysis.IntegratedHeadingDeviationDegSeconds:0.00} °·s ({headingThreeSecondScore:0.#}%), " +
            $"track {analysis.IntegratedTrackDeviationDegSeconds:0.00} °·s ({trackThreeSecondScore:0.#}%). " +
            $"Heading and track use 50/50 penalty RMS inside each portion; portions remain " +
            $"{touchdownWeight / totalWeight:P0} touchdown / {threeSecondWeight / totalWeight:P0} TD+3-second control.";

        return new CompositeMetricEvaluation(
            true,
            score,
            Math.Max(Math.Abs(analysis.TouchdownHeadingErrorDeg), Math.Abs(analysis.TouchdownTrackErrorDeg)),
            "deg",
            explanation,
            false,
            null);
    }

    private static string Signed(double value) => value >= 0 ? $"+{value:0.00}" : $"{value:0.00}";

    private static double Param(EvaluationMetric metric, string name) => metric.Params[name];
    private static double Curve(EvaluationMetric metric, string name, double value) =>
        CompositeScoringMath.PiecewiseScorePercent(value, metric.Curves[name]);
}
