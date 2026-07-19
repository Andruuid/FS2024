using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Controls.Aether;

/// <summary>Maps domain telemetry into Aether presentation snapshots.</summary>
internal static class AetherMapper
{
    public static AetherSnapshot FromGuidance(
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

        var windReading = RelativeWindCalculator.Calculate(sample, isConnected);
        var wind = windReading.IsAvailable
            ? new AetherWind(
                Available: true,
                SpeedKts: windReading.WindSpeedKts,
                RelativeFromDeg: windReading.RelativeFromAngleDeg,
                CrosswindKts: windReading.CrosswindKts,
                HeadwindKts: windReading.LongitudinalKts)
            : AetherWind.Empty;

        double? targetAngle = runway is null
            ? null
            : RunwayPathGeometry.SanitizeGlideslopeDeg(runway.GlideslopeDeg);

        double? pathError = guidance.GlideslopeDeg is { } pathAngle && targetAngle is { } tgtPath
            ? pathAngle - tgtPath
            : null;
        double? descentError = guidance.DescentAngleDeg is { } descentAngle && targetAngle is { } tgtDescent
            ? descentAngle - tgtDescent
            : null;

        var path = new AetherPath(
            PathAngleDeg: guidance.GlideslopeDeg,
            DescentAngleDeg: guidance.DescentAngleDeg,
            TargetAngleDeg: targetAngle,
            PathErrorDeg: pathError,
            DescentErrorDeg: descentError,
            PathTone: MapTone(guidance.GlideslopeStatus),
            DescentTone: MapTone(guidance.DescentAngleStatus),
            DistanceNm: guidance.ApproachDistanceNm,
            ProgressPercent: guidance.ProgressPercent,
            InsideCollectionWindow: guidance.IsInsideCollectionWindow);

        var energy = new AetherEnergy(
            IasKts: guidance.AirspeedKts,
            TargetIasKts: guidance.TargetAirspeedKts,
            IasDeltaKts: guidance.AirspeedDeltaKts,
            IasTone: MapTone(guidance.AirspeedStatus),
            VerticalSpeedFpm: guidance.VerticalSpeedFpm,
            TargetVerticalSpeedFpm: guidance.TargetVerticalSpeedFpm,
            VerticalSpeedTone: MapTone(guidance.DescentAngleStatus));

        var camera = new AetherCamera(
            sample.CameraState,
            sample.CameraViewType,
            sample.CameraGameplayPitchRadians,
            sample.CameraGameplayYawRadians,
            sample.Latitude,
            sample.Longitude,
            sample.AltitudeFeet,
            sample.HeadingTrueDeg,
            sample.PitchDeg);

        AetherRunway? aetherRunway = runway is null
            ? null
            : new AetherRunway(
                runway.ThresholdLatitude,
                runway.ThresholdLongitude,
                runway.ElevationFeet);

        return new AetherSnapshot(
            sequence,
            sample.Timestamp,
            isConnected,
            flightActive,
            wind,
            path,
            energy,
            camera,
            aetherRunway);
    }

    private static AetherTone MapTone(LandingMonitorStatus status) => status switch
    {
        LandingMonitorStatus.Green => AetherTone.Good,
        LandingMonitorStatus.Orange => AetherTone.Caution,
        LandingMonitorStatus.Red => AetherTone.Alert,
        _ => AetherTone.Quiet,
    };
}
