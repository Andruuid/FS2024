using ChallengeLab.Core.Scoring;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Config;

public static class EvaluationKeyValidator
{
    private const double WeightTolerance = 0.01;

    public static IReadOnlyList<string> Validate(LandingEvaluationKey? key)
    {
        var errors = new List<string>();
        if (key is null)
        {
            errors.Add("Evaluation key is null.");
            return errors;
        }

        RequireText(key.Id, "id", errors);
        if (key.Version <= 0) errors.Add("version must be greater than zero.");
        ValidateSettle(key.Settle, errors);
        ValidateTiming(key.Timing, errors);
        ValidateSpeedTarget(key.SpeedTarget, errors);
        ValidateContactStabilityGate(key.Gates?.ContactStability, errors);
        ValidateStallWarningGate(key.Gates?.StallWarning, errors);
        ValidateGear(key.Gates?.Gear, errors);
        ValidateFlapsGate(key.Gates?.Flaps, errors);
        ValidateContactMapping(key.ContactMapping, errors);

        if (key.Phases.Count == 0)
            errors.Add("phases must contain at least one phase.");

        var phaseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metricIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var phase in key.Phases)
        {
            var phasePath = $"phase '{phase.Id}'";
            RequireText(phase.Id, $"{phasePath}.id", errors);
            RequireText(phase.DisplayName, $"{phasePath}.displayName", errors);
            if (!string.IsNullOrWhiteSpace(phase.Id) && !phaseIds.Add(phase.Id))
                errors.Add($"Duplicate phase id '{phase.Id}'.");
            if (!IsPositiveFinite(phase.WeightPercent))
                errors.Add($"{phasePath}.weightPercent must be finite and greater than zero.");
            if (phase.Metrics.Count == 0)
                errors.Add($"{phasePath}.metrics must contain at least one metric.");

            foreach (var metric in phase.Metrics)
                ValidateMetric(metric, phasePath, metricIds, errors);

            var metricWeight = phase.Metrics.Sum(m => m.ImportancePercent);
            if (phase.Metrics.Count > 0 && Math.Abs(metricWeight - 100) > WeightTolerance)
                errors.Add($"{phasePath} metric importance must total 100, but totals {metricWeight:0.###}.");
        }

        var phaseWeight = key.Phases.Sum(p => p.WeightPercent);
        if (key.Phases.Count > 0 && Math.Abs(phaseWeight - 100) > WeightTolerance)
            errors.Add($"Phase weights must total 100, but total {phaseWeight:0.###}.");

