using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Full score inside [min, max]; linear falloff to zero at zeroBelow / zeroAbove.
/// Params: min, max, zeroBelow (optional), zeroAbove (optional)
/// </summary>
public sealed class RangeEvaluator : IEvaluator
{
    public static readonly RangeEvaluator Instance = new();

    public double Evaluate(double value, EvaluationMetric metric)
    {
        var p = metric.Params;
        var min = Get(p, "min", 0);
        var max = Get(p, "max", 0);
        if (max < min) (min, max) = (max, min);

        var zeroBelow = Get(p, "zeroBelow", min - Math.Abs(max - min));
        var zeroAbove = Get(p, "zeroAbove", max + Math.Abs(max - min));

        if (value >= min && value <= max) return 1.0;
        if (value < min)
        {
            if (value <= zeroBelow) return 0;
            return Clamp01((value - zeroBelow) / (min - zeroBelow));
        }

        if (value >= zeroAbove) return 0;
        return Clamp01((zeroAbove - value) / (zeroAbove - max));
    }

    private static double Get(Dictionary<string, double> p, string key, double fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
