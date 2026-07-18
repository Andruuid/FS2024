using ChallengeLab.Core.Config;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Facilities;

/// <summary>Minimal airport record returned by the SimConnect worldwide facilities list.</summary>
public sealed record AirportFacility(
    string Icao,
    string Region,
    double Latitude,
    double Longitude,
    double AltitudeMeters,
    string Country = "");

/// <summary>A runway start/threshold position returned as an AIRPORT/START child.</summary>
public sealed record RunwayStartFacility(
    double Latitude,
    double Longitude,
    double AltitudeMeters,
    double HeadingDeg,
    int Number,
    int Designator,
    int Type);

/// <summary>Optional runway pavement child returned by the SimConnect facility API.</summary>
public sealed record RunwayPavementFacility(
    double LengthMeters,
    double WidthMeters,
    bool Enabled);

/// <summary>One PAPI/VASI installation attached to a runway end.</summary>
public sealed record RunwayVisualSlopeFacility(
    int Type,
    double BiasXMeters,
    double BiasZMeters,
    double SpacingMeters,
    double AngleDeg);

/// <summary>One physical, bidirectional runway returned as an AIRPORT/RUNWAY child.</summary>
public sealed record RunwayFacility(
    double CenterLatitude,
    double CenterLongitude,
    double AltitudeMeters,
    double HeadingTrueDeg,
    double LengthMeters,
    double WidthMeters,
    int Surface,
    int PrimaryNumber,
    int PrimaryDesignator,
    int SecondaryNumber,
    int SecondaryDesignator,
    bool PrimaryClosed,
    bool SecondaryClosed,
    bool PrimaryLandingAllowed,
    bool SecondaryLandingAllowed,
    /// <summary>VASI/PAPI angles in definition order: primary L/R, secondary L/R (null if none).</summary>
    IReadOnlyList<double?>? VasiAnglesDeg = null,
    RunwayPavementFacility? PrimaryThreshold = null,
    RunwayPavementFacility? SecondaryThreshold = null,
    /// <summary>Complete VASI/PAPI records in definition order: primary L/R, secondary L/R.</summary>
    IReadOnlyList<RunwayVisualSlopeFacility?>? VisualSlopeSystems = null);

/// <summary>Detailed airport data assembled from the SimConnect facility-data stream.</summary>
public sealed record AirportRunwayFacility(
    AirportFacility Airport,
    IReadOnlyList<RunwayFacility> Runways,
    IReadOnlyList<RunwayStartFacility> Starts);

/// <summary>A single landing direction derived from one end of a physical runway.</summary>
public sealed record RunwayEndFacility(
    AirportFacility Airport,
    string RunwayId,
    double ThresholdLatitude,
    double ThresholdLongitude,
    double ElevationFeet,
    double HeadingTrueDeg,
    double LengthMeters,
    double WidthMeters,
    int Surface,
    bool IsWater,
    double GlideslopeDeg = 3.0,
    string GlideslopeSource = "default",
    string CountryCode = "",
    double DisplacedThresholdMeters = 0,
    double? LandingDistanceAvailableMeters = null)
{
    public string Key => $"{Airport.Icao}:{RunwayId}";

    public RunwayConfig ToRunwayConfig() => new()
    {
        AirportIcao = Airport.Icao,
        RunwayId = RunwayId,
        ThresholdLatitude = ThresholdLatitude,
        ThresholdLongitude = ThresholdLongitude,
        HeadingTrueDeg = HeadingTrueDeg,
        ElevationFeet = ElevationFeet,
        LengthM = LengthMeters,
        WidthM = WidthMeters,
        GlideslopeDeg = GlideslopeDeg,
        GlideslopeSource = GlideslopeSource,
        CountryCode = CountryCode,
        DisplacedThresholdM = DisplacedThresholdMeters,
        LandingDistanceAvailableM = LandingDistanceAvailableMeters,
        RunwayDataSource = "SimConnect"
    };
}

/// <summary>Converts physical runway records into open, landing-capable runway ends.</summary>
public static class RunwayFacilityGeometry
{
    private const double FeetPerMeter = 3.280839895;
    private const double EarthRadiusMeters = 6_371_000;

    public static IReadOnlyList<RunwayEndFacility> BuildEnds(AirportRunwayFacility airport)
    {
        var result = new List<RunwayEndFacility>();
        foreach (var runway in airport.Runways)
        {
            if (runway.LengthMeters <= 0 || runway.WidthMeters <= 0)
                continue;

            if (!runway.PrimaryClosed && runway.PrimaryLandingAllowed)
            {
                result.Add(BuildEnd(
                    airport,
                    runway,
                    runway.PrimaryNumber,
                    runway.PrimaryDesignator,
                    Normalize(runway.HeadingTrueDeg),
                    primaryEnd: true));
            }

            if (!runway.SecondaryClosed && runway.SecondaryLandingAllowed)
            {
                result.Add(BuildEnd(
                    airport,
                    runway,
                    runway.SecondaryNumber,
                    runway.SecondaryDesignator,
                    Normalize(runway.HeadingTrueDeg + 180),
                    primaryEnd: false));
            }
        }

        return result;
    }

