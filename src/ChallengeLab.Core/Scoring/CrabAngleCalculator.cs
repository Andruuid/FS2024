using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>Sample-rate-independent crab-angle integration from touchdown through TD+3 seconds.</summary>
internal static class CrabAngleCalculator
{
    internal const double WindowSeconds = 3.0;
    private const double MinimumCoverageSeconds = 2.9;
    private const double MaximumSampleGapSeconds = 1.25;
    private const int MinimumSampleCount = 4;

    public static CrabAngleAnalysis Calculate(
        IReadOnlyList<TelemetrySample> rolloutSamples,
        double touchdownTimeSeconds,
        double runwayHeadingTrueDeg)
    {
        if (rolloutSamples.Count == 0
            || !double.IsFinite(touchdownTimeSeconds)
            || !double.IsFinite(runwayHeadingTrueDeg))
        {
            return Unavailable("Crab-angle samples are unavailable.");
        }

        var windowEnd = touchdownTimeSeconds + WindowSeconds;
        var points = new List<Point>();
        foreach (var sample in rolloutSamples.OrderBy(SampleTimeSeconds))
        {
            var time = SampleTimeSeconds(sample);
            if (!double.IsFinite(time) || time < touchdownTimeSeconds - 1e-6)
                continue;

            points.Add(new Point(
                time,
                Math.Abs(NormalizeHeading(sample.HeadingTrueDeg - runwayHeadingTrueDeg))));
            if (time >= windowEnd)
                break;
        }

        if (points.Count < 2 || points[0].TimeSeconds > touchdownTimeSeconds + 0.1)
            return Unavailable("The main-gear touchdown heading sample is unavailable.");

        var touchdownErrorDeg = points[0].AbsoluteErrorDeg;
        double integral = 0;
        double coverage = 0;
        var peak = touchdownErrorDeg;
        var validSamples = 1;
        var maximumGap = 0.0;

        for (var i = 1; i < points.Count; i++)
        {
            var previous = points[i - 1];
            var current = points[i];
            var rawDt = current.TimeSeconds - previous.TimeSeconds;
            if (rawDt <= 0)
                continue;

            maximumGap = Math.Max(maximumGap, rawDt);
            var segmentEnd = Math.Min(current.TimeSeconds, windowEnd);
            var dt = segmentEnd - previous.TimeSeconds;
            if (dt <= 0)
                continue;

            var fraction = dt / rawDt;
            var endError = previous.AbsoluteErrorDeg
                           + (current.AbsoluteErrorDeg - previous.AbsoluteErrorDeg) * fraction;
            integral += 0.5 * (previous.AbsoluteErrorDeg + endError) * dt;
            coverage += dt;
            peak = Math.Max(peak, Math.Max(previous.AbsoluteErrorDeg, endError));
            validSamples++;

            if (segmentEnd >= windowEnd - 1e-9)
                break;
        }

        var sufficient = coverage >= MinimumCoverageSeconds
                         && validSamples >= MinimumSampleCount
                         && maximumGap <= MaximumSampleGapSeconds;
        var reason = sufficient
            ? null
            : coverage < MinimumCoverageSeconds
                ? $"Crab-angle coverage reached only {coverage:0.00} of {WindowSeconds:0.0} seconds."
                : validSamples < MinimumSampleCount
                    ? $"Crab-angle coverage has only {validSamples} valid samples."
                    : $"Crab-angle telemetry gap exceeded {MaximumSampleGapSeconds:0.0} second.";

        return new CrabAngleAnalysis(
            sufficient,
            touchdownErrorDeg,
            integral,
            coverage,
            coverage > 0 ? integral / coverage : 0,
            peak,
            validSamples,
            reason);
    }

    private static CrabAngleAnalysis Unavailable(string reason) =>
        new(false, 0, 0, 0, 0, 0, 0, reason);

    private static double SampleTimeSeconds(TelemetrySample sample) =>
        double.IsFinite(sample.SimulationTimeSeconds)
            ? sample.SimulationTimeSeconds
            : sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0;

    private static double NormalizeHeading(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }

    private readonly record struct Point(double TimeSeconds, double AbsoluteErrorDeg);
}
