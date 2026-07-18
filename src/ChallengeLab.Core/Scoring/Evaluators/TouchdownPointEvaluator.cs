using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>Scores touchdown feet relative to the beginning of the aiming-point marker.</summary>
public sealed class TouchdownPointEvaluator : IEvaluator
{
    public static readonly TouchdownPointEvaluator Instance = new();

    public double Evaluate(double value, EvaluationMetric metric)
    {
        var p = metric.Params;
        return LandingScorer.Score(
                   aimingMarkerFt: 0,
                   touchdownFt: value,
                   idealNearOffsetFt: p["idealNearOffsetFt"],
                   idealFarOffsetFt: p["idealFarOffsetFt"],
                   shortSpanFt: p["shortSpanFt"],
                   longSpanFt: p["longSpanFt"])
               / 100.0;
    }
}
