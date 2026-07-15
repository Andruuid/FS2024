using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public sealed record FreeFlightInferenceSettings(
    int NearbyAirportCount = 5,
    double NearbyAirportRadiusNm = 25,
    double MaximumThresholdDistanceNm = 12,
    double MaximumTrackErrorDeg = 30,
    double MaximumCrossTrackNm = 1.5,
    double MinimumGroundSpeedKts = 30,
    int StableSamplesToLock = 3);

public sealed record AirportDistance(AirportFacility Airport, double DistanceNm);

public sealed record FreeFlightTarget(
    RunwayEndFacility Runway,
    double ThresholdDistanceNm,
    double TrackErrorDeg,
    double CrossTrackNm);

public sealed record FreeFlightInferenceResult(
    AirportDistance? NearestAirport,
    FreeFlightTarget? Candidate,
    FreeFlightTarget? LockedTarget,
    int StableSamples);

/// <summary>
/// Pure, deterministic airport/runway inference. It deliberately uses ground track rather
/// than aircraft heading so a wind-crabbed final still selects the correct runway.
/// </summary>
public sealed class FreeFlightRunwayInference
{
    private readonly FreeFlightInferenceSettings _settings;
    private string? _stableKey;
    private int _stableSamples;

    public FreeFlightRunwayInference(FreeFlightInferenceSettings? settings = null)
        => _settings = settings ?? new FreeFlightInferenceSettings();

    public FreeFlightInferenceSettings Settings => _settings;
    public FreeFlightTarget? LockedTarget { get; private set; }

    public IReadOnlyList<AirportDistance> RankNearbyAirports(
        TelemetrySample sample,
        IEnumerable<AirportFacility> airports)
        => airports
            .Where(a => IsFinitePosition(a.Latitude, a.Longitude))
            .Select(a => new AirportDistance(
                a,
                GeoUtil.HaversineMetersPublic(
                    sample.Latitude,
                    sample.Longitude,
                    a.Latitude,
                    a.Longitude) / 1852.0))
            .OrderBy(a => a.DistanceNm)
            .Take(_settings.NearbyAirportCount)
            .ToList();

    public FreeFlightInferenceResult Update(
        TelemetrySample sample,
        IReadOnlyList<AirportDistance> nearbyAirports,
        IEnumerable<AirportRunwayFacility> detailedAirports)
    {
        var nearest = nearbyAirports.FirstOrDefault();
        if (LockedTarget is not null)
            return new FreeFlightInferenceResult(nearest, LockedTarget, LockedTarget, _stableSamples);

        FreeFlightTarget? candidate = null;
        if (!sample.SimOnGround
            && sample.GroundSpeedKts >= _settings.MinimumGroundSpeedKts
            && nearbyAirports.Any(a => a.DistanceNm <= _settings.NearbyAirportRadiusNm))
        {
            candidate = detailedAirports
                .SelectMany(RunwayFacilityGeometry.BuildEnds)
                .Select(end => EvaluateCandidate(sample, end))
                .Where(x => x is not null)
                .Cast<(FreeFlightTarget Target, double Rank)>()
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Target.Runway.Key, StringComparer.Ordinal)
                .Select(x => x.Target)
                .FirstOrDefault();
        }

        if (candidate is null)
        {
            _stableKey = null;
            _stableSamples = 0;
        }
        else if (candidate.Runway.Key == _stableKey)
        {
            _stableSamples++;
        }
        else
        {
            _stableKey = candidate.Runway.Key;
            _stableSamples = 1;
        }

        if (candidate is not null && _stableSamples >= _settings.StableSamplesToLock)
            LockedTarget = candidate;

        return new FreeFlightInferenceResult(nearest, candidate, LockedTarget, _stableSamples);
    }

    public void Reset()
    {
        LockedTarget = null;
        _stableKey = null;
        _stableSamples = 0;
    }

    private (FreeFlightTarget Target, double Rank)? EvaluateCandidate(
        TelemetrySample sample,
        RunwayEndFacility end)
    {
        var runway = end.ToRunwayConfig();
        if (!RunwayPathGeometry.TryGetState(sample, runway, out var state)
            || state.ApproachDistanceMeters <= 0)
            return null;

        var thresholdDistanceNm = GeoUtil.HaversineMetersPublic(
            sample.Latitude,
            sample.Longitude,
            end.ThresholdLatitude,
            end.ThresholdLongitude) / 1852.0;
        var trackError = Math.Abs(NormalizeSigned(sample.GroundTrackTrueDeg - end.HeadingTrueDeg));
        var crossTrackNm = Math.Abs(state.LateralMeters) / 1852.0;

        if (thresholdDistanceNm > _settings.MaximumThresholdDistanceNm
            || trackError > _settings.MaximumTrackErrorDeg
            || crossTrackNm > _settings.MaximumCrossTrackNm)
            return null;

        var rank = trackError / _settings.MaximumTrackErrorDeg
                   + crossTrackNm / _settings.MaximumCrossTrackNm
                   + thresholdDistanceNm / _settings.MaximumThresholdDistanceNm;
        return (new FreeFlightTarget(end, thresholdDistanceNm, trackError, crossTrackNm), rank);
    }

    private static bool IsFinitePosition(double latitude, double longitude)
        => double.IsFinite(latitude) && double.IsFinite(longitude)
                                     && latitude is >= -90 and <= 90
                                     && longitude is >= -180 and <= 180;

    private static double NormalizeSigned(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }
}
