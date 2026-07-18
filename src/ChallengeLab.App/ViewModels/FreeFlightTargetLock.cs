using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.ViewModels;

/// <summary>
/// Immutable ownership marker for an acquired Free Flight runway. The guidance challenge is
/// built once so both HUDs and the later scoring session use the same normalized geometry.
/// </summary>
internal sealed record FreeFlightTargetLock(
    FreeFlightTarget Target,
    ChallengeConfig GuidanceChallenge,
    DateTimeOffset AcquiredAt,
    FreeFlightTargetAcquisition Acquisition,
    bool WasLateAcquisition)
{
    public string Key => Target.Runway.Key;
    public RunwayConfig Runway => GuidanceChallenge.Runway;

    public static FreeFlightTargetLock Acquire(
        FreeFlightTarget target,
        TelemetrySample sample,
        RunwayReferenceResolver runwayResolver,
        bool lateAcquisition)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(runwayResolver);

        return new FreeFlightTargetLock(
            target,
            FreeFlightChallengeFactory.Create(target, sample, runwayResolver),
            sample.Timestamp,
            new FreeFlightTargetAcquisition(
                sample.Latitude,
                sample.Longitude,
                sample.AltitudeFeet,
                sample.HeadingTrueDeg,
                sample.GroundTrackTrueDeg,
                sample.GroundSpeedKts,
                sample.AirspeedKts,
                sample.GearHandlePosition,
                sample.AircraftTitle?.Trim()),
            lateAcquisition);
    }
}

internal sealed record FreeFlightTargetAcquisition(
    double Latitude,
    double Longitude,
    double AltitudeFeet,
    double HeadingTrueDeg,
    double? GroundTrackTrueDeg,
    double GroundSpeedKts,
    double AirspeedKts,
    double GearHandlePosition,
    string? AircraftTitle);
