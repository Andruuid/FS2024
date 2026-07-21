namespace ChallengeLab.Core.FlightLoading;

public sealed class FlightLoadReadinessEvaluator
{
    public const double MaxHorizontalErrorM = 1_000;
    public const double MaxAltitudeErrorFeet = 500;
    public const double MaxAirspeedErrorKts = 30;
    public const double MaxHeadingErrorDegrees = 20;

    private readonly FltFileMetadata _target;
    private readonly int _requiredSamples;
    private DateTimeOffset? _flightLoadedUtc;

    public FlightLoadReadinessEvaluator(FltFileMetadata target, int requiredConsecutiveSamples = 3)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _requiredSamples = Math.Max(1, requiredConsecutiveSamples);
    }

    public int ConsecutiveValidSamples { get; private set; }
    public bool IsReady => _flightLoadedUtc is not null && ConsecutiveValidSamples >= _requiredSamples;
    public FlightLoadSampleValidation? LastValidation { get; private set; }
    public FlightLoadObservation? LastObservation { get; private set; }

    public void MarkFlightLoaded(DateTimeOffset timestampUtc)
    {
        _flightLoadedUtc = timestampUtc;
        ConsecutiveValidSamples = 0;
        LastValidation = null;
        LastObservation = null;
    }

    public bool Observe(FlightLoadObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (_flightLoadedUtc is null || observation.TimestampUtc < _flightLoadedUtc.Value)
            return false;

        LastObservation = observation;
        LastValidation = Validate(_target, observation);
        ConsecutiveValidSamples = LastValidation.IsMatch ? ConsecutiveValidSamples + 1 : 0;
        return IsReady;
    }

    public static FlightLoadSampleValidation Validate(
        FltFileMetadata target,
        FlightLoadObservation observation)
    {
        var issues = new List<string>();
        double? horizontalError = null;
        double? altitudeError = null;
        double? airspeedError = null;
        double? headingError = null;

        if (string.IsNullOrWhiteSpace(observation.AircraftTitle)
            || string.IsNullOrWhiteSpace(target.AircraftTitle)
            || !FlightLoadSafetyPolicy.AircraftTitlesMatch(observation.AircraftTitle, target.AircraftTitle))
            issues.Add($"Aircraft TITLE is '{observation.AircraftTitle ?? "unavailable"}', expected '{target.AircraftTitle ?? "unknown"}'.");

        if (target.Latitude is null || target.Longitude is null
            || observation.Latitude is null || observation.Longitude is null
            || !double.IsFinite(observation.Latitude.Value) || !double.IsFinite(observation.Longitude.Value))
        {
            issues.Add("Position is unavailable.");
        }
        else
        {
            horizontalError = HaversineMeters(
                target.Latitude.Value, target.Longitude.Value,
                observation.Latitude.Value, observation.Longitude.Value);
            if (horizontalError > MaxHorizontalErrorM)
                issues.Add($"Position is {horizontalError:0} m from the FLT target (limit {MaxHorizontalErrorM:0} m).");
        }

        if (target.AltitudeFeet is null || observation.AltitudeFeet is null
            || !double.IsFinite(observation.AltitudeFeet.Value))
        {
            issues.Add("Altitude is unavailable.");
        }
        else
        {
            altitudeError = Math.Abs(observation.AltitudeFeet.Value - target.AltitudeFeet.Value);
            if (altitudeError > MaxAltitudeErrorFeet)
                issues.Add($"Altitude error is {altitudeError:0} ft (limit {MaxAltitudeErrorFeet:0} ft).");
        }

        if (target.OnGround is not null && observation.OnGround != target.OnGround)
            issues.Add($"On-ground state is {Display(observation.OnGround)}, expected {Display(target.OnGround)}.");

        if (target.HeadingDegrees is not null)
        {
            if (observation.HeadingTrueDeg is null || !double.IsFinite(observation.HeadingTrueDeg.Value))
            {
                issues.Add("Heading is unavailable.");
            }
            else
            {
                headingError = HeadingDifference(target.HeadingDegrees.Value, observation.HeadingTrueDeg.Value);
                if (headingError > MaxHeadingErrorDegrees)
                    issues.Add($"Heading error is {headingError:0}Â° (limit {MaxHeadingErrorDegrees:0}Â°).");
            }
        }

        if (target.AirspeedKts is > 1)
        {
            if (observation.AirspeedKts is null || !double.IsFinite(observation.AirspeedKts.Value))
            {
                issues.Add("Indicated airspeed is unavailable.");
            }
            else
            {
                airspeedError = Math.Abs(observation.AirspeedKts.Value - target.AirspeedKts.Value);
                if (airspeedError > MaxAirspeedErrorKts)
                    issues.Add($"IAS error is {airspeedError:0} kt (limit {MaxAirspeedErrorKts:0} kt).");
            }
        }

        return new FlightLoadSampleValidation
        {
            IsMatch = issues.Count == 0,
            HorizontalErrorM = horizontalError,
            AltitudeErrorFeet = altitudeError,
            AirspeedErrorKts = airspeedError,
            HeadingErrorDegrees = headingError,
            Issues = issues
        };
    }

    private static double HeadingDifference(double first, double second)
    {
        var difference = Math.Abs((first - second) % 360d);
        return difference > 180d ? 360d - difference : difference;
    }

    private static string Display(bool? value) => value is null ? "unavailable" : value.Value ? "ground" : "airborne";

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadiusM = 6_371_000;
        var p1 = lat1 * Math.PI / 180d;
        var p2 = lat2 * Math.PI / 180d;
        var dp = (lat2 - lat1) * Math.PI / 180d;
        var dl = (lon2 - lon1) * Math.PI / 180d;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2)
                + Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * earthRadiusM * Math.Asin(Math.Sqrt(a));
    }
}
