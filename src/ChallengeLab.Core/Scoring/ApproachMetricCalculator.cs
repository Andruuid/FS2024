using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

internal readonly record struct ApproachMetricResult(
    int RawSampleCount,
    double DurationSeconds,
    double GroundDistanceMeters,
    double MeanAbsoluteErrorFeet,
    double MeanBelowGlideslopeDeg,
    double MeanAboveGlideslopeDeg,
    double WeightedGlideslopeDeviationDeg,
    double VerticalExcessVariationFeetPerSecond,
    double LateralExcessVariationIndex,
    double RootMeanSquareErrorFeet,
    double MeanAbsoluteBankDeg);

/// <summary>
/// Deterministic short-final metric calculation. Raw visual-frame telemetry is converted to
/// runway-local coordinates, split at discontinuities, resampled to 5 Hz, and lightly smoothed
/// before asymmetric angular glideslope deviation, diagnostic mean |alt error|,
/// bank stability (mean |bank|), and total-variation steadiness metrics are integrated.
/// </summary>
internal static class ApproachMetricCalculator
{
    // Being low on a nominal path is scored much more generously than being high,
    // where excess energy tends to produce a deep landing. Keeping this as a
    // weighted absolute deviation also prevents high/low excursions from cancelling.
    internal const double BelowGlideslopePenaltyFactor = 0.35;
    internal const double AboveGlideslopePenaltyFactor = 2.0;
    private const double MaximumRawGapSeconds = 2.0;
    private static readonly TimeSpan ResampleInterval = TimeSpan.FromSeconds(0.2); // 5 Hz

