using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring.Evaluators;

public interface IEvaluator
{
    /// <summary>Returns score in [0, 1].</summary>
    double Evaluate(double value, CriterionConfig criterion, DifficultyLevel level = DifficultyLevel.Easy);
}

public static class EvaluatorFactory
{
    public static IEvaluator Create(string evaluatorType) => evaluatorType.ToLowerInvariant() switch
    {
        "target" => TargetEvaluator.Instance,
        "band" => BandEvaluator.Instance,
        "piecewise" or "zones" or "curve" => PiecewiseEvaluator.Instance,
        "centerline" or "lateral" => CenterlineEvaluator.Instance,
        "boolean" => BooleanEvaluator.Instance,
        "window" => RangeEvaluator.Instance,
        _ => RangeEvaluator.Instance
    };
}
