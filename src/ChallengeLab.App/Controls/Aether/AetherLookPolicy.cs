using ChallengeLab.App.Controls;

namespace ChallengeLab.App.Controls.Aether;

/// <summary>
/// Cockpit look gate for Aether. Hides exterior/instrument views and when the pilot is
/// clearly looking away from the runway target. Near-threshold flare keeps a forward view.
/// </summary>
internal sealed class AetherLookPolicy
{
    private readonly CockpitLookVisibilityPolicy _policy = new();

    public bool ShouldRender(AetherSnapshot? snapshot)
    {
        var cam = snapshot?.Camera ?? default;
        CockpitRunwayTarget? runway = snapshot?.Runway is { } target
            ? new CockpitRunwayTarget(
                target.ThresholdLatitude,
                target.ThresholdLongitude,
                target.ElevationFeet)
            : null;
        var context = new CockpitLookContext(
            cam.CameraState,
            cam.CameraSubstate,
            cam.CameraViewType,
            cam.CameraPitchRadians,
            cam.CameraYawRadians,
            cam.AircraftLatitude,
            cam.AircraftLongitude,
            cam.AircraftAltitudeFeet,
            cam.AircraftHeadingDeg,
            cam.AircraftPitchDeg,
            runway);

        return _policy.ShouldShow(
            snapshot?.IsConnected == true,
            snapshot?.IsFlightActive == true,
            context);
    }
}
