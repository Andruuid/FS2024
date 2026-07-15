using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Runway-local approach geometry used by approach metrics and live HUD feedback.
/// Nominal path: elevation + distance_nm × 318 ft (≈ 3° glideslope).
/// </summary>
public static class RunwayPathGeometry
{
    public const double EarthRadiusMeters = 6_371_000.0;
    public const double MetersPerNauticalMile = 1_852.0;
    public const double NominalPathFeetPerNauticalMile = 318.0;

    public readonly record struct PathState(
        double ApproachDistanceMeters,
        double ApproachDistanceNm,
        double LateralMeters,
        double ExpectedAltitudeFeet,
        double AltitudeErrorFeet);

    /// <summary>
    /// Project aircraft position onto the extended runway centerline.
    /// Positive approach distance = before threshold (on final).
    /// Positive altitude error = above the nominal 3° path.
    /// Positive lateral = right of centerline when looking along runway heading.
    /// </summary>
    public static bool TryGetState(
        double latitude,
        double longitude,
        double altitudeFeet,
        RunwayConfig runway,
        out PathState state)
    {
        state = default;
        if (!double.IsFinite(latitude)
            || !double.IsFinite(longitude)
            || !double.IsFinite(altitudeFeet)
            || !double.IsFinite(runway.ThresholdLatitude)
            || !double.IsFinite(runway.ThresholdLongitude)
            || !double.IsFinite(runway.HeadingTrueDeg)
            || !double.IsFinite(runway.ElevationFeet))
        {
            return false;
        }

        var referenceLatitudeRadians = runway.ThresholdLatitude * Math.PI / 180.0;
        var northMeters =
            (latitude - runway.ThresholdLatitude) * Math.PI / 180.0 * EarthRadiusMeters;
        var eastMeters =
            (longitude - runway.ThresholdLongitude) * Math.PI / 180.0
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

        state = new PathState(
            approachDistanceMeters,
            approachDistanceMeters / MetersPerNauticalMile,
            lateralMeters,
            expectedAltitudeFeet,
            altitudeFeet - expectedAltitudeFeet);
        return true;
    }

    public static bool TryGetState(TelemetrySample sample, RunwayConfig runway, out PathState state)
        => TryGetState(sample.Latitude, sample.Longitude, sample.AltitudeFeet, runway, out state);
}
