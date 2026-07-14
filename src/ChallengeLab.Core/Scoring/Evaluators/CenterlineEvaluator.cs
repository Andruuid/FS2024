using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Centerline position only (not heading).
/// Params: tolerance (full score band, m), zeroAt (0% at this offset, m), exponent (curve shape).
/// Defaults match the former strict curve: ±1.5 m / 10 m / 1.3.
/// </summary>
public sealed class CenterlineEvaluator : IEvaluator
{
    public static readonly CenterlineEvaluator Instance = new();

    public double Evaluate(double value, CriterionConfig criterion)
    {
        var (t, z, p) = ResolveParams(criterion);
        return CenterlineScore.Calculate01(value, t, z, p);
    }

    public static (double Tolerance, double ZeroAt, double Exponent) ResolveParams(CriterionConfig criterion)
    {
        var p = criterion.Params;
        // Prefer plain keys; fall back to legacy strict* names if present in old JSON.
        var t = Get(p, "tolerance", Get(p, "t", Get(p, "strictTolerance", Get(p, "strict_t", 1.5))));
        var z = Get(p, "zeroAt", Get(p, "z", Get(p, "strictZeroAt", Get(p, "strict_z", 10.0))));
        var exp = Get(p, "exponent", Get(p, "p", Get(p, "strictExponent", Get(p, "strict_p", 1.3))));
        return (t, z, exp);
    }

    private static double Get(Dictionary<string, double> p, string key, double fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;
}