    public static ApproachMetricResult Calculate(
        IReadOnlyList<TelemetrySample> samples,
        RunwayConfig runway,
        double minimumDistanceNm,
        double maximumDistanceNm)
    {
        if (samples.Count == 0
            || !double.IsFinite(minimumDistanceNm)
            || !double.IsFinite(maximumDistanceNm)
            || maximumDistanceNm <= minimumDistanceNm)
        {
            return default;
        }

        var minimumDistanceMeters = minimumDistanceNm * RunwayPathGeometry.MetersPerNauticalMile;
        var maximumDistanceMeters = maximumDistanceNm * RunwayPathGeometry.MetersPerNauticalMile;
        var segments = BuildSegments(
            samples, runway, minimumDistanceMeters, maximumDistanceMeters,
            out var rawSampleCount);

        double durationSeconds = 0;
        double groundDistanceMeters = 0;
        double absoluteErrorIntegral = 0;
        double squaredErrorIntegral = 0;
        double absoluteBankIntegral = 0;
        double belowGlideslopeIntegralDegSeconds = 0;
        double aboveGlideslopeIntegralDegSeconds = 0;
        // Total path variation (not reversal-only): live preview and finals must react to
        // corrections / pumping / S-turns. One-way intercepts also cost, which matches
        // pilot expectation that "wild flying" pulls the score down during approach.
        double verticalTotalVariationFeet = 0;
        double lateralTotalVariationMeters = 0;

        foreach (var rawSegment in segments)
        {
            var points = Smooth(Resample(rawSegment));
            if (points.Count < 2)
                continue;

            for (var i = 1; i < points.Count; i++)
            {
                var previous = points[i - 1];
                var current = points[i];
                var dt = (current.Timestamp - previous.Timestamp).TotalSeconds;
                if (dt <= 0)
                    continue;

                durationSeconds += dt;
                var previousAngleErrorDeg = GlideslopeAngleErrorDeg(previous, runway.GlideslopeDeg);
                var currentAngleErrorDeg = GlideslopeAngleErrorDeg(current, runway.GlideslopeDeg);
                absoluteErrorIntegral +=
                    0.5 * (Math.Abs(previous.AltitudeErrorFeet) + Math.Abs(current.AltitudeErrorFeet)) * dt;
                squaredErrorIntegral +=
                    0.5 * (previous.AltitudeErrorFeet * previous.AltitudeErrorFeet
                           + current.AltitudeErrorFeet * current.AltitudeErrorFeet) * dt;
                absoluteBankIntegral +=
                    0.5 * (Math.Abs(previous.BankDeg) + Math.Abs(current.BankDeg)) * dt;
                belowGlideslopeIntegralDegSeconds += 0.5
                    * (Math.Max(0, -previousAngleErrorDeg) + Math.Max(0, -currentAngleErrorDeg))
                    * dt;
                aboveGlideslopeIntegralDegSeconds += 0.5
                    * (Math.Max(0, previousAngleErrorDeg) + Math.Max(0, currentAngleErrorDeg))
                    * dt;

                verticalTotalVariationFeet +=
                    Math.Abs(current.AltitudeErrorFeet - previous.AltitudeErrorFeet);
                lateralTotalVariationMeters +=
                    Math.Abs(current.LateralMeters - previous.LateralMeters);

                var alongDelta =
                    current.ApproachDistanceMeters - previous.ApproachDistanceMeters;
                var lateralDelta = current.LateralMeters - previous.LateralMeters;
                groundDistanceMeters += Math.Sqrt(
                    alongDelta * alongDelta + lateralDelta * lateralDelta);
            }
        }

        var meanAbsoluteErrorFeet = durationSeconds > 0
            ? absoluteErrorIntegral / durationSeconds
            : 0;
        var rootMeanSquareErrorFeet = durationSeconds > 0
            ? Math.Sqrt(squaredErrorIntegral / durationSeconds)
            : 0;
        var verticalVariationFeetPerSecond = durationSeconds > 0
            ? verticalTotalVariationFeet / durationSeconds
            : 0;
        var lateralWeaveIndex = groundDistanceMeters > 0
            ? lateralTotalVariationMeters / groundDistanceMeters
            : 0;
        var meanAbsoluteBankDeg = durationSeconds > 0
            ? absoluteBankIntegral / durationSeconds
            : 0;
        var meanBelowGlideslopeDeg = durationSeconds > 0
            ? belowGlideslopeIntegralDegSeconds / durationSeconds
            : 0;
        var meanAboveGlideslopeDeg = durationSeconds > 0
            ? aboveGlideslopeIntegralDegSeconds / durationSeconds
            : 0;
        var weightedGlideslopeDeviationDeg =
            meanBelowGlideslopeDeg * BelowGlideslopePenaltyFactor
            + meanAboveGlideslopeDeg * AboveGlideslopePenaltyFactor;

        return new ApproachMetricResult(
            rawSampleCount,
            durationSeconds,
            groundDistanceMeters,
            meanAbsoluteErrorFeet,
            meanBelowGlideslopeDeg,
            meanAboveGlideslopeDeg,
            weightedGlideslopeDeviationDeg,
            verticalVariationFeetPerSecond,
            lateralWeaveIndex,
            rootMeanSquareErrorFeet,
            meanAbsoluteBankDeg);
    }

    private static double GlideslopeAngleErrorDeg(
        ApproachPoint point,
        double targetGlideslopeDeg)
    {
        var target = RunwayPathGeometry.SanitizeGlideslopeDeg(targetGlideslopeDeg);
        var pathDistanceMeters = point.ApproachDistanceMeters
                                 + RunwayPathGeometry.GlideslopeAimPointOffsetMeters;
        var targetHeightMeters = Math.Tan(target * Math.PI / 180.0) * pathDistanceMeters;
        var measuredHeightMeters = targetHeightMeters
                                   + point.AltitudeErrorFeet * RunwayPathGeometry.MetersPerFoot;
        var measuredDeg = Math.Atan2(measuredHeightMeters, pathDistanceMeters) * 180.0 / Math.PI;
        return measuredDeg - target;
    }

    private static List<List<ApproachPoint>> BuildSegments(
        IReadOnlyList<TelemetrySample> samples,
        RunwayConfig runway,
        double minimumDistanceMeters,
        double maximumDistanceMeters,
        out int rawSampleCount)
    {
        var segments = new List<List<ApproachPoint>>();
        List<ApproachPoint>? currentSegment = null;
        rawSampleCount = 0;

        foreach (var sample in samples)
        {
            if (sample.SimOnGround
                || !TryCreatePoint(sample, runway, out var point)
                || point.ApproachDistanceMeters < minimumDistanceMeters
                || point.ApproachDistanceMeters > maximumDistanceMeters)
            {
                FinishSegment(ref currentSegment, segments);
                continue;
            }

            rawSampleCount++;
            if (currentSegment is null)
            {
                currentSegment = new List<ApproachPoint> { point };
                continue;
            }

            var dt = (point.Timestamp - currentSegment[^1].Timestamp).TotalSeconds;
            if (dt is <= 0 or > MaximumRawGapSeconds)
            {
                FinishSegment(ref currentSegment, segments);
                currentSegment = new List<ApproachPoint> { point };
                continue;
            }

            currentSegment.Add(point);
        }

        FinishSegment(ref currentSegment, segments);
        return segments;
    }

