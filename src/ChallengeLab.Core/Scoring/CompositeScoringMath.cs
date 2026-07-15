using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scoring;

public static class CompositeScoringMath
{
    public static double PiecewiseScorePercent(double value, IReadOnlyList<ScorePoint> raw)
    {
        if (raw.Count == 0) return 0;
        var points = raw.OrderBy(p => p.V).ToArray();
        if (value <= points[0].V) return Math.Clamp(points[0].S, 0, 100);
        if (value >= points[^1].V) return Math.Clamp(points[^1].S, 0, 100);

        for (var i = 0; i < points.Length - 1; i++)
        {
            var a = points[i];
            var b = points[i + 1];
            if (value < a.V || value > b.V) continue;
            var t = (value - a.V) / (b.V - a.V);
            return Math.Clamp(a.S + t * (b.S - a.S), 0, 100);
        }

        return 0;
    }

    public static double CombineScoresByPenaltyRms(
        params (double Score, double Weight)[] components)
    {
        if (components is null || components.Length == 0)
            throw new ArgumentException("At least one component is required.", nameof(components));
        if (components.Any(x => !double.IsFinite(x.Weight) || x.Weight < 0))
            throw new ArgumentException("Component weights must be finite and nonnegative.", nameof(components));

        var totalWeight = components.Sum(x => x.Weight);
        if (!double.IsFinite(totalWeight) || totalWeight <= 0)
            throw new ArgumentException("At least one positive weight is required.", nameof(components));

        double sum = 0;
        foreach (var (rawScore, rawWeight) in components)
        {
            var score = Math.Clamp(double.IsFinite(rawScore) ? rawScore : 0, 0, 100);
            var penalty = 1.0 - score / 100.0;
            sum += rawWeight / totalWeight * penalty * penalty;
        }

        return 100.0 * (1.0 - Math.Clamp(Math.Sqrt(sum), 0, 1));
    }

    public static double[] ZeroPhaseOnePoleLowPass(
        IReadOnlyList<double> values,
        IReadOnlyList<double> times,
        double requestedCutoffHz)
    {
        if (values.Count != times.Count)
            throw new ArgumentException("Value and timestamp counts differ.");
        if (values.Count == 0) return Array.Empty<double>();

        var intervals = Enumerable.Range(1, times.Count - 1)
            .Select(i => times[i] - times[i - 1])
            .Where(dt => double.IsFinite(dt) && dt > 0.0001)
            .OrderBy(dt => dt)
            .ToArray();
        if (intervals.Length == 0) return values.ToArray();

        var medianDt = intervals[intervals.Length / 2];
        var cutoff = Math.Min(requestedCutoffHz, (1.0 / medianDt) * 0.25);
        if (!double.IsFinite(cutoff) || cutoff <= 0) return values.ToArray();

        var forward = OnePoleLowPass(values, times, cutoff);
        Array.Reverse(forward);
        var reversedTimes = new double[times.Count];
        for (var i = 0; i < times.Count; i++)
            reversedTimes[i] = times[^1] - times[times.Count - 1 - i];
        var backward = OnePoleLowPass(forward, reversedTimes, cutoff);
        Array.Reverse(backward);
        return backward;
    }

    public static double Quantile(IEnumerable<double> input, double quantile)
    {
        var sorted = input.Where(double.IsFinite).OrderBy(x => x).ToArray();
        if (sorted.Length == 0) return double.NaN;
        var position = Math.Clamp(quantile, 0, 1) * (sorted.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (position - lower) * (sorted[upper] - sorted[lower]);
    }

    private static double[] OnePoleLowPass(
        IReadOnlyList<double> values,
        IReadOnlyList<double> times,
        double cutoffHz)
    {
        var output = new double[values.Count];
        if (values.Count == 0) return output;
        output[0] = values[0];
        for (var i = 1; i < values.Count; i++)
        {
            var dt = Math.Max(0.0001, times[i] - times[i - 1]);
            var alpha = 1.0 - Math.Exp(-2.0 * Math.PI * cutoffHz * dt);
            output[i] = output[i - 1] + alpha * (values[i] - output[i - 1]);
        }
        return output;
    }
}
