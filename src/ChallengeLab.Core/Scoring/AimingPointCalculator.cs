namespace ChallengeLab.Core.Scoring;

/// <summary>Regulatory aiming-point estimate measured from the usable landing threshold.</summary>
public static class AimingPointCalculator
{
    public static double CalculateExpectedDistanceFromThresholdFeet(
        string? countryCode,
        double landingDistanceAvailableFeet)
    {
        if (!double.IsFinite(landingDistanceAvailableFeet) || landingDistanceAvailableFeet <= 0)
            throw new ArgumentOutOfRangeException(nameof(landingDistanceAvailableFeet));

        if (string.Equals(countryCode?.Trim(), "US", StringComparison.OrdinalIgnoreCase))
            return landingDistanceAvailableFeet >= 4_200 ? 1_000 : 500;

        var lengthMeters = landingDistanceAvailableFeet * RunwayPathGeometry.MetersPerFoot;
        if (lengthMeters >= 2_400) return 1_312;
        if (lengthMeters >= 1_200) return 984;
        return 500;
    }

    public static double CalculateDistanceFromPavementEndFeet(
        string? countryCode,
        double landingDistanceAvailableFeet,
        double displacedThresholdFeet)
    {
        if (!double.IsFinite(displacedThresholdFeet) || displacedThresholdFeet < 0)
            throw new ArgumentOutOfRangeException(nameof(displacedThresholdFeet));
        return displacedThresholdFeet
               + CalculateExpectedDistanceFromThresholdFeet(countryCode, landingDistanceAvailableFeet);
    }

    public static double EstimateMarkerLengthFeet(string? countryCode, double landingDistanceAvailableFeet)
    {
        if (string.Equals(countryCode?.Trim(), "US", StringComparison.OrdinalIgnoreCase))
            return landingDistanceAvailableFeet >= 4_200 ? 150 : 100;
        return landingDistanceAvailableFeet >= 1_200 / RunwayPathGeometry.MetersPerFoot
            ? 52.5 / RunwayPathGeometry.MetersPerFoot
            : 37.5 / RunwayPathGeometry.MetersPerFoot;
    }
}
