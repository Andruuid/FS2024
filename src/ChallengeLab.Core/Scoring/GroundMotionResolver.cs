using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Resolves the aircraft's actual course over the ground for wind-aware trajectory math.
/// GPS ground track is authoritative; heading is retained only as a compatibility fallback.
/// </summary>
public static class GroundMotionResolver
{
    public const string GpsGroundTrackSource = "GPS GROUND TRUE TRACK";
    public const string HeadingFallbackSource = "PLANE HEADING DEGREES TRUE fallback";

    public static bool TryResolveCourse(
        TelemetrySample sample,
        out GroundMotionCourse course)
    {
        ArgumentNullException.ThrowIfNull(sample);

        if (sample.GroundTrackTrueDeg is { } track && double.IsFinite(track))
        {
            course = new GroundMotionCourse(Normalize360(track), GpsGroundTrackSource, true);
            return true;
        }

        if (double.IsFinite(sample.HeadingTrueDeg))
        {
            course = new GroundMotionCourse(
                Normalize360(sample.HeadingTrueDeg),
                HeadingFallbackSource,
                false);
            return true;
        }

        course = default;
        return false;
    }

    /// <summary>Signed ground-speed component along the supplied true course.</summary>
    public static double? ProjectGroundSpeedAlong(
        TelemetrySample sample,
        double targetCourseTrueDeg)
    {
        if (!double.IsFinite(sample.GroundSpeedKts)
            || sample.GroundSpeedKts < 0
            || !double.IsFinite(targetCourseTrueDeg)
            || !TryResolveCourse(sample, out var course))
        {
            return null;
        }

        var errorRadians = NormalizeSigned(course.Degrees - targetCourseTrueDeg)
                           * Math.PI / 180.0;
        return sample.GroundSpeedKts * Math.Cos(errorRadians);
    }

    public static double NormalizeSigned(double degrees)
    {
        degrees %= 360.0;
        if (degrees > 180) degrees -= 360;
        if (degrees < -180) degrees += 360;
        return degrees;
    }

    private static double Normalize360(double degrees)
    {
        degrees %= 360.0;
        return degrees < 0 ? degrees + 360 : degrees;
    }
}

public readonly record struct GroundMotionCourse(
    double Degrees,
    string Source,
    bool UsesGroundTrack);
