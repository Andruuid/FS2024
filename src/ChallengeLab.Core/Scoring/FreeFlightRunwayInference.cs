using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public sealed record FreeFlightInferenceSettings(
    int NearbyAirportCount = 12,
    double NearbyAirportRadiusNm = 30,
    double ProvisionalMaximumThresholdDistanceNm = 20,
    double ProvisionalMaximumCourseErrorDeg = 60,
    double ProvisionalMaximumCrossTrackNm = 3,
    double ConfirmationMaximumThresholdDistanceNm = 12,
    double ConfirmationMaximumCourseErrorDeg = 30,
    double ConfirmationMaximumCrossTrackNm = 1.5,
    double MinimumGroundSpeedKts = 30,
    int StableSamplesToConfirm = 3);

public sealed record AirportDistance(AirportFacility Airport, double DistanceNm);

public sealed record FreeFlightTarget(
    RunwayEndFacility Runway,
    double ThresholdDistanceNm,
    double CourseErrorDeg,
    double CrossTrackNm,
    double? HeadingErrorDeg = null,
    string CourseSource = GroundMotionResolver.HeadingFallbackSource);

public sealed record FreeFlightInferenceResult(
    AirportDistance? NearestAirport,
    FreeFlightTarget? ProvisionalTarget,
    FreeFlightTarget? StableTarget,
    int StableSamples);

/// <summary>
/// Pure, deterministic airport/runway inference using position, runway geometry,
/// and aircraft course over the ground. A relaxed target is available immediately; only a target
/// inside the tighter confirmation envelope accumulates stable samples.
/// </summary>
public sealed class FreeFlightRunwayInference
{
    private readonly FreeFlightInferenceSettings _settings;
    private string? _stableKey;
    private int _stableSamples;

    public FreeFlightRunwayInference(FreeFlightInferenceSettings? settings = null)
        => _settings = settings ?? new FreeFlightInferenceSettings();

    public FreeFlightInferenceSettings Settings => _settings;
    public FreeFlightTarget? ProvisionalTarget { get; private set; }
    public FreeFlightTarget? StableTarget { get; private set; }

    /// <summary>
    /// Build a bounded facility-detail shortlist. The nearest airport is always retained,
    /// while the remaining slots favor airports ahead of the aircraft rather than private
    /// strips that merely happen to be closer off the approach path.
    /// </summary>
    public IReadOnlyList<AirportDistance> RankNearbyAirports(
        TelemetrySample sample,
        IEnumerable<AirportFacility> airports)
    {
        var ranked = airports
            .Where(a => IsFinitePosition(a.Latitude, a.Longitude))
            .Select(a =>
            {
                var distanceNm = GeoUtil.HaversineMetersPublic(
                    sample.Latitude,
                    sample.Longitude,
                    a.Latitude,
                    a.Longitude) / 1852.0;
                var bearingError = BearingErrorDeg(sample, a);
                var score = bearingError / 90.0 + distanceNm / _settings.NearbyAirportRadiusNm;
                return (Distance: new AirportDistance(a, distanceNm), BearingError: bearingError, Score: score);
            })
            .Where(x => x.Distance.DistanceNm <= _settings.NearbyAirportRadiusNm)
            .ToList();

        if (ranked.Count == 0)
            return [];

        var nearest = ranked.OrderBy(x => x.Distance.DistanceNm).First();
        var result = ranked
            .Where(x => !(x.Distance.Airport.Icao.Equals(nearest.Distance.Airport.Icao, StringComparison.OrdinalIgnoreCase)
                          && x.Distance.Airport.Region.Equals(nearest.Distance.Airport.Region, StringComparison.OrdinalIgnoreCase))
                        && x.BearingError <= 90)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Distance.Airport.Icao, StringComparer.Ordinal)
            .Take(Math.Max(0, _settings.NearbyAirportCount - 1))
            .Select(x => x.Distance)
            .Prepend(nearest.Distance)
            .ToList();

