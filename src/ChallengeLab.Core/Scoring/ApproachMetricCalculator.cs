using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

internal readonly record struct ApproachMetricResult(
    int RawSampleCount,
    double DurationSeconds,
    double GroundDistanceMeters,
    double MeanAbsoluteErrorFeet,
    double VerticalExcessVariationFeetPerSecond,
    double LateralExcessVariationIndex,
    double RootMeanSquareErrorFeet);

/// <summary>
/// Deterministic short-final metric calculation. Raw visual-frame telemetry is converted to
/// runway-local coordinates, split at discontinuities, resampled to 5 Hz, and lightly smoothed
/// before the path-accuracy and reversal-only steadiness metrics are integrated.
/// </summary>
internal static class ApproachMetricCalculator
{
    private const double EarthRadiusMeters = 6_371_000.0;
    private const double MetersPerNauticalMile = 1_852.0;
    private const double NominalPathFeetPerNauticalMile = 318.0;
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

        var minimumDistanceMeters = minimumDistanceNm * MetersPerNauticalMile;
        var maximumDistanceMeters = maximumDistanceNm * MetersPerNauticalMile;
        var segments = BuildSegments(samples, runway, minimumDistanceMeters, maximumDistanceMeters,
            out var rawSampleCount);

        double durationSeconds = 0;
        double groundDistanceMeters = 0;
        double absoluteErrorIntegral = 0;
        double squaredErrorIntegral = 0;
        double verticalExcessVariationFeet = 0;
        double lateralExcessVariationMeters = 0;

        foreach (var rawSegment in segments)
        {
            var points = Smooth(Resample(rawSegment));
            if (points.Count < 2)
                continue;

            double segmentVerticalVariation = 0;
            double segmentLateralVariation = 0;

            for (var i = 1; i < points.Count; i++)
            {
                var previous = points[i - 1];
                var current = points[i];
                var dt = (current.Timestamp - previous.Timestamp).TotalSeconds;
                if (dt <= 0)
                    continue;

                durationSeconds += dt;
                absoluteErrorIntegral +=
                    0.5 * (Math.Abs(previous.AltitudeErrorFeet) + Math.Abs(current.AltitudeErrorFeet)) * dt;
                squaredErrorIntegral +=
                    0.5 * (previous.AltitudeErrorFeet * previous.AltitudeErrorFeet
                           + current.AltitudeErrorFeet * current.AltitudeErrorFeet) * dt;

                segmentVerticalVariation +=
                    Math.Abs(current.AltitudeErrorFeet - previous.AltitudeErrorFeet);
                segmentLateralVariation +=
                    Math.Abs(current.LateralMeters - previous.LateralMeters);

                var alongDelta =
                    current.ApproachDistanceMeters - previous.ApproachDistanceMeters;
                var lateralDelta = current.LateralMeters - previous.LateralMeters;
                groundDistanceMeters += Math.Sqrt(
                    alongDelta * alongDelta + lateralDelta * lateralDelta);
            }

            var verticalNetChange =
                Math.Abs(points[^1].AltitudeErrorFeet - points[0].AltitudeErrorFeet);
            verticalExcessVariationFeet +=
                Math.Max(0, segmentVerticalVariation - verticalNetChange);

            var lateralNetChange = Math.Abs(points[^1].LateralMeters - points[0].LateralMeters);
            lateralExcessVariationMeters +=
                Math.Max(0, segmentLateralVariation - lateralNetChange);
        }

        var meanAbsoluteErrorFeet = durationSeconds > 0
            ? absoluteErrorIntegral / durationSeconds
            : 0;
        var rootMeanSquareErrorFeet = durationSeconds > 0
            ? Math.Sqrt(squaredErrorIntegral / durationSeconds)
            : 0;
        var verticalExcessVariationFeetPerSecond = durationSeconds > 0
            ? verticalExcessVariationFeet / durationSeconds
            : 0;
        var lateralExcessVariationIndex = groundDistanceMeters > 0
            ? lateralExcessVariationMeters / groundDistanceMeters
            : 0;

        return new ApproachMetricResult(
            rawSampleCount,
            durationSeconds,
            groundDistanceMeters,
            meanAbsoluteErrorFeet,
            verticalExcessVariationFeetPerSecond,
            lateralExcessVariationIndex,
            rootMeanSquareErrorFeet);
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
        if (!double.IsFinite(sample.Latitude)
            || !double.IsFinite(sample.Longitude)
            || !double.IsFinite(sample.AltitudeFeet)
            || !double.IsFinite(runway.ThresholdLatitude)
            || !double.IsFinite(runway.ThresholdLongitude)
            || !double.IsFinite(runway.HeadingTrueDeg)
            || !double.IsFinite(runway.ElevationFeet))
        {
            return false;
        }

        var referenceLatitudeRadians = runway.ThresholdLatitude * Math.PI / 180.0;
        var northMeters =
            (sample.Latitude - runway.ThresholdLatitude) * Math.PI / 180.0 * EarthRadiusMeters;
        var eastMeters =
            (sample.Longitude - runway.ThresholdLongitude) * Math.PI / 180.0
            * EarthRadiusMeters * Math.Cos(referenceLatitudeRadians);
        var headingRadians = runway.HeadingTrueDeg * Math.PI / 180.0;

        var runwayAlongMeters =
            northMeters * Math.Cos(headingRadians) + eastMeters * Math.Sin(headingRadians);
        var approachDistanceMeters = -runwayAlongMeters;
        var lateralMeters =
            eastMeters * Math.Cos(headingRadians) - northMeters * Math.Sin(headingRadians);
        var expectedAltitudeFeet = runway.ElevationFeet
                                   + approachDistanceMeters / MetersPerNauticalMile
                                   * NominalPathFeetPerNauticalMile;

        point = new ApproachPoint(
            sample.Timestamp,
            approachDistanceMeters,
            lateralMeters,
            sample.AltitudeFeet - expectedAltitudeFeet);
        return true;
    }

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
                Lerp(previous.AltitudeErrorFeet, current.AltitudeErrorFeet, fraction)));
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
                    (previous.AltitudeErrorFeet + current.AltitudeErrorFeet + next.AltitudeErrorFeet) / 3.0
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
        double AltitudeErrorFeet);
}
