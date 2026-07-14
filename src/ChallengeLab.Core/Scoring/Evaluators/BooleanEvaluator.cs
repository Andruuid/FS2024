using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Params: expected (1 or 0). Value treated as true if abs(value - expected) &lt; 0.5 when expected is 0/1,
/// or value == expected for other numbers. Pass = 1, fail = 0 (or soft via failScore).
/// </summary>
public sealed class BooleanEvaluator : IEvaluator
{
    public static readonly BooleanEvaluator Instance = new();

    public double Evaluate(double value, CriterionConfig criterion)
    {
        var p = criterion.Params;
        var expected = p.TryGetValue("expected", out var e) ? e : 1.0;
        var failScore = p.TryGetValue("failScore", out var f) ? f : 0.0;
        var pass = Math.Abs(value - expected) < 0.5;
        return pass ? 1.0 : Math.Clamp(failScore, 0, 1);
    }
}