    public static string FormatRunwayId(int number, int designator, double headingTrueDeg)
    {
        var numberText = number switch
        {
            >= 1 and <= 36 => number.ToString("00"),
            37 => "N",
            38 => "NE",
            39 => "E",
            40 => "SE",
            41 => "S",
            42 => "SW",
            43 => "W",
            44 => "NW",
            _ => FormatHeadingNumber(headingTrueDeg)
        };
        var suffix = designator switch
        {
            1 => "L",
            2 => "R",
            3 => "C",
            4 => "W",
            5 => "A",
            6 => "B",
            _ => ""
        };
        return numberText + suffix;
    }

    public static bool IsWaterSurface(int surface) => surface is 2 or >= 26 and <= 31;

    private static RunwayEndFacility BuildEnd(
        AirportRunwayFacility airport,
        RunwayFacility runway,
        int number,
        int designator,
        double heading,
        bool primaryEnd)
    {
        var thresholdFacility = primaryEnd ? runway.PrimaryThreshold : runway.SecondaryThreshold;
        var thresholdOffsetMeters = thresholdFacility is { Enabled: true }
            ? Math.Max(0, thresholdFacility.LengthMeters)
            : 0;
        var start = airport.Starts.FirstOrDefault(s =>
            s.Type is 1 or 2 && s.Number == number && s.Designator == designator);

        double latitude;
        double longitude;
        double altitudeMeters;
        if (start is not null)
        {
            latitude = start.Latitude;
            longitude = start.Longitude;
            altitudeMeters = start.AltitudeMeters;
        }
        else
        {
            // A landing threshold is half a runway behind its landing-course heading.
            var outwardHeading = heading + 180;
            (latitude, longitude) = Project(
                runway.CenterLatitude,
                runway.CenterLongitude,
                Normalize(outwardHeading),
                runway.LengthMeters / 2.0);
            if (thresholdOffsetMeters > 0)
            {
                (latitude, longitude) = Project(
                    latitude,
                    longitude,
                    heading,
                    thresholdOffsetMeters);
            }
            altitudeMeters = runway.AltitudeMeters;
        }

        // VASI order from facility def: primary L, primary R, secondary L, secondary R.
        var vasi = runway.VasiAnglesDeg;
        var left = primaryEnd
            ? AngleAt(vasi, 0)
            : AngleAt(vasi, 2);
        var right = primaryEnd
            ? AngleAt(vasi, 1)
            : AngleAt(vasi, 3);
        var runwayId = FormatRunwayId(number, designator, heading);
        // Catalog (ICAO+runway steep approaches) → VASI/PAPI → 3° default.
        var gs = GlideslopeAngleResolver.ResolveFreeFlight(
            airport.Airport.Icao,
            runwayId,
            left,
            right);

        return new RunwayEndFacility(
            airport.Airport,
            runwayId,
            latitude,
            longitude,
            altitudeMeters * FeetPerMeter,
            heading,
            runway.LengthMeters,
            runway.WidthMeters,
            runway.Surface,
            IsWaterSurface(runway.Surface),
            gs.Degrees,
            gs.Source,
            airport.Airport.Country,
            thresholdOffsetMeters,
            Math.Max(0, runway.LengthMeters - thresholdOffsetMeters));
    }

    private static double? AngleAt(IReadOnlyList<double?>? angles, int index)
    {
        if (angles is null || index < 0 || index >= angles.Count)
            return null;
        return angles[index];
    }

    private static (double Latitude, double Longitude) Project(
        double latitude,
        double longitude,
        double bearingDeg,
        double distanceMeters)
    {
        var angular = distanceMeters / EarthRadiusMeters;
        var bearing = bearingDeg * Math.PI / 180.0;
        var lat1 = latitude * Math.PI / 180.0;
        var lon1 = longitude * Math.PI / 180.0;
        var lat2 = Math.Asin(
            Math.Sin(lat1) * Math.Cos(angular)
            + Math.Cos(lat1) * Math.Sin(angular) * Math.Cos(bearing));
        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearing) * Math.Sin(angular) * Math.Cos(lat1),
            Math.Cos(angular) - Math.Sin(lat1) * Math.Sin(lat2));
        return (lat2 * 180.0 / Math.PI, NormalizeLongitude(lon2 * 180.0 / Math.PI));
    }

    private static double Normalize(double deg)
    {
        deg %= 360;
        return deg < 0 ? deg + 360 : deg;
    }

    private static string FormatHeadingNumber(double headingTrueDeg)
    {
        var number = (int)Math.Round(Normalize(headingTrueDeg) / 10.0) % 36;
        return (number == 0 ? 36 : number).ToString("00");
    }

    private static double NormalizeLongitude(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }
}
