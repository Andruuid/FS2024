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
        ValidateGear(key.Gates?.Gear, errors);

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

    private static void ValidateMetric(
        EvaluationMetric metric,
        string phasePath,
        HashSet<string> metricIds,
        List<string> errors)
    {
        var path = $"{phasePath} metric '{metric.Id}'";
        RequireText(metric.Id, $"{path}.id", errors);
        RequireText(metric.DisplayName, $"{path}.displayName", errors);
        RequireText(metric.Metric, $"{path}.metric", errors);
        RequireText(metric.Evaluator, $"{path}.evaluator", errors);
        if (!string.IsNullOrWhiteSpace(metric.Id) && !metricIds.Add(metric.Id))
            errors.Add($"Duplicate metric id '{metric.Id}'.");
        if (!IsPositiveFinite(metric.ImportancePercent))
            errors.Add($"{path}.importancePercent must be finite and greater than zero.");
        if (!string.IsNullOrWhiteSpace(metric.Metric) && !MetricResolver.IsKnownMetric(metric.Metric))
            errors.Add($"{path}.metric '{metric.Metric}' is unknown.");
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

    private static bool IsPositiveFinite(double value) => double.IsFinite(value) && value > 0;

    private static void RequireText(string value, string path, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) errors.Add($"{path} is required.");
    }
}
