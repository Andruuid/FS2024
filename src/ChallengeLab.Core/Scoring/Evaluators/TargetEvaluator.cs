using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Peak score at ideal, full score within tolerance, falloff to zero at maxError.
/// Params: ideal, tolerance, maxError
/// </summary>
public sealed class TargetEvaluator : IEvaluator
{
    public static readonly TargetEvaluator Instance = new();

    public double Evaluate(double value, CriterionConfig criterion)
    {
        var p = criterion.Params;
        var ideal = Get(p, "ideal", 0);
        var tolerance = Math.Max(0.0001, Get(p, "tolerance", 1));
        var maxError = Math.Max(tolerance, Get(p, "maxError", tolerance * 3));

        var error = Math.Abs(value - ideal);
        if (error <= tolerance) return 1.0;
        if (error >= maxError) return 0;
        return 1.0 - (error - tolerance) / (maxError - tolerance);
    }

    private static double Get(Dictionary<string, double> p, string key, double fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;
}
