using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Centerline position only (not heading). Difficulty selects t / z / p:
/// Easy ±4 m / 18 m / 1.7 · Normal ±3 m / 15 m / 1.5 · Strict ±1.5 m / 10 m / 1.3
/// </summary>
public sealed class CenterlineEvaluator : IEvaluator
{
    public static readonly CenterlineEvaluator Instance = new();

    public double Evaluate(double value, CriterionConfig criterion, DifficultyLevel level = DifficultyLevel.Easy)
    {
        var (t, z, p) = ResolveParams(criterion, level);
        return CenterlineScore.Calculate01(value, t, z, p);
    }

    public static (double Tolerance, double ZeroAt, double Exponent) ResolveParams(
        CriterionConfig criterion,
        DifficultyLevel level)
    {
        var p = criterion.Params;
        // Defaults = Normal (used as fallback)
        var t = Get(p, "tolerance", Get(p, "t", 3.0));
        var z = Get(p, "zeroAt", Get(p, "z", 15.0));
        var exp = Get(p, "exponent", Get(p, "p", 1.5));

        // Per-level overrides: easyTolerance / strictTolerance, or nested keys
        switch (level)
        {
            case DifficultyLevel.Easy:
                t = Get(p, "easyTolerance", Get(p, "easy_t", 4.0));
                z = Get(p, "easyZeroAt", Get(p, "easy_z", 18.0));
                exp = Get(p, "easyExponent", Get(p, "easy_p", 1.7));
                break;
            case DifficultyLevel.Strict:
                t = Get(p, "strictTolerance", Get(p, "strict_t", 1.5));
                z = Get(p, "strictZeroAt", Get(p, "strict_z", 10.0));
                exp = Get(p, "strictExponent", Get(p, "strict_p", 1.3));
                break;
        }

        // Explicit level table still wins if present as normal defaults only when
        // no level-specific keys — already handled above.

        return (t, z, exp);
    }

    private static double Get(Dictionary<string, double> p, string key, double fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;
}
