using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Facilities;

/// <summary>Nearest airport resolved offline from the bundled OurAirports snapshot.</summary>
public sealed record NearestAirportInfo(
    string Ident,
    string Name,
    string Municipality,
    string CountryCode,
    double Latitude,
    double Longitude,
    double DistanceNm);

/// <summary>
/// Offline nearest-airport lookup over the bundled OurAirports airports.csv
/// (ident, name, municipality, iso_country — columns the runway catalog does not parse).
/// Used to label flight-state snapshots without waiting for the live facilities catalog.
/// </summary>
public sealed class OurAirportsAirportIndex
{
    private const double MetersPerNauticalMile = 1852.0;

    private static readonly Lazy<OurAirportsAirportIndex> DefaultIndex = new(LoadDefaultCore);

    private readonly List<AirportEntry> _airports;

    private OurAirportsAirportIndex(List<AirportEntry> airports, string? loadError)
    {
        _airports = airports;
        LoadError = loadError;
    }

    public static OurAirportsAirportIndex Default => DefaultIndex.Value;
    public string? LoadError { get; }
    public int AirportCount => _airports.Count;
    public bool IsAvailable => _airports.Count > 0;

    public static OurAirportsAirportIndex Load(string airportsCsvPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(airportsCsvPath);
        if (!File.Exists(airportsCsvPath))
            throw new FileNotFoundException("OurAirports airports.csv was not found.", airportsCsvPath);

        var airports = new List<AirportEntry>(90_000);
        foreach (var row in OurAirportsRunwayCatalog.ReadTable(airportsCsvPath))
        {
            var type = row.Get("type");
            if (string.Equals(type, "closed", StringComparison.OrdinalIgnoreCase))
                continue;

            var ident = OurAirportsRunwayCatalog.NormalizeAirport(row.Get("ident"));
            var latitude = OurAirportsRunwayCatalog.ParseLatitude(row.Get("latitude_deg"));
            var longitude = OurAirportsRunwayCatalog.ParseLongitude(row.Get("longitude_deg"));
            if (string.IsNullOrWhiteSpace(ident) || latitude is null || longitude is null)
                continue;

            airports.Add(new AirportEntry(
                ident,
                row.Get("name").Trim(),
                row.Get("municipality").Trim(),
                row.Get("iso_country").Trim().ToUpperInvariant(),
                latitude.Value,
                longitude.Value));
        }

        return new OurAirportsAirportIndex(airports, loadError: null);
    }

    /// <summary>
    /// Nearest open airport/heliport to the given position, or null when the index is
    /// unavailable. Cheap equirectangular scan picks candidates; haversine decides.
    /// </summary>
    public NearestAirportInfo? FindNearest(double latitude, double longitude)
    {
        if (_airports.Count == 0) return null;

        var cosLat = Math.Cos(latitude * Math.PI / 180.0);
        // Keep a handful of approximate best candidates, then refine with haversine —
        // the approximation is direction-safe at these scales but not exact.
        Span<int> bestIndices = stackalloc int[8];
        Span<double> bestScores = stackalloc double[8];
        var bestCount = 0;

        for (var i = 0; i < _airports.Count; i++)
        {
            var airport = _airports[i];
            var dLat = airport.Latitude - latitude;
            var dLon = airport.Longitude - longitude;
            if (dLon > 180) dLon -= 360;
            else if (dLon < -180) dLon += 360;
            var x = dLon * cosLat;
            var score = dLat * dLat + x * x;

            if (bestCount < bestIndices.Length)
            {
                bestIndices[bestCount] = i;
                bestScores[bestCount] = score;
                bestCount++;
                continue;
            }

            var worst = 0;
            for (var j = 1; j < bestScores.Length; j++)
                if (bestScores[j] > bestScores[worst]) worst = j;
            if (score < bestScores[worst])
            {
                bestScores[worst] = score;
                bestIndices[worst] = i;
            }
        }

        AirportEntry? nearest = null;
        var nearestMeters = double.MaxValue;
        for (var j = 0; j < bestCount; j++)
        {
            var airport = _airports[bestIndices[j]];
            var meters = GeoUtil.HaversineMetersPublic(
                latitude, longitude, airport.Latitude, airport.Longitude);
            if (meters < nearestMeters)
            {
                nearestMeters = meters;
                nearest = airport;
            }
        }

        if (nearest is null) return null;
        return new NearestAirportInfo(
            nearest.Ident,
            nearest.Name,
            nearest.Municipality,
            nearest.CountryCode,
            nearest.Latitude,
            nearest.Longitude,
            nearestMeters / MetersPerNauticalMile);
    }

    private static OurAirportsAirportIndex LoadDefaultCore()
    {
        foreach (var directory in OurAirportsRunwayCatalog.DefaultDirectories())
        {
            var airports = Path.Combine(directory, "airports.csv");
            if (!File.Exists(airports))
                continue;
            try
            {
                return Load(airports);
            }
            catch (Exception ex)
            {
                return new OurAirportsAirportIndex(
                    new List<AirportEntry>(),
                    $"Could not load OurAirports airports.csv from '{directory}': {ex.Message}");
            }
        }

        return new OurAirportsAirportIndex(
            new List<AirportEntry>(),
            "Bundled OurAirports airports.csv was not found.");
    }

    private sealed record AirportEntry(
        string Ident,
        string Name,
        string Municipality,
        string CountryCode,
        double Latitude,
        double Longitude);
}
