using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

internal static class GroundTrackCalculator
{
    internal const double MaximumBaselineSeconds = 1.0;
    internal const double MinimumBaselineMetres = 5.0;

    public static bool TryResolve(
        TelemetrySample current,
        IReadOnlyList<TelemetrySample> history,
        out double groundTrackTrueDeg,
        out string source)
    {
        if (current.GroundTrackTrueDeg is { } recorded && double.IsFinite(recorded))
        {
            groundTrackTrueDeg = Normalize360(recorded);
            source = "GPS GROUND TRUE TRACK";
            return true;
        }

        var currentTime = SampleTimeSeconds(current);
        if (!double.IsFinite(currentTime) || !HasFinitePosition(current))
        {
            groundTrackTrueDeg = 0;
            source = "";
            return false;
        }

        for (var i = history.Count - 1; i >= 0; i--)
        {
            var previous = history[i];
            var previousTime = SampleTimeSeconds(previous);
            var dt = currentTime - previousTime;
            if (!double.IsFinite(dt) || dt <= 0)
                continue;
            if (dt > MaximumBaselineSeconds)
                break;
            if (!HasFinitePosition(previous))
                continue;

            var distance = GeoUtil.HaversineMetersPublic(
                previous.Latitude, previous.Longitude, current.Latitude, current.Longitude);
            if (!double.IsFinite(distance) || distance < MinimumBaselineMetres)
                continue;

            groundTrackTrueDeg = GeoUtil.BearingDegrees(
                previous.Latitude, previous.Longitude, current.Latitude, current.Longitude);
            source = $"position-derived track ({distance:0.0} m / {dt:0.00} s)";
            return true;
        }

        groundTrackTrueDeg = 0;
        source = "";
        return false;
    }

    internal static double SampleTimeSeconds(TelemetrySample sample) =>
        double.IsFinite(sample.SimulationTimeSeconds)
            ? sample.SimulationTimeSeconds
            : sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0;

    internal static double NormalizeSigned(double degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees < -180) degrees += 360;
        return degrees;
    }

    private static double Normalize360(double degrees)
    {
        degrees %= 360;
        return degrees < 0 ? degrees + 360 : degrees;
    }

    private static bool HasFinitePosition(TelemetrySample sample) =>
        double.IsFinite(sample.Latitude)
        && double.IsFinite(sample.Longitude)
        && Math.Abs(sample.Latitude) <= 90
        && Math.Abs(sample.Longitude) <= 180;
}

/// <summary>
/// Sample-rate-independent heading and ground-track integration from touchdown
/// through TD+3 seconds.
/// </summary>
internal static class RunwayAlignmentCalculator
{
    internal const double WindowSeconds = 3.0;
    private const double MinimumCoverageSeconds = 2.9;
    private const double MaximumSampleGapSeconds = 1.25;
    private const int MinimumSampleCount = 4;

