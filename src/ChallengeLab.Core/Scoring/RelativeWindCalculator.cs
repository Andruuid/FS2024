using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Aircraft-relative ambient-wind components. Positive crosswind means wind from the right;
/// negative means wind from the left. Positive longitudinal wind is a headwind.
/// </summary>
public readonly record struct RelativeWindReading(
    bool IsAvailable,
    bool HasWind,
    double WindDirectionDeg,
    double WindSpeedKts,
    double RelativeFromAngleDeg,
    double LongitudinalKts,
    double CrosswindKts);

public static class RelativeWindCalculator
{
    public const double CalmThresholdKts = 0.5;

    public static RelativeWindReading Calculate(TelemetrySample sample, bool isConnected = true) =>
        Calculate(
            sample.WindDirectionDeg,
            sample.WindVelocityKts,
            sample.HeadingTrueDeg,
            isConnected);

    public static RelativeWindReading Calculate(
        double windDirectionDeg,
        double windSpeedKts,
        double aircraftHeadingDeg,
        bool isConnected = true)
    {
        if (!isConnected
            || !double.IsFinite(windDirectionDeg)
            || !double.IsFinite(windSpeedKts)
            || !double.IsFinite(aircraftHeadingDeg)
            || windSpeedKts < 0)
        {
            return default;
        }

        var direction = NormalizeDirection(windDirectionDeg);
        var relativeFrom = LandingMonitorCalculator.NormalizeSignedDegrees(
            direction - NormalizeDirection(aircraftHeadingDeg));
        var relativeRadians = relativeFrom * Math.PI / 180.0;

        return new RelativeWindReading(
            IsAvailable: true,
            HasWind: windSpeedKts >= CalmThresholdKts,
            WindDirectionDeg: direction,
            WindSpeedKts: windSpeedKts,
            RelativeFromAngleDeg: relativeFrom,
            LongitudinalKts: windSpeedKts * Math.Cos(relativeRadians),
            CrosswindKts: windSpeedKts * Math.Sin(relativeRadians));
    }

    private static double NormalizeDirection(double degrees)
    {
        var normalized = degrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }
}
