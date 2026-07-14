using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Linear interpolation across ordered control points (metric value → score).
/// JSON convention: <c>s</c> is the metric score in percent 0–100
/// (e.g. <c>{ "v": -100, "s": 100 }</c> = perfect). Engine returns 0–1 for blending.
/// Legacy 0–1 fractions are still accepted if every point has s ≤ 1.
/// </summary>
public sealed class PiecewiseEvaluator : IEvaluator
{
    public static readonly PiecewiseEvaluator Instance = new();

    public double Evaluate(double value, CriterionConfig criterion, DifficultyLevel level = DifficultyLevel.Easy)
    {
        var points = Normalize(criterion.Points);
        if (points.Count == 0)
            return 0;

        if (points.Count == 1)
            return Clamp01(points[0].S);

        // Outside ends: clamp to end scores (hard landing / deep float stay at end score)
        if (value <= points[0].V)
            return Clamp01(points[0].S);
        if (value >= points[^1].V)
            return Clamp01(points[^1].S);

        for (var i = 0; i < points.Count - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (value >= a.V && value <= b.V)
            {
                if (Math.Abs(b.V - a.V) < 1e-9)
                    return Clamp01(b.S);
                var t = (value - a.V) / (b.V - a.V);
                return Clamp01(a.S + t * (b.S - a.S));
            }
        }

        return 0;
    }

    /// <summary>
    /// Convert control points to (v, score01). Preferred JSON: s = 0–100 percent.
    /// If any s &gt; 1, the whole series is treated as percent (÷100).
    /// If all s ≤ 1, treated as legacy 0–1 fractions.
    /// </summary>
    private static List<(double V, double S)> Normalize(List<ScorePoint>? raw)
    {
        if (raw is null || raw.Count == 0)
            return new List<(double, double)>();

        // Prefer 0–100% in JSON. If any control point uses s > 1, treat the series as percent.
        var asPercent = raw.Any(p => p.S > 1.0);
        return raw
            .Select(p => (V: p.V, S: asPercent ? p.S / 100.0 : p.S))
            .OrderBy(p => p.V)
            .ToList();
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
