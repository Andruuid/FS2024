namespace ChallengeLab.App.Controls;

/// <summary>
/// Shared stateful visibility gate for approach overlays. It keeps the HUD inside a forward
/// landing cone, uses runway direction when available, and falls back conservatively when
/// MSFS reports an unlocked/headlook camera without a usable orientation.
/// </summary>
internal sealed class CockpitLookVisibilityPolicy
{
    internal const double EnterHorizontalDegrees = 30;
    internal const double ExitHorizontalDegrees = 38;
    internal const double EnterVerticalDegrees = 20;
    internal const double ExitVerticalDegrees = 28;
    internal const double NearRunwayDistanceMeters = 1_852;
    internal const double ZeroOrientationEpsilonDegrees = 0.5;

    private const int CockpitCameraState = 2;
    private const int UnlockedCameraSubstate = 2;
    private const int InstrumentViewType = 2;
    private const int QuickviewViewType = 3;
    private const int QuickviewCameraSubstate = 3;
    private const int SmartCameraSubstate = 4;
    private const int InstrumentCameraSubstate = 5;
    private const double EarthRadiusMeters = 6_371_000;
    private const double FeetPerMeter = 3.280839895013123;

    private bool _wasVisible;

    public bool ShouldShow(
        bool isConnected,
        bool isFlightActive,
        CockpitLookContext view)
    {
        if (!isConnected || !isFlightActive)
            return Hide();

        if (view.CameraState is { } cameraState && cameraState != CockpitCameraState)
            return Hide();

        if (view.CameraViewType is InstrumentViewType or QuickviewViewType
            || view.CameraSubstate is QuickviewCameraSubstate
                or SmartCameraSubstate
                or InstrumentCameraSubstate)
        {
            return Hide();
        }

        if (view.CameraPitchRadians is not { } cameraPitchRadians
            || view.CameraYawRadians is not { } cameraYawRadians
            || !double.IsFinite(cameraPitchRadians)
            || !double.IsFinite(cameraYawRadians))
        {
            // Preserve compatibility when orientation telemetry is wholly unavailable, but
            // fail closed when MSFS positively says the pilot is in headlook/freelook.
            return view.CameraSubstate == UnlockedCameraSubstate ? Hide() : Show();
        }

        var cameraYawDegrees = NormalizeSigned(cameraYawRadians * 180.0 / Math.PI);
        var cameraPitchDegrees = cameraPitchRadians * 180.0 / Math.PI;

        // Some MSFS 2024 controller paths report unlocked/headlook while leaving the documented
        // gameplay angles at zero. Treat that combination as unknown rather than forward.
        if (view.CameraSubstate == UnlockedCameraSubstate
            && Math.Abs(cameraYawDegrees) <= ZeroOrientationEpsilonDegrees
            && Math.Abs(cameraPitchDegrees) <= ZeroOrientationEpsilonDegrees)
        {
            return Hide();
        }

        var horizontalLimit = _wasVisible
            ? ExitHorizontalDegrees
            : EnterHorizontalDegrees;
        var verticalLimit = _wasVisible
            ? ExitVerticalDegrees
            : EnterVerticalDegrees;

        // This local cone applies with or without a runway lock and keeps the overlay confined
        // to the main windshield instead of following deliberate side/downward cockpit looks.
        if (Math.Abs(cameraYawDegrees) > horizontalLimit
            || Math.Abs(cameraPitchDegrees) > verticalLimit)
        {
            return Hide();
        }

        if (view.Runway is not { } runway
            || !TryGetTargetDirection(
                view,
                runway,
                out var targetBearing,
                out var targetElevation,
                out var targetDistanceMeters)
            || targetDistanceMeters <= NearRunwayDistanceMeters)
        {
            return Show();
        }

        var lookHeading = Normalize360(view.AircraftHeadingDeg + cameraYawDegrees);
        var lookPitch = view.AircraftPitchDeg + cameraPitchDegrees;
        var horizontalError = Math.Abs(NormalizeSigned(targetBearing - lookHeading));
        var verticalError = Math.Abs(targetElevation - lookPitch);

        return horizontalError <= horizontalLimit && verticalError <= verticalLimit
            ? Show()
            : Hide();
    }

