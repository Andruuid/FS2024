using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public readonly record struct TouchdownPointMeasurement(
    double RunwayLengthFeet,
    double PerfectDistanceFeet,
    double ActualDistanceFeet,
    double SignedErrorFeet,
    double AbsoluteErrorFeet);

/// <summary>Pure runway-local touchdown-position calculation.</summary>
public static class TouchdownPointCalculator
{
    public static double PerfectTouchdownPointFeet(double runwayLengthFeet)
        => runwayLengthFeet <= 6_000 ? 1_100 : 1_200;

    public static bool TryCalculate(
        RunwayConfig runway,
        TelemetrySample touchdown,
        out TouchdownPointMeasurement measurement,
        out string? unavailableReason)
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

        var runwayLengthFeet = runway.LengthM / RunwayPathGeometry.MetersPerFoot;
        var actualDistanceFeet = new GeoUtil(runway)
            .DistanceAlongRunwayMeters(touchdown.Latitude, touchdown.Longitude)
            / RunwayPathGeometry.MetersPerFoot;
        if (!double.IsFinite(runwayLengthFeet) || !double.IsFinite(actualDistanceFeet))
        {
            unavailableReason = "Touchdown-point geometry could not be calculated.";
            return false;
        }

        var perfectDistanceFeet = PerfectTouchdownPointFeet(runwayLengthFeet);
        var signedErrorFeet = actualDistanceFeet - perfectDistanceFeet;
        measurement = new TouchdownPointMeasurement(
            runwayLengthFeet,
            perfectDistanceFeet,
            actualDistanceFeet,
            signedErrorFeet,
            Math.Abs(signedErrorFeet));
        return true;
    }

    private static bool IsValidPosition(double latitude, double longitude)
        => double.IsFinite(latitude)
           && double.IsFinite(longitude)
           && latitude is >= -90 and <= 90
           && longitude is >= -180 and <= 180;
}
