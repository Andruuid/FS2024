using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

public interface IEvaluator
{
    /// <summary>Returns score in [0, 1].</summary>
    double Evaluate(double value, CriterionConfig criterion);
}

public static class EvaluatorFactory
{
    public static IEvaluator Create(string evaluatorType) => evaluatorType.ToLowerInvariant() switch
    {
        "target" => TargetEvaluator.Instance,
        "band" => BandEvaluator.Instance,
        "boolean" => BooleanEvaluator.Instance,
        "window" => RangeEvaluator.Instance, // window uses same bounds as range for final value
        _ => RangeEvaluator.Instance
    };
}
