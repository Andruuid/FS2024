using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public readonly record struct TouchdownPointMeasurement(
    double RunwayLengthFeet,
    double AimingMarkerDistanceFeet,
    double IdealNearDistanceFeet,
    double IdealFarDistanceFeet,
    double ActualDistanceFeet,
    double OffsetFromAimingMarkerFeet,
    double SignedDistanceFromIdealBandFeet)
{
    public double AbsoluteDistanceFromIdealBandFeet => Math.Abs(SignedDistanceFromIdealBandFeet);

    // Backward-compatible midpoint aliases used when rendering v1/v2 stored results.
    public double PerfectDistanceFeet => (IdealNearDistanceFeet + IdealFarDistanceFeet) / 2.0;
    public double SignedErrorFeet => ActualDistanceFeet - PerfectDistanceFeet;
    public double AbsoluteErrorFeet => Math.Abs(SignedErrorFeet);
}

/// <summary>Pure runway-local touchdown-position calculation.</summary>
public static class TouchdownPointCalculator
{
    public const double DefaultIdealNearOffsetFeet = 300;
    public const double DefaultIdealFarOffsetFeet = 500;

    /// <summary>Compatibility helper returning the midpoint of the new ideal band.</summary>
    public static double PerfectTouchdownPointFeet(double runwayLengthFeet)
    {
        var marker = AimingPointCalculator.CalculateExpectedDistanceFromThresholdFeet(
            countryCode: null,
            runwayLengthFeet);
        return marker + (DefaultIdealNearOffsetFeet + DefaultIdealFarOffsetFeet) / 2.0;
    }

    /// <summary>Compatibility helper returning the midpoint of the new ideal band.</summary>
    public static double PerfectTouchdownPointFeet(RunwayConfig runway)
    {
        ArgumentNullException.ThrowIfNull(runway);
        return ResolveAimingMarkerFeet(runway)
               + (DefaultIdealNearOffsetFeet + DefaultIdealFarOffsetFeet) / 2.0;
    }

    public static bool TryCalculate(
        RunwayConfig runway,
        TelemetrySample touchdown,
        out TouchdownPointMeasurement measurement,
        out string? unavailableReason,
        double idealNearOffsetFeet = DefaultIdealNearOffsetFeet,
        double idealFarOffsetFeet = DefaultIdealFarOffsetFeet)
    {
        measurement = default;
        unavailableReason = null;

        if (!double.IsFinite(runway.LengthM) || runway.LengthM <= 0)
        {
            unavailableReason = "Runway length is missing or invalid.";
            return false;
        }

        if (!IsValidPosition(runway.ThresholdLatitude, runway.ThresholdLongitude)
            || !double.IsFinite(runway.HeadingTrueDeg))
        {
            unavailableReason = "Runway threshold or heading geometry is invalid.";
            return false;
        }

        if (!IsValidPosition(touchdown.Latitude, touchdown.Longitude))
        {
            unavailableReason = "Touchdown position is missing or invalid.";
            return false;
        }

        if (!double.IsFinite(idealNearOffsetFeet)
            || !double.IsFinite(idealFarOffsetFeet)
            || idealFarOffsetFeet <= idealNearOffsetFeet)
        {
            unavailableReason = "Touchdown ideal-band configuration is invalid.";
            return false;
        }

        var runwayLengthFeet = runway.LengthM / RunwayPathGeometry.MetersPerFoot;
        var actualDistanceFeet = new GeoUtil(runway)
            .DistanceAlongRunwayMeters(touchdown.Latitude, touchdown.Longitude)
            / RunwayPathGeometry.MetersPerFoot;
        double aimingMarkerFeet;
        try
        {
            aimingMarkerFeet = ResolveAimingMarkerFeet(runway);
        }
        catch (ArgumentOutOfRangeException)
        {
            unavailableReason = "Runway landing distance is missing or invalid.";
            return false;
        }

        if (!double.IsFinite(runwayLengthFeet)
            || !double.IsFinite(actualDistanceFeet)
            || !double.IsFinite(aimingMarkerFeet))
        {
            unavailableReason = "Touchdown-point geometry could not be calculated.";
            return false;
        }

        var idealNear = aimingMarkerFeet + idealNearOffsetFeet;
        var idealFar = aimingMarkerFeet + idealFarOffsetFeet;
        var distanceFromBand = actualDistanceFeet < idealNear
            ? actualDistanceFeet - idealNear
            : actualDistanceFeet > idealFar
                ? actualDistanceFeet - idealFar
                : 0;
        measurement = new TouchdownPointMeasurement(
            runwayLengthFeet,
            aimingMarkerFeet,
            idealNear,
            idealFar,
            actualDistanceFeet,
            actualDistanceFeet - aimingMarkerFeet,
            distanceFromBand);
        return true;
    }

    public static double ResolveAimingMarkerFeet(RunwayConfig runway)
    {
        ArgumentNullException.ThrowIfNull(runway);
        if (runway.AimingMarkerStartM is { } configured
            && double.IsFinite(configured)
            && configured >= 0)
            return configured / RunwayPathGeometry.MetersPerFoot;

        var displacementM = runway.DisplacedThresholdM is { } displacement
                            && double.IsFinite(displacement) && displacement >= 0
            ? displacement
            : 0;
        var ldaM = runway.LandingDistanceAvailableM is { } lda
                   && double.IsFinite(lda) && lda > 0
            ? lda
            : runway.LengthM - displacementM;
        return AimingPointCalculator.CalculateExpectedDistanceFromThresholdFeet(
            runway.CountryCode,
            ldaM / RunwayPathGeometry.MetersPerFoot);
    }

    private static bool IsValidPosition(double latitude, double longitude) =>
        double.IsFinite(latitude)
        && double.IsFinite(longitude)
        && latitude is >= -90 and <= 90
        && longitude is >= -180 and <= 180;
}
