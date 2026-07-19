namespace ChallengeLab.App.Controls.Aether;

/// <summary>Tone band used by Aether instruments (independent of scoring enums).</summary>
internal enum AetherTone
{
    Quiet,
    Good,
    Caution,
    Alert
}

/// <summary>Immutable frame for the Aether approach overlay.</summary>
internal sealed record AetherSnapshot(
    long Sequence,
    DateTimeOffset CapturedAt,
    bool IsConnected,
    bool IsFlightActive,
    AetherWind Wind,
    AetherPath Path,
    AetherEnergy Energy,
    AetherCamera Camera,
    AetherRunway? Runway)
{
    public static AetherSnapshot Disconnected(long sequence) => new(
        sequence,
        DateTimeOffset.UtcNow,
        IsConnected: false,
        IsFlightActive: false,
        AetherWind.Empty,
        AetherPath.Empty,
        AetherEnergy.Empty,
        default,
        null);
}

internal readonly record struct AetherWind(
    bool Available,
    double SpeedKts,
    double RelativeFromDeg,
    double CrosswindKts,
    double HeadwindKts)
{
    public static AetherWind Empty => default;

    public string CrosswindLabel
    {
        get
        {
            if (!Available)
                return "XWIND —";
            if (Math.Abs(CrosswindKts) < 0.05)
                return "XWIND 0.0";
            var side = CrosswindKts > 0 ? "R" : "L";
            return $"XWIND {side} {Math.Abs(CrosswindKts):0.0}";
        }
    }
}

internal readonly record struct AetherPath(
    double? PathAngleDeg,
    double? DescentAngleDeg,
    double? TargetAngleDeg,
    double? PathErrorDeg,
    double? DescentErrorDeg,
    AetherTone PathTone,
    AetherTone DescentTone,
    double? DistanceNm,
    double? ProgressPercent,
    bool InsideCollectionWindow)
{
    public static AetherPath Empty => new(
        null, null, null, null, null,
        AetherTone.Quiet, AetherTone.Quiet,
        null, null, false);

    public bool HasTarget => TargetAngleDeg is not null;
}

internal readonly record struct AetherEnergy(
    double? IasKts,
    double? TargetIasKts,
    double? IasDeltaKts,
    AetherTone IasTone,
    double? VerticalSpeedFpm,
    double? TargetVerticalSpeedFpm,
    AetherTone VerticalSpeedTone)
{
    public static AetherEnergy Empty => new(
        null, null, null, AetherTone.Quiet,
        null, null, AetherTone.Quiet);
}

internal readonly record struct AetherCamera(
    int? CameraState,
    int? CameraSubstate,
    int? CameraViewType,
    double? CameraPitchRadians,
    double? CameraYawRadians,
    double AircraftLatitude,
    double AircraftLongitude,
    double AircraftAltitudeFeet,
    double AircraftHeadingDeg,
    double AircraftPitchDeg);

internal readonly record struct AetherRunway(
    double ThresholdLatitude,
    double ThresholdLongitude,
    double ElevationFeet);
