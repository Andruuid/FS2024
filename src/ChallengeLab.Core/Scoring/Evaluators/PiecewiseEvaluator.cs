using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Linear interpolation across ordered control points (value → score).
/// Ideal for multi-zone touchdown VS curves (firm ideal, float + hard penalties).
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

    private static List<(double V, double S)> Normalize(List<ScorePoint>? raw)
    {
        if (raw is null || raw.Count == 0)
            return new List<(double, double)>();

        // Allow scores entered as 0–100 percent
        var pts = raw
            .Select(p => (V: p.V, S: p.S > 1.0 ? p.S / 100.0 : p.S))
            .OrderBy(p => p.V)
            .ToList();

        return pts;
    }

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
