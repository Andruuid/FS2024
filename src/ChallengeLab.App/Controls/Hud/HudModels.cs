using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Controls.Hud;

/// <summary>Compact immutable frame consumed by the approach HUD renderer.</summary>
internal sealed record HudPresentationFrame(
    long Sequence,
    DateTimeOffset CapturedAt,
    bool IsConnected,
    bool IsFlightActive,
    LandingMonitorReading Guidance,
    RelativeWindReading Wind,
    double? TargetGlideslopeDeg,
    HudViewContext View)
{
    public static HudPresentationFrame Disconnected(long sequence) => new(
        sequence,
        DateTimeOffset.UtcNow,
        IsConnected: false,
        IsFlightActive: false,
        EmptyGuidance,
        default,
        null,
        default);

    public static HudPresentationFrame FromSample(
        TelemetrySample sample,
        bool isConnected,
        long sequence,
        RunwayConfig? runway,
        double? targetAirspeedKts,
        double approachPathMinDistNm,
        double approachPathMaxDistNm)
    {
        var guidance = LandingMonitorCalculator.Calculate(
            sample,
            runway,
            targetAirspeedKts,
            approachPathMinDistNm,
            approachPathMaxDistNm);
        var flightActive = isConnected && (
            !string.IsNullOrWhiteSpace(sample.AircraftTitle)
            || sample.AirspeedKts > 1
            || sample.AltitudeFeet > 50
            || !sample.SimOnGround);

        return new HudPresentationFrame(
            sequence,
            sample.Timestamp,
            isConnected,
            flightActive,
            guidance,
            RelativeWindCalculator.Calculate(sample, isConnected),
            runway is null
                ? null
                : RunwayPathGeometry.SanitizeGlideslopeDeg(runway.GlideslopeDeg),
            HudViewContext.FromSample(sample, runway));
    }

    private static LandingMonitorReading EmptyGuidance { get; } = new(
        null,
        null,
        null,
        LandingMonitorStatus.Neutral,
        null,
        LandingMonitorStatus.Neutral,
        null,
        LandingMonitorStatus.Neutral,
        null,
        null,
        null,
        null,
        0,
        false);
}

internal readonly record struct HudViewContext(
    int? CameraState,
    int? CameraViewType,
    double? CameraPitchRadians,
    double? CameraYawRadians,
    double AircraftLatitude,
    double AircraftLongitude,
    double AircraftAltitudeFeet,
    double AircraftHeadingDeg,
    double AircraftPitchDeg,
    bool HasRunwayTarget,
    double RunwayLatitude,
    double RunwayLongitude,
    double RunwayElevationFeet)
{
    public static HudViewContext FromSample(TelemetrySample sample, RunwayConfig? runway) => new(
        sample.CameraState,
        sample.CameraViewType,
        sample.CameraGameplayPitchRadians,
        sample.CameraGameplayYawRadians,
        sample.Latitude,
        sample.Longitude,
        sample.AltitudeFeet,
        sample.HeadingTrueDeg,
        sample.PitchDeg,
        runway is not null,
        runway?.ThresholdLatitude ?? 0,
        runway?.ThresholdLongitude ?? 0,
        runway?.ElevationFeet ?? 0);
}

/// <summary>
/// Stateful airport-direction visibility gate. Missing direction or runway data deliberately
/// falls back to visible; known exterior and instrument views always hide the overlay.
/// </summary>
internal sealed class HudViewGate
{
    internal const double EnterHorizontalDegrees = 35;
    internal const double ExitHorizontalDegrees = 45;
    internal const double EnterVerticalDegrees = 25;
    internal const double ExitVerticalDegrees = 35;

    private const int CockpitCameraState = 2;
    private const int InstrumentViewType = 2;
    private const double EarthRadiusMeters = 6_371_000;
    private const double FeetPerMeter = 3.280839895013123;

    private bool _wasDirectionVisible;

