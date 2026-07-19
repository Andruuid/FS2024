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
    double? DescentAngleDeg,
    LandingMonitorStatus DescentAngleStatus,
    double? VerticalSpeedFpm,
    double? TargetVerticalSpeedFpm,
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
    public const double KnotsToFeetPerMinute = 6076.115485564304 / 60.0;

    public const double GlideslopeGreenHalfBandDeg = 0.2;
    public const double DescentAngleGreenHalfBandDeg = 0.2;
    public const double DescentAngleOrangeHalfBandDeg = 0.5;

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
                null,
                LandingMonitorStatus.Neutral,
                verticalSpeed,
                null,
                null,
                null,
                0,
                false);
        }

        var targetGlideslopeDeg = RunwayPathGeometry.SanitizeGlideslopeDeg(runway.GlideslopeDeg);
        double? glideslope = null;
        var glideslopeStatus = LandingMonitorStatus.Neutral;
        var heightAboveFieldFeet = sample.AltitudeFeet - runway.ElevationFeet;
        // Geometric path angle to the aim point, compared against the runway's nominal GS.
        var pathDistanceMeters = path.GlideslopePathDistanceMeters;
        if (!sample.SimOnGround
            && pathDistanceMeters > 0
            && double.IsFinite(heightAboveFieldFeet)
            && heightAboveFieldFeet >= 0)
        {
            glideslope = Math.Atan2(
                    heightAboveFieldFeet,
                    pathDistanceMeters * FeetPerMeter)
                * 180.0 / Math.PI;
            glideslopeStatus = ClassifyGlideslope(glideslope.Value, targetGlideslopeDeg);
        }

        var closingSpeed = GroundMotionResolver.ProjectGroundSpeedAlong(
            sample,
            runway.HeadingTrueDeg);

        double? descentAngle = null;
        double? targetVerticalSpeed = null;
        var descentAngleStatus = LandingMonitorStatus.Neutral;
        if (!sample.SimOnGround
            && verticalSpeed is not null
            && closingSpeed is > 5)
        {
            descentAngle = Math.Atan2(
                    -verticalSpeed.Value,
                    closingSpeed.Value * KnotsToFeetPerMinute)
                * 180.0 / Math.PI;
            descentAngleStatus = ClassifyDescentAngle(
                descentAngle.Value,
                targetGlideslopeDeg);
            targetVerticalSpeed = -closingSpeed.Value
                                  * KnotsToFeetPerMinute
                                  * Math.Tan(targetGlideslopeDeg * Math.PI / 180.0);
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
            descentAngle,
            descentAngleStatus,
            verticalSpeed,
            targetVerticalSpeed,
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

    /// <summary>Green when geometric angle is within ±0.2° of the runway target path.</summary>
    public static LandingMonitorStatus ClassifyGlideslope(
        double measuredDeg,
        double targetDeg = RunwayPathGeometry.DefaultGlideslopeDeg)
    {
        var target = RunwayPathGeometry.SanitizeGlideslopeDeg(targetDeg);
        // Small epsilon so exact half-band edges (e.g. 2.8 vs 3.0) stay green.
        return Math.Abs(measuredDeg - target) <= GlideslopeGreenHalfBandDeg + 1e-9
            ? LandingMonitorStatus.Green
            : LandingMonitorStatus.Red;
    }

    public static LandingMonitorStatus ClassifyDescentAngle(
        double measuredDeg,
        double targetDeg = RunwayPathGeometry.DefaultGlideslopeDeg)
    {
        if (!double.IsFinite(measuredDeg))
            return LandingMonitorStatus.Neutral;

        var target = RunwayPathGeometry.SanitizeGlideslopeDeg(targetDeg);
        var error = Math.Abs(measuredDeg - target);
        if (error <= DescentAngleGreenHalfBandDeg + 1e-9)
            return LandingMonitorStatus.Green;
        return error <= DescentAngleOrangeHalfBandDeg + 1e-9
            ? LandingMonitorStatus.Orange
            : LandingMonitorStatus.Red;
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