        return errors;
    }

    private static void ValidateContactStabilityGate(
        ContactStabilityGateConfig? gate,
        List<string> errors)
    {
        if (gate is null) return;

        if (!double.IsFinite(gate.OneBounceMultiplier)
            || gate.OneBounceMultiplier is < 0 or > 1)
            errors.Add("gates.contactStability.oneBounceMultiplier must be between 0 and 1.");
        if (!double.IsFinite(gate.TwoOrMoreBouncesMultiplier)
            || gate.TwoOrMoreBouncesMultiplier is < 0 or > 1)
            errors.Add("gates.contactStability.twoOrMoreBouncesMultiplier must be between 0 and 1.");
        if (double.IsFinite(gate.OneBounceMultiplier)
            && double.IsFinite(gate.TwoOrMoreBouncesMultiplier)
            && gate.TwoOrMoreBouncesMultiplier > gate.OneBounceMultiplier)
            errors.Add("gates.contactStability.twoOrMoreBouncesMultiplier must not exceed oneBounceMultiplier.");
    }

    private static void ValidateStallWarningGate(
        StallWarningGateConfig? gate,
        List<string> errors)
    {
        if (gate is null) return;

        if (!double.IsFinite(gate.MultiplierOnWarning)
            || gate.MultiplierOnWarning is < 0 or > 1)
            errors.Add("gates.stallWarning.multiplierOnWarning must be between 0 and 1.");
    }

    private static void ValidateMetric(
        EvaluationMetric metric,
        string phasePath,
        HashSet<string> metricIds,
        List<string> errors)
    {
        var path = $"{phasePath} metric '{metric.Id}'";
        RequireText(metric.Id, $"{path}.id", errors);
        RequireText(metric.DisplayName, $"{path}.displayName", errors);
        RequireText(metric.Evaluator, $"{path}.evaluator", errors);
        if (!string.IsNullOrWhiteSpace(metric.Id) && !metricIds.Add(metric.Id))
            errors.Add($"Duplicate metric id '{metric.Id}'.");
        if (!IsPositiveFinite(metric.ImportancePercent))
            errors.Add($"{path}.importancePercent must be finite and greater than zero.");
        var composite = EvaluatorFactory.IsComposite(metric.Evaluator);
        if (!composite)
        {
            RequireText(metric.Metric, $"{path}.metric", errors);
            if (!string.IsNullOrWhiteSpace(metric.Metric) && !MetricResolver.IsKnownMetric(metric.Metric))
                errors.Add($"{path}.metric '{metric.Metric}' is unknown.");
        }
        if (!string.IsNullOrWhiteSpace(metric.Evaluator) && !EvaluatorFactory.IsKnown(metric.Evaluator))
            errors.Add($"{path}.evaluator '{metric.Evaluator}' is unknown.");

        if (!EvaluatorFactory.IsKnown(metric.Evaluator)) return;
        switch (metric.Evaluator.Trim().ToLowerInvariant())
        {
            case "piecewise":
                ValidatePiecewise(metric, path, errors);
                break;
            case "target":
                ValidateExactParams(metric, path, errors, "ideal", "tolerance", "maxError");
                if (TryFinite(metric, "tolerance", out var tolerance) && tolerance < 0)
                    errors.Add($"{path}.params.tolerance must be at least zero.");
                if (TryFinite(metric, "maxError", out var maxError)
                    && TryFinite(metric, "tolerance", out tolerance)
                    && maxError <= tolerance)
                    errors.Add($"{path}.params.maxError must be greater than tolerance.");
                break;
            case "centerline":
                ValidateExactParams(metric, path, errors, "tolerance", "zeroAt", "exponent");
                if (TryFinite(metric, "tolerance", out var centerTolerance) && centerTolerance < 0)
                    errors.Add($"{path}.params.tolerance must be at least zero.");
                if (TryFinite(metric, "zeroAt", out var zeroAt)
                    && TryFinite(metric, "tolerance", out centerTolerance)
                    && zeroAt <= centerTolerance)
                    errors.Add($"{path}.params.zeroAt must be greater than tolerance.");
                if (TryFinite(metric, "exponent", out var exponent) && exponent <= 0)
                    errors.Add($"{path}.params.exponent must be greater than zero.");
                break;
            case "range":
                ValidateExactParams(metric, path, errors, "min", "max", "zeroBelow", "zeroAbove");
                if (TryFinite(metric, "min", out var min) && TryFinite(metric, "max", out var max) && max < min)
                    errors.Add($"{path}.params.max must be at least min.");
                if (TryFinite(metric, "zeroBelow", out var below) && TryFinite(metric, "min", out min) && below >= min)
                    errors.Add($"{path}.params.zeroBelow must be less than min.");
                if (TryFinite(metric, "zeroAbove", out var above) && TryFinite(metric, "max", out max) && above <= max)
                    errors.Add($"{path}.params.zeroAbove must be greater than max.");
                break;
            case "band":
                ValidateExactParams(metric, path, errors, "peakMin", "peakMax", "zeroScoreBelow", "zeroScoreAbove");
                break;
            case "boolean":
                ValidateAllowedParams(metric, path, errors, new[] { "expected", "failScore" }, "expected");
                if (TryFinite(metric, "failScore", out var failScore) && failScore is < 0 or > 1)
                    errors.Add($"{path}.params.failScore must be between 0 and 1.");
                break;
            case "landingimpact":
                ValidateComposite(metric, path, errors,
                    new[] { "verticalSpeedWeight", "peakGWeight" },
                    new[] { "verticalSpeed", "peakG" },
                    new[] { "verticalSpeedWeight", "peakGWeight" });
                break;
            case "flareefficiency":
                ValidateComposite(metric, path, errors,
                    new[] { "entryVerticalSpeedFpm", "minSustainSeconds", "distanceWeight", "timeWeight", "positiveVerticalSpeedWeight" },
                    new[] { "distance", "time", "positiveVerticalSpeedSeconds" },
                    new[] { "distanceWeight", "timeWeight", "positiveVerticalSpeedWeight" });
                if (TryFinite(metric, "minSustainSeconds", out var sustain) && sustain < 0)
                    errors.Add($"{path}.params.minSustainSeconds must be at least zero.");
                break;
            case "contactstability":
                ValidateComposite(metric, path, errors,
                    new[] { "countWeight", "maxAirborneDurationWeight", "worstSecondaryImpactWeight" },
                    new[] { "count", "maxAirborneDuration" },
                    new[] { "countWeight", "maxAirborneDurationWeight", "worstSecondaryImpactWeight" });
                break;
        }
    }

    private static void ValidateComposite(
        EvaluationMetric metric,
        string path,
        List<string> errors,
        IReadOnlyCollection<string> allowedParams,
        IReadOnlyCollection<string> requiredCurves,
        IReadOnlyCollection<string> componentWeights)
    {
        ValidateExactParams(metric, path, errors, allowedParams.ToArray());
        foreach (var name in requiredCurves)
        {
            if (!metric.Curves.TryGetValue(name, out var curve))
            {
                errors.Add($"{path}.curves.{name} is required.");
                continue;
            }
            ValidateCurve(curve, $"{path}.curves.{name}", errors);
        }
        foreach (var unknown in metric.Curves.Keys.Where(k => !requiredCurves.Contains(k, StringComparer.Ordinal)))
            errors.Add($"{path}.curves contains unknown key '{unknown}'.");

        double total = 0;
        foreach (var weight in componentWeights)
        {
            if (!TryFinite(metric, weight, out var value)) continue;
            if (value < 0) errors.Add($"{path}.params.{weight} must be nonnegative.");
            total += Math.Max(0, value);
        }
        if (componentWeights.All(w => TryFinite(metric, w, out _)) && total <= 0)
            errors.Add($"{path} component weights must contain at least one positive value.");
    }

    private static void ValidateCurve(IReadOnlyList<ScorePoint> points, string path, List<string> errors)
    {
        if (points.Count < 2)
        {
            errors.Add($"{path} must contain at least two control points.");
            return;
        }
        var values = new HashSet<double>();
        foreach (var point in points)
        {
            if (!double.IsFinite(point.V)) errors.Add($"{path} contains a non-finite v value.");
            if (!double.IsFinite(point.S) || point.S is < 0 or > 100)
                errors.Add($"{path} score s must be between 0 and 100.");
            if (!values.Add(point.V)) errors.Add($"{path} contains duplicate v value {point.V}.");
        }
    }

    private static void ValidatePiecewise(EvaluationMetric metric, string path, List<string> errors)
    {
        if (metric.Params.Count > 0)
            errors.Add($"{path}.params is not used by piecewise evaluators.");
        if (metric.Points is not { Count: >= 2 })
        {
            errors.Add($"{path}.points must contain at least two control points.");
            return;
        }

        var values = new HashSet<double>();
        foreach (var point in metric.Points)
        {
            if (!double.IsFinite(point.V)) errors.Add($"{path}.points contains a non-finite v value.");
            if (!double.IsFinite(point.S) || point.S is < 0 or > 100)
                errors.Add($"{path}.points score s must be between 0 and 100.");
            if (!values.Add(point.V)) errors.Add($"{path}.points contains duplicate v value {point.V}.");
        }
    }

    private static void ValidateExactParams(EvaluationMetric metric, string path, List<string> errors, params string[] required)
        => ValidateAllowedParams(metric, path, errors, required, required);

    private static void ValidateAllowedParams(
        EvaluationMetric metric,
        string path,
        List<string> errors,
        IReadOnlyCollection<string> allowed,
        params string[] required)
    {
        foreach (var name in required)
        {
            if (!metric.Params.TryGetValue(name, out var value))
                errors.Add($"{path}.params.{name} is required.");
            else if (!double.IsFinite(value))
                errors.Add($"{path}.params.{name} must be finite.");
        }

        foreach (var pair in metric.Params)
        {
            if (!allowed.Contains(pair.Key, StringComparer.Ordinal))
                errors.Add($"{path}.params contains unknown key '{pair.Key}'.");
            else if (!double.IsFinite(pair.Value))
                errors.Add($"{path}.params.{pair.Key} must be finite.");
        }
    }

    private static bool TryFinite(EvaluationMetric metric, string name, out double value)
        => metric.Params.TryGetValue(name, out value) && double.IsFinite(value);

    private static void ValidateSettle(EvaluationSettle? settle, List<string> errors)
    {
        if (settle is null) { errors.Add("settle is required."); return; }
        if (!IsPositiveFinite(settle.GroundSpeedKts)) errors.Add("settle.groundSpeedKts must be greater than zero.");
        if (!double.IsFinite(settle.HoldSeconds) || settle.HoldSeconds < 0) errors.Add("settle.holdSeconds must be at least zero.");
    }

    private static void ValidateTiming(EvaluationTiming? timing, List<string> errors)
    {
        if (timing is null) { errors.Add("timing is required."); return; }
        if (!IsPositiveFinite(timing.GroundTrackWindowBeforeSeconds)) errors.Add("timing.groundTrackWindowBeforeSeconds must be greater than zero.");
        if (!IsPositiveFinite(timing.GroundTrackWindowAfterSeconds)) errors.Add("timing.groundTrackWindowAfterSeconds must be greater than zero.");
        if (!double.IsFinite(timing.PostTouchdownAlignmentDelaySeconds) || timing.PostTouchdownAlignmentDelaySeconds < 0)
            errors.Add("timing.postTouchdownAlignmentDelaySeconds must be at least zero.");
        if (!IsPositiveFinite(timing.FlareAglFeet)) errors.Add("timing.flareAglFeet must be greater than zero.");
        if (!double.IsFinite(timing.PostArmIgnoreSeconds) || timing.PostArmIgnoreSeconds < 0)
            errors.Add("timing.postArmIgnoreSeconds must be at least zero.");
        if (!double.IsFinite(timing.MinAirborneAglFeet) || timing.MinAirborneAglFeet < 0)
            errors.Add("timing.minAirborneAglFeet must be at least zero.");
        if (timing.MinAirborneSamples < 0)
            errors.Add("timing.minAirborneSamples must be at least zero.");
        if (!double.IsFinite(timing.ApproachPathMinDistNm) || timing.ApproachPathMinDistNm < 0)
            errors.Add("timing.approachPathMinDistNm must be at least zero.");
        if (!double.IsFinite(timing.ApproachPathMaxDistNm) || timing.ApproachPathMaxDistNm <= 0)
            errors.Add("timing.approachPathMaxDistNm must be greater than zero.");
        if (timing.ApproachPathMaxDistNm <= timing.ApproachPathMinDistNm)
            errors.Add("timing.approachPathMaxDistNm must be greater than approachPathMinDistNm.");
        if (!double.IsFinite(timing.ImpactPreWindowSeconds) || timing.ImpactPreWindowSeconds < 0)
            errors.Add("timing.impactPreWindowSeconds must be at least zero.");
        if (!IsPositiveFinite(timing.ImpactWindowSeconds))
            errors.Add("timing.impactWindowSeconds must be greater than zero.");
        if (!IsPositiveFinite(timing.ImpactFilterCutoffHz))
            errors.Add("timing.impactFilterCutoffHz must be greater than zero.");
        if (!double.IsFinite(timing.ImpactPeakQuantile) || timing.ImpactPeakQuantile is < 0 or > 1)
            errors.Add("timing.impactPeakQuantile must be between zero and one.");
        if (timing.MinImpactSamples < 1)
            errors.Add("timing.minImpactSamples must be at least one.");
        if (!IsPositiveFinite(timing.BounceMinAirborneSeconds))
            errors.Add("timing.bounceMinAirborneSeconds must be greater than zero.");
        if (!IsPositiveFinite(timing.BounceWindowSeconds))
            errors.Add("timing.bounceWindowSeconds must be greater than zero.");
    }

    private static void ValidateSpeedTarget(EvaluationSpeedTarget? speed, List<string> errors)
    {
        if (speed is null) { errors.Add("speedTarget is required."); return; }
        if (!double.IsFinite(speed.DefaultVappKts) || speed.DefaultVappKts is <= 50 or >= 250)
            errors.Add("speedTarget.defaultVappKts must be between 50 and 250.");
        if (!double.IsFinite(speed.TouchdownOffsetKts) || speed.TouchdownOffsetKts is < 0 or >= 50)
            errors.Add("speedTarget.touchdownOffsetKts must be between 0 and 50.");
        if (!double.IsFinite(speed.Vs0Factor) || speed.Vs0Factor <= 1)
            errors.Add("speedTarget.vs0Factor must be greater than 1.");
    }

    private static void ValidateGear(GearGateConfig? gear, List<string> errors)
    {
        if (gear is null) { errors.Add("gates.gear is required."); return; }
        if (!double.IsFinite(gear.MultiplierOnFail) || gear.MultiplierOnFail is <= 0 or > 1)
            errors.Add("gates.gear.multiplierOnFail must be greater than 0 and at most 1.");
    }

    private static void ValidateFlapsGate(FlapsGateConfig? flaps, List<string> errors)
    {
        // Optional for older keys; when present must be well-formed.
        if (flaps is null) return;
        if (!double.IsFinite(flaps.MinIndex) || flaps.MinIndex < 0)
            errors.Add("gates.flaps.minIndex must be at least zero.");
        if (!double.IsFinite(flaps.MaxIndex) || flaps.MaxIndex < flaps.MinIndex)
            errors.Add("gates.flaps.maxIndex must be >= minIndex.");
        if (!double.IsFinite(flaps.MultiplierOnFail) || flaps.MultiplierOnFail is <= 0 or > 1)
            errors.Add("gates.flaps.multiplierOnFail must be greater than 0 and at most 1.");
    }

    private static void ValidateContactMapping(LandingContactMapping? mapping, List<string> errors)
    {
        if (mapping is null) { errors.Add("contactMapping is required."); return; }
        foreach (var (name, value) in new[]
                 {
                     ("leftMainGearIndex", mapping.LeftMainGearIndex),
                     ("rightMainGearIndex", mapping.RightMainGearIndex),
                     ("noseGearIndex", mapping.NoseGearIndex)
                 })
            if (value is < 0 or > 15) errors.Add($"contactMapping.{name} must be between 0 and 15.");
        if (mapping.LeftMainGearIndex == mapping.RightMainGearIndex)
            errors.Add("contactMapping left and right main gear indices must differ.");
    }

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static void RequireText(string value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{path} is required.");
    }
}