    public static RunwayAlignmentAnalysis Calculate(
        IReadOnlyList<TelemetrySample> rolloutSamples,
        double touchdownTimeSeconds,
        double runwayHeadingTrueDeg,
        double? touchdownGroundTrackTrueDeg,
        string touchdownGroundTrackSource)
    {
        if (rolloutSamples.Count == 0
            || !double.IsFinite(touchdownTimeSeconds)
            || !double.IsFinite(runwayHeadingTrueDeg))
            return Unavailable("Runway-alignment samples are unavailable.");

        var windowEnd = touchdownTimeSeconds + WindowSeconds;
        var points = new List<Point>();
        var history = new List<TelemetrySample>();
        foreach (var sample in rolloutSamples.OrderBy(GroundTrackCalculator.SampleTimeSeconds))
        {
            var time = GroundTrackCalculator.SampleTimeSeconds(sample);
            if (!double.IsFinite(time) || time < touchdownTimeSeconds - 1e-6)
                continue;

            double track;
            string source;
            if (points.Count == 0
                && touchdownGroundTrackTrueDeg is { } captured
                && double.IsFinite(captured))
            {
                track = captured;
                source = touchdownGroundTrackSource;
            }
            else if (!GroundTrackCalculator.TryResolve(sample, history, out track, out source))
            {
                history.Add(sample);
                continue;
            }

            if (!double.IsFinite(sample.HeadingTrueDeg))
            {
                history.Add(sample);
                continue;
            }

            points.Add(new Point(
                time,
                GroundTrackCalculator.NormalizeSigned(sample.HeadingTrueDeg - runwayHeadingTrueDeg),
                GroundTrackCalculator.NormalizeSigned(track - runwayHeadingTrueDeg),
                source));
            history.Add(sample);
            if (time >= windowEnd)
                break;
        }

        if (points.Count < 2 || points[0].TimeSeconds > touchdownTimeSeconds + 0.1)
            return Unavailable("The main-gear touchdown heading/track sample is unavailable.");

        var touchdown = points[0];
        var trueCrab = GroundTrackCalculator.NormalizeSigned(
            touchdown.HeadingErrorDeg - touchdown.TrackErrorDeg);
        double headingIntegral = 0;
        double trackIntegral = 0;
        double coverage = 0;
        var headingPeak = Math.Abs(touchdown.HeadingErrorDeg);
        var trackPeak = Math.Abs(touchdown.TrackErrorDeg);
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
            var endHeading = InterpolateSignedAngle(previous.HeadingErrorDeg, current.HeadingErrorDeg, fraction);
            var endTrack = InterpolateSignedAngle(previous.TrackErrorDeg, current.TrackErrorDeg, fraction);
            headingIntegral += IntegrateAbsoluteLinear(previous.HeadingErrorDeg, endHeading, dt);
            trackIntegral += IntegrateAbsoluteLinear(previous.TrackErrorDeg, endTrack, dt);
            coverage += dt;
            headingPeak = Math.Max(headingPeak, Math.Max(Math.Abs(previous.HeadingErrorDeg), Math.Abs(endHeading)));
            trackPeak = Math.Max(trackPeak, Math.Max(Math.Abs(previous.TrackErrorDeg), Math.Abs(endTrack)));
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
                ? $"Runway-alignment coverage reached only {coverage:0.00} of {WindowSeconds:0.0} seconds."
                : validSamples < MinimumSampleCount
                    ? $"Runway-alignment coverage has only {validSamples} valid heading/track samples."
                    : $"Runway-alignment telemetry gap exceeded {MaximumSampleGapSeconds:0.0} second.";

        return new RunwayAlignmentAnalysis(
            sufficient,
            touchdown.HeadingErrorDeg,
            touchdown.TrackErrorDeg,
            trueCrab,
            headingIntegral,
            trackIntegral,
            coverage,
            coverage > 0 ? headingIntegral / coverage : 0,
            coverage > 0 ? trackIntegral / coverage : 0,
            headingPeak,
            trackPeak,
            validSamples,
            touchdown.Source,
            reason);
    }

    private static RunwayAlignmentAnalysis Unavailable(string reason) =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", reason);

    private static double InterpolateSignedAngle(double start, double end, double fraction) =>
        GroundTrackCalculator.NormalizeSigned(
            start + GroundTrackCalculator.NormalizeSigned(end - start) * fraction);

    private static double IntegrateAbsoluteLinear(double start, double end, double duration)
    {
        var startAbs = Math.Abs(start);
        var endAbs = Math.Abs(end);
        if (start == 0 || end == 0 || Math.Sign(start) == Math.Sign(end))
            return 0.5 * (startAbs + endAbs) * duration;

        var zeroFraction = startAbs / (startAbs + endAbs);
        return 0.5 * startAbs * duration * zeroFraction
               + 0.5 * endAbs * duration * (1 - zeroFraction);
    }

    private readonly record struct Point(
        double TimeSeconds,
        double HeadingErrorDeg,
        double TrackErrorDeg,
        string Source);
}