    private bool Show()
    {
        _wasVisible = true;
        return true;
    }

    private bool Hide()
    {
        _wasVisible = false;
        return false;
    }

    private static bool TryGetTargetDirection(
        CockpitLookContext view,
        CockpitRunwayTarget runway,
        out double bearingDegrees,
        out double elevationDegrees,
        out double distanceMeters)
    {
        bearingDegrees = 0;
        elevationDegrees = 0;
        distanceMeters = 0;

        if (!double.IsFinite(view.AircraftLatitude)
            || !double.IsFinite(view.AircraftLongitude)
            || !double.IsFinite(view.AircraftAltitudeFeet)
            || !double.IsFinite(view.AircraftHeadingDeg)
            || !double.IsFinite(view.AircraftPitchDeg)
            || !double.IsFinite(runway.ThresholdLatitude)
            || !double.IsFinite(runway.ThresholdLongitude)
            || !double.IsFinite(runway.ElevationFeet))
        {
            return false;
        }

        var latitude1 = view.AircraftLatitude * Math.PI / 180.0;
        var latitude2 = runway.ThresholdLatitude * Math.PI / 180.0;
        var deltaLatitude = latitude2 - latitude1;
        var deltaLongitude = (runway.ThresholdLongitude - view.AircraftLongitude) * Math.PI / 180.0;
        var sinHalfLatitude = Math.Sin(deltaLatitude / 2.0);
        var sinHalfLongitude = Math.Sin(deltaLongitude / 2.0);
        var haversine = sinHalfLatitude * sinHalfLatitude
                        + Math.Cos(latitude1) * Math.Cos(latitude2)
                        * sinHalfLongitude * sinHalfLongitude;
        distanceMeters = 2.0 * EarthRadiusMeters
                         * Math.Asin(Math.Sqrt(Math.Clamp(haversine, 0, 1)));
        if (!double.IsFinite(distanceMeters))
            return false;

        if (distanceMeters < 1)
        {
            bearingDegrees = Normalize360(view.AircraftHeadingDeg);
            elevationDegrees = 0;
            return true;
        }

        var y = Math.Sin(deltaLongitude) * Math.Cos(latitude2);
        var x = Math.Cos(latitude1) * Math.Sin(latitude2)
                - Math.Sin(latitude1) * Math.Cos(latitude2) * Math.Cos(deltaLongitude);
        bearingDegrees = Normalize360(Math.Atan2(y, x) * 180.0 / Math.PI);
        elevationDegrees = Math.Atan2(
                runway.ElevationFeet - view.AircraftAltitudeFeet,
                distanceMeters * FeetPerMeter)
            * 180.0 / Math.PI;
        return double.IsFinite(bearingDegrees) && double.IsFinite(elevationDegrees);
    }

    private static double Normalize360(double degrees)
    {
        var normalized = degrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    private static double NormalizeSigned(double degrees)
    {
        var normalized = (degrees + 180.0) % 360.0;
        if (normalized < 0)
            normalized += 360.0;
        return normalized - 180.0;
    }
}

internal readonly record struct CockpitLookContext(
    int? CameraState,
    int? CameraSubstate,
    int? CameraViewType,
    double? CameraPitchRadians,
    double? CameraYawRadians,
    double AircraftLatitude,
    double AircraftLongitude,
    double AircraftAltitudeFeet,
    double AircraftHeadingDeg,
    double AircraftPitchDeg,
    CockpitRunwayTarget? Runway);

internal readonly record struct CockpitRunwayTarget(
    double ThresholdLatitude,
    double ThresholdLongitude,
    double ElevationFeet);