    private static void FinishSegment(
        ref List<ApproachPoint>? currentSegment,
        ICollection<List<ApproachPoint>> segments)
    {
        if (currentSegment is { Count: >= 2 })
            segments.Add(currentSegment);
        currentSegment = null;
    }

    private static bool TryCreatePoint(
        TelemetrySample sample,
        RunwayConfig runway,
        out ApproachPoint point)
    {
        point = default;
        if (!RunwayPathGeometry.TryGetState(sample, runway, out var path))
            return false;

        point = new ApproachPoint(
            ScoringTimestamp(sample),
            path.ApproachDistanceMeters,
            path.LateralMeters,
            path.AltitudeErrorFeet,
            sample.BankDeg);
        return true;
    }

    private static DateTimeOffset ScoringTimestamp(TelemetrySample sample) =>
        double.IsFinite(sample.SimulationTimeSeconds)
            ? DateTimeOffset.UnixEpoch.AddSeconds(sample.SimulationTimeSeconds)
            : sample.Timestamp;

    private static List<ApproachPoint> Resample(IReadOnlyList<ApproachPoint> rawPoints)
    {
        if (rawPoints.Count < 2)
            return rawPoints.ToList();

        var result = new List<ApproachPoint>();
        var first = rawPoints[0];
        var last = rawPoints[^1];
        result.Add(first);

        var rawIndex = 1;
        for (var timestamp = first.Timestamp + ResampleInterval;
             timestamp < last.Timestamp;
             timestamp += ResampleInterval)
        {
            while (rawIndex < rawPoints.Count && rawPoints[rawIndex].Timestamp < timestamp)
                rawIndex++;

            if (rawIndex >= rawPoints.Count)
                break;

            var previous = rawPoints[rawIndex - 1];
            var current = rawPoints[rawIndex];
            var sourceDuration = (current.Timestamp - previous.Timestamp).TotalSeconds;
            if (sourceDuration <= 0)
                continue;

            var fraction = (timestamp - previous.Timestamp).TotalSeconds / sourceDuration;
            result.Add(new ApproachPoint(
                timestamp,
                Lerp(previous.ApproachDistanceMeters, current.ApproachDistanceMeters, fraction),
                Lerp(previous.LateralMeters, current.LateralMeters, fraction),
                Lerp(previous.AltitudeErrorFeet, current.AltitudeErrorFeet, fraction),
                Lerp(previous.BankDeg, current.BankDeg, fraction)));
        }

        if (result[^1].Timestamp < last.Timestamp)
            result.Add(last);
        return result;
    }

    private static List<ApproachPoint> Smooth(IReadOnlyList<ApproachPoint> points)
    {
        if (points.Count < 3)
            return points.ToList();

        var result = new List<ApproachPoint>(points.Count) { points[0] };
        for (var i = 1; i < points.Count - 1; i++)
        {
            var previous = points[i - 1];
            var current = points[i];
            var next = points[i + 1];
            result.Add(current with
            {
                LateralMeters = (previous.LateralMeters + current.LateralMeters + next.LateralMeters) / 3.0,
                AltitudeErrorFeet =
                    (previous.AltitudeErrorFeet + current.AltitudeErrorFeet + next.AltitudeErrorFeet) / 3.0,
                BankDeg = (previous.BankDeg + current.BankDeg + next.BankDeg) / 3.0
            });
        }

        result.Add(points[^1]);
        return result;
    }

    private static double Lerp(double start, double end, double fraction) =>
        start + (end - start) * fraction;

    private readonly record struct ApproachPoint(
        DateTimeOffset Timestamp,
        double ApproachDistanceMeters,
        double LateralMeters,
        double AltitudeErrorFeet,
        double BankDeg);
}