    public bool ShouldShow(HudPresentationFrame? frame)
    {
        if (frame is null || !frame.IsConnected || !frame.IsFlightActive)
        {
            _wasDirectionVisible = false;
            return false;
        }

        var view = frame.View;
        if (view.CameraState is { } cameraState && cameraState != CockpitCameraState)
        {
            _wasDirectionVisible = false;
            return false;
        }

        if (view.CameraViewType == InstrumentViewType)
        {
            _wasDirectionVisible = false;
            return false;
        }

        if (!view.HasRunwayTarget
            || view.CameraPitchRadians is not { } cameraPitch
            || view.CameraYawRadians is not { } cameraYaw
            || !TryGetTargetDirection(view, out var targetBearing, out var targetElevation))
        {
            _wasDirectionVisible = false;
            return true;
        }

        // MSFS reports gameplay camera offsets in radians. Treat positive yaw as right and
        // positive pitch as up, then combine them with aircraft attitude to get world look direction.
        var lookHeading = NormalizeDirection(
            view.AircraftHeadingDeg + cameraYaw * 180.0 / Math.PI);
        var lookPitch = view.AircraftPitchDeg + cameraPitch * 180.0 / Math.PI;
        var horizontalError = Math.Abs(LandingMonitorCalculator.NormalizeSignedDegrees(
            targetBearing - lookHeading));
        var verticalError = Math.Abs(targetElevation - lookPitch);

        var horizontalLimit = _wasDirectionVisible
            ? ExitHorizontalDegrees
            : EnterHorizontalDegrees;
        var verticalLimit = _wasDirectionVisible
            ? ExitVerticalDegrees
            : EnterVerticalDegrees;
        _wasDirectionVisible = horizontalError <= horizontalLimit
                               && verticalError <= verticalLimit;
        return _wasDirectionVisible;
    }

    private static bool TryGetTargetDirection(
        HudViewContext view,
        out double bearingDegrees,
        out double elevationDegrees)
    {
        bearingDegrees = 0;
        elevationDegrees = 0;
        if (!double.IsFinite(view.AircraftLatitude)
            || !double.IsFinite(view.AircraftLongitude)
            || !double.IsFinite(view.AircraftAltitudeFeet)
            || !double.IsFinite(view.AircraftHeadingDeg)
            || !double.IsFinite(view.AircraftPitchDeg)
            || !double.IsFinite(view.RunwayLatitude)
            || !double.IsFinite(view.RunwayLongitude)
            || !double.IsFinite(view.RunwayElevationFeet))
        {
            return false;
        }

        var latitude1 = view.AircraftLatitude * Math.PI / 180.0;
        var latitude2 = view.RunwayLatitude * Math.PI / 180.0;
        var deltaLatitude = latitude2 - latitude1;
        var deltaLongitude = (view.RunwayLongitude - view.AircraftLongitude) * Math.PI / 180.0;
        var sinHalfLatitude = Math.Sin(deltaLatitude / 2.0);
        var sinHalfLongitude = Math.Sin(deltaLongitude / 2.0);
        var haversine = sinHalfLatitude * sinHalfLatitude
                        + Math.Cos(latitude1) * Math.Cos(latitude2)
                        * sinHalfLongitude * sinHalfLongitude;
        var distanceMeters = 2.0 * EarthRadiusMeters
                             * Math.Asin(Math.Sqrt(Math.Clamp(haversine, 0, 1)));
        if (!double.IsFinite(distanceMeters) || distanceMeters < 1)
            return false;

        var y = Math.Sin(deltaLongitude) * Math.Cos(latitude2);
        var x = Math.Cos(latitude1) * Math.Sin(latitude2)
                - Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(deltaLongitude);
        bearingDegrees = NormalizeDirection(Math.Atan2(y, x) * 180.0 / Math.PI);
        elevationDegrees = Math.Atan2(
                view.RunwayElevationFeet - view.AircraftAltitudeFeet,
                distanceMeters * FeetPerMeter)
            * 180.0 / Math.PI;
        return double.IsFinite(bearingDegrees) && double.IsFinite(elevationDegrees);
    }

    private static double NormalizeDirection(double degrees)
    {
        var normalized = degrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }
}
