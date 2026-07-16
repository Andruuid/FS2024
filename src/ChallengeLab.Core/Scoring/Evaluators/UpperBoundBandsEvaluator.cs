using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Stepwise scoring where each point's V is an inclusive upper bound and S is
/// the score percent for values up to that bound. Values beyond the last bound score zero.
/// </summary>
public sealed class UpperBoundBandsEvaluator : IEvaluator
{
    public static readonly UpperBoundBandsEvaluator Instance = new();

    public double Evaluate(double value, EvaluationMetric metric)
    {
        if (!double.IsFinite(value) || metric.Points is not { Count: > 0 })
            return 0;

        foreach (var point in metric.Points)
        {
            if (value <= point.V)
                return Math.Clamp(point.S / 100.0, 0, 1);
        }

        return 0;
    }
}