        return result;
    }

    public FreeFlightInferenceResult Update(
        TelemetrySample sample,
        IReadOnlyList<AirportDistance> nearbyAirports,
        IEnumerable<AirportRunwayFacility> detailedAirports,
        bool advanceStability = true)
    {
        var nearest = nearbyAirports.OrderBy(a => a.DistanceNm).FirstOrDefault();
        FreeFlightTarget? provisional = null;
        if (!sample.SimOnGround
            && sample.GroundSpeedKts >= _settings.MinimumGroundSpeedKts
            && nearbyAirports.Count > 0)
        {
            provisional = detailedAirports
                .SelectMany(RunwayFacilityGeometry.BuildEnds)
                .Select(end => EvaluateCandidate(sample, end))
                .Where(x => x is not null)
                .Cast<(FreeFlightTarget Target, double Rank)>()
                .OrderBy(x => x.Rank)
                .ThenBy(x => x.Target.Runway.Key, StringComparer.Ordinal)
                .Select(x => x.Target)
                .FirstOrDefault();
        }

        ProvisionalTarget = provisional;
        if (provisional is null || !IsInsideConfirmationEnvelope(provisional))
        {
            _stableKey = null;
            _stableSamples = 0;
            StableTarget = null;
        }
        else if (provisional.Runway.Key == _stableKey)
        {
            if (advanceStability)
                _stableSamples++;
        }
        else
        {
            _stableKey = provisional.Runway.Key;
            _stableSamples = advanceStability ? 1 : 0;
            StableTarget = null;
        }

        if (provisional is not null
            && _stableSamples >= _settings.StableSamplesToConfirm)
        {
            StableTarget = provisional;
        }

        return new FreeFlightInferenceResult(nearest, provisional, StableTarget, _stableSamples);
    }

    public bool IsInsideConfirmationEnvelope(FreeFlightTarget target) =>
        target.ThresholdDistanceNm <= _settings.ConfirmationMaximumThresholdDistanceNm
        && target.CourseErrorDeg <= _settings.ConfirmationMaximumCourseErrorDeg
        && target.CrossTrackNm <= _settings.ConfirmationMaximumCrossTrackNm;

    public void Reset()
    {
        ProvisionalTarget = null;
        StableTarget = null;
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
        if (!GroundMotionResolver.TryResolveCourse(sample, out var course))
            return null;

        var courseError = Math.Abs(GroundMotionResolver.NormalizeSigned(
            course.Degrees - end.HeadingTrueDeg));
        double? headingError = double.IsFinite(sample.HeadingTrueDeg)
            ? Math.Abs(GroundMotionResolver.NormalizeSigned(
                sample.HeadingTrueDeg - end.HeadingTrueDeg))
            : null;
        var crossTrackNm = Math.Abs(state.LateralMeters) / 1852.0;

        if (thresholdDistanceNm > _settings.ProvisionalMaximumThresholdDistanceNm
            || courseError > _settings.ProvisionalMaximumCourseErrorDeg
            || crossTrackNm > _settings.ProvisionalMaximumCrossTrackNm)
            return null;

        var rank = courseError / _settings.ProvisionalMaximumCourseErrorDeg
                   + crossTrackNm / _settings.ProvisionalMaximumCrossTrackNm
                   + thresholdDistanceNm / _settings.ProvisionalMaximumThresholdDistanceNm;
        return (new FreeFlightTarget(
            end,
            thresholdDistanceNm,
            courseError,
            crossTrackNm,
            headingError,
            course.Source), rank);
    }

    private static double BearingErrorDeg(TelemetrySample sample, AirportFacility airport)
    {
        if (Math.Abs(sample.Latitude - airport.Latitude) < 1e-10
            && Math.Abs(sample.Longitude - airport.Longitude) < 1e-10)
            return 0;

        var lat1 = sample.Latitude * Math.PI / 180.0;
        var lat2 = airport.Latitude * Math.PI / 180.0;
        var deltaLon = (airport.Longitude - sample.Longitude) * Math.PI / 180.0;
        var y = Math.Sin(deltaLon) * Math.Cos(lat2);
        var x = Math.Cos(lat1) * Math.Sin(lat2)
                - Math.Sin(lat1) * Math.Cos(lat2) * Math.Cos(deltaLon);
        var bearing = Math.Atan2(y, x) * 180.0 / Math.PI;
        return GroundMotionResolver.TryResolveCourse(sample, out var course)
            ? Math.Abs(GroundMotionResolver.NormalizeSigned(course.Degrees - bearing))
            : 180;
    }

    private static bool IsFinitePosition(double latitude, double longitude)
        => double.IsFinite(latitude) && double.IsFinite(longitude)
                                     && latitude is >= -90 and <= 90
                                     && longitude is >= -180 and <= 180;

}
