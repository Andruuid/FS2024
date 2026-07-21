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
    ApproachSpeedReading ApproachSpeed,
    RelativeWindReading Wind,
    double? CrabAngleDeg,
    double? TargetGlideslopeDeg,
    double? RadioAltitudeFeet,
    HudViewContext View)
{
    public static HudPresentationFrame Disconnected(long sequence) => new(
        sequence,
        DateTimeOffset.UtcNow,
        IsConnected: false,
        IsFlightActive: false,
        EmptyGuidance,
        default,
        default,
        null,
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
        double approachPathMaxDistNm,
        double? vappKts = null)
    {
        var guidance = LandingMonitorCalculator.Calculate(
            sample,
            runway,
            targetAirspeedKts,
            approachPathMinDistNm,
            approachPathMaxDistNm);
        return FromGuidance(sample, isConnected, sequence, runway, guidance, vappKts);
    }

    public static HudPresentationFrame FromGuidance(
        TelemetrySample sample,
        bool isConnected,
        long sequence,
        RunwayConfig? runway,
        LandingMonitorReading guidance,
        double? vappKts = null)
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
            ApproachSpeedPresentation.Calculate(guidance.AirspeedKts, vappKts),
            RelativeWindCalculator.Calculate(sample, isConnected),
            CrabAnglePresentation.FromSample(sample),
            runway is null
                ? null
                : RunwayPathGeometry.SanitizeGlideslopeDeg(runway.GlideslopeDeg),
            ResolveRadioAltitudeFeet(sample),
            HudViewContext.FromSample(sample, runway));
    }

    private static double? ResolveRadioAltitudeFeet(TelemetrySample sample) =>
        sample.RadioHeightAvailable && double.IsFinite(sample.RadioHeightFeet)
            ? Math.Max(0, sample.RadioHeightFeet)
            : null;

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
    internal const double EnterUpwardDegrees = 35;
    internal const double ExitUpwardDegrees = 45;
    internal const double EnterDownwardDegrees = CockpitLookVisibilityPolicy.EnterDownwardDegrees;
    internal const double ExitDownwardDegrees = CockpitLookVisibilityPolicy.ExitDownwardDegrees;
    internal const double NearRunwayDistanceMeters = CockpitLookVisibilityPolicy.NearRunwayDistanceMeters;

    private readonly CockpitLookVisibilityPolicy _policy = new(
        EnterUpwardDegrees,
        ExitUpwardDegrees);

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

/// <summary>HUD1-only trailing sample average for the numeric vertical-speed readout.</summary>
internal sealed class HudVerticalSpeedSmoother
{
    internal const double DefaultWindowSeconds = 1.5;

    private readonly TimeSpan _window;
    private readonly Queue<(DateTimeOffset Timestamp, double Value)> _samples = new();
    private DateTimeOffset? _latestTimestamp;
    private double _sum;

    public HudVerticalSpeedSmoother(double windowSeconds = DefaultWindowSeconds)
    {
        if (!double.IsFinite(windowSeconds) || windowSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowSeconds));

        _window = TimeSpan.FromSeconds(windowSeconds);
    }

    public double? Update(DateTimeOffset timestamp, double? verticalSpeedFpm)
    {
        if (verticalSpeedFpm is not { } value || !double.IsFinite(value))
        {
            Reset();
            return null;
        }

        if (_latestTimestamp is { } latest && timestamp < latest)
            Reset();

        _latestTimestamp = timestamp;
        _samples.Enqueue((timestamp, value));
        _sum += value;

        var cutoff = timestamp - _window;
        while (_samples.TryPeek(out var sample) && sample.Timestamp < cutoff)
        {
            _samples.Dequeue();
            _sum -= sample.Value;
        }

        return _samples.Count == 0 ? null : _sum / _samples.Count;
    }

    public void Reset()
    {
        _samples.Clear();
        _latestTimestamp = null;
        _sum = 0;
    }
}
