using ChallengeLab.App.Controls;
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
    double? CrabAngleDeg,
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
        return FromGuidance(sample, isConnected, sequence, runway, guidance);
    }

    public static HudPresentationFrame FromGuidance(
        TelemetrySample sample,
        bool isConnected,
        long sequence,
        RunwayConfig? runway,
        LandingMonitorReading guidance)
    {
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
            CrabAnglePresentation.FromSample(sample),
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
    int? CameraSubstate,
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
        sample.CameraSubstate,
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
/// HUD adapter over the shared cockpit-look visibility policy.
/// </summary>
internal sealed class HudViewGate
{
    internal const double EnterHorizontalDegrees = CockpitLookVisibilityPolicy.EnterHorizontalDegrees;
    internal const double ExitHorizontalDegrees = CockpitLookVisibilityPolicy.ExitHorizontalDegrees;
    internal const double EnterUpwardDegrees = CockpitLookVisibilityPolicy.EnterUpwardDegrees;
    internal const double ExitUpwardDegrees = CockpitLookVisibilityPolicy.ExitUpwardDegrees;
    internal const double EnterDownwardDegrees = CockpitLookVisibilityPolicy.EnterDownwardDegrees;
    internal const double ExitDownwardDegrees = CockpitLookVisibilityPolicy.ExitDownwardDegrees;
    internal const double NearRunwayDistanceMeters = CockpitLookVisibilityPolicy.NearRunwayDistanceMeters;

    private readonly CockpitLookVisibilityPolicy _policy = new();

    public bool ShouldShow(HudPresentationFrame? frame)
    {
        var view = frame?.View ?? default;
        CockpitRunwayTarget? runway = view.HasRunwayTarget
            ? new CockpitRunwayTarget(
                view.RunwayLatitude,
                view.RunwayLongitude,
                view.RunwayElevationFeet)
            : null;
        var context = new CockpitLookContext(
            view.CameraState,
            view.CameraSubstate,
            view.CameraViewType,
            view.CameraPitchRadians,
            view.CameraYawRadians,
            view.AircraftLatitude,
            view.AircraftLongitude,
            view.AircraftAltitudeFeet,
            view.AircraftHeadingDeg,
            view.AircraftPitchDeg,
            runway);

        return _policy.ShouldShow(
            frame?.IsConnected == true,
            frame?.IsFlightActive == true,
            context);
    }
}
