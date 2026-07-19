namespace ChallengeLab.App.Controls.Aether;

/// <summary>
/// Cockpit look gate for Aether. Hides exterior/instrument views and when the pilot is
/// clearly looking away from the runway target. Near-threshold flare keeps a forward view.
/// </summary>
internal sealed class AetherLookPolicy
{
    private const int CockpitCameraState = 2;
    private const int InstrumentViewType = 2;
    private const double EarthRadiusMeters = 6_371_000;
    private const double FeetPerMeter = 3.280839895013123;

    // Independent hysteresis bands (not shared with any other overlay).
    private const double EnterYawDeg = 32;
    private const double ExitYawDeg = 42;
    private const double EnterPitchDeg = 22;
    private const double ExitPitchDeg = 32;
    private const double NearThresholdMeters = 1_650;

    private bool _wasLookingAtTarget;

    public bool ShouldRender(AetherSnapshot? snapshot)
    {
        if (snapshot is null || !snapshot.IsConnected || !snapshot.IsFlightActive)
        {
            _wasLookingAtTarget = false;
            return false;
        }

        var cam = snapshot.Camera;
        if (cam.CameraState is { } state && state != CockpitCameraState)
        {
            _wasLookingAtTarget = false;
            return false;
        }

        if (cam.CameraViewType == InstrumentViewType)
        {
            _wasLookingAtTarget = false;
            return false;
        }

        if (snapshot.Runway is not { } runway
            || cam.CameraPitchRadians is not { } pitchRad
            || cam.CameraYawRadians is not { } yawRad
            || !TryDirection(cam, runway, out var bearingDeg, out var elevationDeg, out var distanceM))
        {
            // No runway lock: still show energy/wind instruments in cockpit.
            _wasLookingAtTarget = false;
            return true;
        }

        var yawDeg = NormalizeSigned(yawRad * 180.0 / Math.PI);
        var pitchDeg = pitchRad * 180.0 / Math.PI;
        var yawLimit = _wasLookingAtTarget ? ExitYawDeg : EnterYawDeg;
        var pitchLimit = _wasLookingAtTarget ? ExitPitchDeg : EnterPitchDeg;

        if (distanceM <= NearThresholdMeters)
        {
            _wasLookingAtTarget = Math.Abs(yawDeg) <= yawLimit && Math.Abs(pitchDeg) <= pitchLimit;
            return _wasLookingAtTarget;
        }

        var lookHeading = Normalize360(cam.AircraftHeadingDeg + yawDeg);
        var lookPitch = cam.AircraftPitchDeg + pitchDeg;
        var horizontal = Math.Abs(NormalizeSigned(bearingDeg - lookHeading));
        var vertical = Math.Abs(elevationDeg - lookPitch);

        _wasLookingAtTarget = horizontal <= yawLimit && vertical <= pitchLimit;
        return _wasLookingAtTarget;
    }

    private static bool TryDirection(
        AetherCamera cam,
        AetherRunway runway,
        out double bearingDeg,
        out double elevationDeg,
        out double distanceM)
    {
        bearingDeg = 0;
        elevationDeg = 0;
        distanceM = 0;

        if (!double.IsFinite(cam.AircraftLatitude)
            || !double.IsFinite(cam.AircraftLongitude)
            || !double.IsFinite(cam.AircraftAltitudeFeet)
            || !double.IsFinite(cam.AircraftHeadingDeg)
            || !double.IsFinite(cam.AircraftPitchDeg)
            || !double.IsFinite(runway.ThresholdLatitude)
            || !double.IsFinite(runway.ThresholdLongitude)
            || !double.IsFinite(runway.ElevationFeet))
        {
            return false;
        }

        var lat1 = cam.AircraftLatitude * Math.PI / 180.0;
        var lat2 = runway.ThresholdLatitude * Math.PI / 180.0;
        var dLat = lat2 - lat1;
        var dLon = (runway.ThresholdLongitude - cam.AircraftLongitude) * Math.PI / 180.0;
        var sinHalfLat = Math.Sin(dLat / 2.0);
        var sinHalfLon = Math.Sin(dLon / 2.0);
        var h = sinHalfLat * sinHalfLat
                + Math.Cos(lat1) * Math.Cos(lat2) * sinHalfLon * sinHalfLon;
        distanceM = 2.0 * EarthRadiusMeters * Math.Asin(Math.Sqrt(Math.Clamp(h, 0, 1)));
        if (!double.IsFinite(distanceM))
            return false;

        if (distanceM < 1)
        {
            bearingDeg = Normalize360(cam.AircraftHeadingDeg);
            elevationDeg = 0;
            return true;
        }

        var y = Math.Sin(dLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2)
                - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(dLon);
        bearingDeg = Normalize360(Math.Atan2(y, x) * 180.0 / Math.PI);
        elevationDeg = Math.Atan2(
                runway.ElevationFeet - cam.AircraftAltitudeFeet,
                distanceM * FeetPerMeter)
            * 180.0 / Math.PI;
        return double.IsFinite(bearingDeg) && double.IsFinite(elevationDeg);
    }

    private static double Normalize360(double degrees)
    {
        var n = degrees % 360.0;
        return n < 0 ? n + 360.0 : n;
    }

    private static double NormalizeSigned(double degrees)
    {
        var n = (degrees + 180.0) % 360.0;
        if (n < 0)
            n += 360.0;
        return n - 180.0;
    }
}
