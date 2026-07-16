using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Runway-local approach geometry used by approach metrics and live HUD feedback.
/// Nominal path: 3° glideslope that reaches runway elevation at the aim point
/// (threshold + <see cref="GlideslopeAimPointOffsetFeet"/> along runway heading),
/// i.e. elevation + path_distance_nm × 318 ft.
/// </summary>
public static class RunwayPathGeometry
{
    public const double EarthRadiusMeters = 6_371_000.0;
    public const double MetersPerNauticalMile = 1_852.0;
    public const double MetersPerFoot = 0.3048;
    public const double NominalPathFeetPerNauticalMile = 318.0;

    /// <summary>
    /// Aim point past the threshold where the nominal 3° path meets field elevation.
    /// Distance windows and HUD range stay threshold-based; only altitude path uses this.
    /// </summary>
    public const double GlideslopeAimPointOffsetFeet = 1_200.0;

    public static double GlideslopeAimPointOffsetMeters =>
        GlideslopeAimPointOffsetFeet * MetersPerFoot;

    public static double GlideslopeAimPointOffsetNm =>
        GlideslopeAimPointOffsetMeters / MetersPerNauticalMile;

    public readonly record struct PathState(
        double ApproachDistanceMeters,
        double ApproachDistanceNm,
        double LateralMeters,
        double ExpectedAltitudeFeet,
        double AltitudeErrorFeet)
    {
        /// <summary>
        /// Along-track distance to the glideslope aim point (threshold + 1,200 ft).
        /// Positive = still before the aim point on final.
        /// </summary>
        public double GlideslopePathDistanceMeters =>
            ApproachDistanceMeters + GlideslopeAimPointOffsetMeters;

        public double GlideslopePathDistanceNm =>
            GlideslopePathDistanceMeters / MetersPerNauticalMile;
    }

    /// <summary>
    /// Project aircraft position onto the extended runway centerline.
    /// Positive approach distance = before threshold (on final).
    /// Expected altitude uses the aim point 1,200 ft past threshold, not the threshold itself.
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

        // 3° path meets elevation at threshold + 1,200 ft (aim / typical TDZ markers).
        var pathDistanceMeters = approachDistanceMeters + GlideslopeAimPointOffsetMeters;
        var expectedAltitudeFeet = runway.ElevationFeet
                                   + pathDistanceMeters / MetersPerNauticalMile
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

    /// <summary>Nominal path altitude at a signed approach distance from the threshold (NM).</summary>
    public static double ExpectedAltitudeFeet(double approachDistanceNm, double elevationFeet) =>
        elevationFeet
        + (approachDistanceNm + GlideslopeAimPointOffsetNm) * NominalPathFeetPerNauticalMile;
}
