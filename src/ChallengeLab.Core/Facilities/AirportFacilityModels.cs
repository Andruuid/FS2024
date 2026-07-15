using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Facilities;

/// <summary>Minimal airport record returned by the SimConnect worldwide facilities list.</summary>
public sealed record AirportFacility(
    string Icao,
    string Region,
    double Latitude,
    double Longitude,
    double AltitudeMeters);

/// <summary>A runway start/threshold position returned as an AIRPORT/START child.</summary>
public sealed record RunwayStartFacility(
    double Latitude,
    double Longitude,
    double AltitudeMeters,
    double HeadingDeg,
    int Number,
    int Designator,
    int Type);

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
    bool SecondaryLandingAllowed);

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
    bool IsWater)
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
        WidthM = WidthMeters
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
                    Normalize(runway.HeadingTrueDeg)));
            }

            if (!runway.SecondaryClosed && runway.SecondaryLandingAllowed)
            {
                result.Add(BuildEnd(
                    airport,
                    runway,
                    runway.SecondaryNumber,
                    runway.SecondaryDesignator,
                    Normalize(runway.HeadingTrueDeg + 180)));
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
        double heading)
    {
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
            altitudeMeters = runway.AltitudeMeters;
        }

        return new RunwayEndFacility(
            airport.Airport,
            FormatRunwayId(number, designator, heading),
            latitude,
            longitude,
            altitudeMeters * FeetPerMeter,
            heading,
            runway.LengthMeters,
            runway.WidthMeters,
            runway.Surface,
            IsWaterSurface(runway.Surface));
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
