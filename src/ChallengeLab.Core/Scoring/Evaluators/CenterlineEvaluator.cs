using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Centerline position only (not heading).
/// Params: tolerance (full score band, m), zeroAt (0% at this offset, m), exponent (curve shape).
/// Defaults match the evaluation key: ±3 m full / 22 m zero / 1.2 exponent.
/// </summary>
public sealed class CenterlineEvaluator : IEvaluator
{
    public static readonly CenterlineEvaluator Instance = new();

    public double Evaluate(double value, EvaluationMetric metric)
    {
        var (t, z, p) = ResolveParams(metric);
        return CenterlineScore.Calculate01(value, t, z, p);
    }

    public static (double Tolerance, double ZeroAt, double Exponent) ResolveParams(EvaluationMetric metric)
    {
        var p = metric.Params;
        var t = Get(p, "tolerance", 3.0);
        var z = Get(p, "zeroAt", 22.0);
        var exp = Get(p, "exponent", 1.2);
        return (t, z, exp);
    }

    private static double Get(Dictionary<string, double> p, string key, double fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;
}
