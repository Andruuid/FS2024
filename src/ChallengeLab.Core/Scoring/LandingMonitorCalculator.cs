using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public enum LandingMonitorStatus
{
    Neutral,
    Green,
    Orange,
    Red
}

/// <summary>Instantaneous, side-effect-free approach guidance for the secondary HUD.</summary>
public sealed record LandingMonitorReading(
    double? AirspeedKts,
    double? TargetAirspeedKts,
    double? AirspeedDeltaKts,
    LandingMonitorStatus AirspeedStatus,
    double? GlideslopeDeg,
    LandingMonitorStatus GlideslopeStatus,
    double? VerticalSpeedFpm,
    LandingMonitorStatus VerticalSpeedStatus,
    double? ApproachDistanceNm,
    double? ClosingSpeedKts,
    double ProgressPercent,
    bool IsInsideCollectionWindow);

/// <summary>
/// Computes live landing-monitor values without retaining attempt state. Stateful concerns such as
/// ETA smoothing, graph history, and collection start/stop live in the application view model.
/// </summary>
public static class LandingMonitorCalculator
{
    public const double FeetPerMeter = 3.280839895013123;

    public static LandingMonitorReading Calculate(
        TelemetrySample sample,
        RunwayConfig? runway,
        double? targetAirspeedKts,
        double approachPathMinDistNm,
        double approachPathMaxDistNm)
    {
        var airspeed = FiniteOrNull(sample.AirspeedKts);
        var target = targetAirspeedKts is > 0 && double.IsFinite(targetAirspeedKts.Value)
            ? targetAirspeedKts
            : null;
        double? airspeedDelta = airspeed is not null && target is not null
            ? airspeed.Value - target.Value
            : null;
        var airspeedStatus = airspeedDelta is null
            ? LandingMonitorStatus.Neutral
            : ClassifyAirspeed(airspeedDelta.Value);

        var verticalSpeed = FiniteOrNull(sample.VerticalSpeedFpm);
        var verticalSpeedStatus = verticalSpeed is null
            ? LandingMonitorStatus.Neutral
            : ClassifyVerticalSpeed(verticalSpeed.Value);

        if (runway is null
            || !RunwayPathGeometry.TryGetState(sample, runway, out var path)
            || !double.IsFinite(approachPathMaxDistNm)
            || approachPathMaxDistNm <= 0)
        {
            return new LandingMonitorReading(
                airspeed,
                target,
                airspeedDelta,
                airspeedStatus,
                null,
                LandingMonitorStatus.Neutral,
                verticalSpeed,
                verticalSpeedStatus,
                null,
                null,
                0,
                false);
        }

        double? glideslope = null;
        var glideslopeStatus = LandingMonitorStatus.Neutral;
        var heightAboveThresholdFeet = sample.AltitudeFeet - runway.ElevationFeet;
        if (!sample.SimOnGround
            && path.ApproachDistanceMeters > 0
            && double.IsFinite(heightAboveThresholdFeet)
            && heightAboveThresholdFeet >= 0)
        {
            glideslope = Math.Atan2(
                    heightAboveThresholdFeet,
                    path.ApproachDistanceMeters * FeetPerMeter)
                * 180.0 / Math.PI;
            glideslopeStatus = ClassifyGlideslope(glideslope.Value);
        }

        double? closingSpeed = null;
        if (double.IsFinite(sample.GroundSpeedKts)
            && sample.GroundSpeedKts >= 0
            && double.IsFinite(sample.GroundTrackTrueDeg)
            && double.IsFinite(runway.HeadingTrueDeg))
        {
            var trackErrorRadians = NormalizeSignedDegrees(
                    sample.GroundTrackTrueDeg - runway.HeadingTrueDeg)
                * Math.PI / 180.0;
            closingSpeed = sample.GroundSpeedKts * Math.Cos(trackErrorRadians);
        }

        var progress = (1.0 - Math.Clamp(path.ApproachDistanceNm, 0, approachPathMaxDistNm)
            / approachPathMaxDistNm) * 100.0;
        var insideCollectionWindow = !sample.SimOnGround
                                     && path.ApproachDistanceNm >= approachPathMinDistNm
                                     && path.ApproachDistanceNm <= approachPathMaxDistNm;

        return new LandingMonitorReading(
            airspeed,
            target,
            airspeedDelta,
            airspeedStatus,
            glideslope,
            glideslopeStatus,
            verticalSpeed,
            verticalSpeedStatus,
            path.ApproachDistanceNm,
            closingSpeed,
            Math.Clamp(progress, 0, 100),
            insideCollectionWindow);
    }

    public static LandingMonitorStatus ClassifyAirspeed(double deltaKts)
    {
        var error = Math.Abs(deltaKts);
        if (error <= 10) return LandingMonitorStatus.Green;
        return error < 50 ? LandingMonitorStatus.Orange : LandingMonitorStatus.Red;
    }

    public static LandingMonitorStatus ClassifyGlideslope(double degrees) =>
        degrees is >= 2.8 and <= 3.2
            ? LandingMonitorStatus.Green
            : LandingMonitorStatus.Red;

    public static LandingMonitorStatus ClassifyVerticalSpeed(double verticalSpeedFpm)
    {
        if (verticalSpeedFpm > 0 || verticalSpeedFpm < -1000)
            return LandingMonitorStatus.Red;
        if (verticalSpeedFpm < -700)
            return LandingMonitorStatus.Orange;
        return LandingMonitorStatus.Green;
    }

    public static double NormalizeSignedDegrees(double degrees)
    {
        degrees %= 360.0;
        if (degrees > 180) degrees -= 360;
        if (degrees < -180) degrees += 360;
        return degrees;
    }

    private static double? FiniteOrNull(double value) => double.IsFinite(value) ? value : null;
}
