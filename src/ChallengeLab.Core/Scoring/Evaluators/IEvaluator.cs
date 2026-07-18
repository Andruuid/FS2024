using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

public interface IEvaluator
{
    /// <summary>Returns score in [0, 1].</summary>
    double Evaluate(double value, EvaluationMetric metric);
}

public static class EvaluatorFactory
{
    private static readonly HashSet<string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "target", "band", "piecewise", "upperboundbands", "centerline", "boolean", "range",
        "touchdownpoint", "landingimpact", "flareefficiency", "contactstability", "crabangle",
        "runwayalignment"
    };

    public static bool IsKnown(string evaluatorType) => KnownTypes.Contains(Normalize(evaluatorType));

    public static bool IsComposite(string evaluatorType) => Normalize(evaluatorType) is
        "landingimpact" or "flareefficiency" or "contactstability" or "crabangle" or "runwayalignment";

    public static IEvaluator Create(string evaluatorType) => evaluatorType.ToLowerInvariant() switch
    {
        "target" => TargetEvaluator.Instance,
        "band" => BandEvaluator.Instance,
        "piecewise" => PiecewiseEvaluator.Instance,
        "upperboundbands" => UpperBoundBandsEvaluator.Instance,
        "centerline" => CenterlineEvaluator.Instance,
        "boolean" => BooleanEvaluator.Instance,
        "range" => RangeEvaluator.Instance,
        "touchdownpoint" => TouchdownPointEvaluator.Instance,
        _ => throw new ArgumentException($"Unknown evaluator '{evaluatorType}'.", nameof(evaluatorType))
    };

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();
}
