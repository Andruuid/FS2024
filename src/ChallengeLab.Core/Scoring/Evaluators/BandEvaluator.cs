using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Peak score inside [peakMin, peakMax]; linear falloff to zero outside toward zeroScoreBelow / zeroScoreAbove.
/// Used for firm-landing preference (e.g. -180..-80 fpm peak, butter and hard both low).
/// </summary>
public sealed class BandEvaluator : IEvaluator
{
    public static readonly BandEvaluator Instance = new();

    public double Evaluate(double value, EvaluationMetric metric)
    {
        var p = metric.Params;
        var peakMin = Get(p, "peakMin", -180);
        var peakMax = Get(p, "peakMax", -80);
        if (peakMax < peakMin) (peakMin, peakMax) = (peakMax, peakMin);

        var zeroBelow = Get(p, "zeroScoreBelow", peakMin - 200);
        var zeroAbove = Get(p, "zeroScoreAbove", peakMax + 80);

        if (value >= peakMin && value <= peakMax) return 1.0;

        if (value < peakMin)
        {
            if (value <= zeroBelow) return 0;
            return Clamp01((value - zeroBelow) / (peakMin - zeroBelow));
        }

        if (value >= zeroAbove) return 0;
        return Clamp01((zeroAbove - value) / (zeroAbove - peakMax));
    }

    private static double Get(Dictionary<string, double> p, string key, double fallback)
        => p.TryGetValue(key, out var v) ? v : fallback;

    private static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
}
