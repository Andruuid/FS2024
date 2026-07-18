using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ChallengeLab.Core.Facilities;

/// <summary>One landing direction resolved from the bundled OurAirports CSV snapshot.</summary>
public sealed record OurAirportsRunwayEnd(
    string AirportIdent,
    string CountryCode,
    string RunwayId,
    double PhysicalEndLatitude,
    double PhysicalEndLongitude,
    double UsableThresholdLatitude,
    double UsableThresholdLongitude,
    double ElevationFeet,
    double HeadingTrueDeg,
    double FullLengthFeet,
    double WidthFeet,
    double DisplacedThresholdFeet,
    double LandingDistanceAvailableFeet,
    string SnapshotId);

/// <summary>
/// Streaming, read-only index over the bundled OurAirports airports.csv and runways.csv files.
/// Exact airport/runway identifiers are intentionally required; renamed runways fall back to
/// simulator or stored geometry instead of being guessed.
/// </summary>
public sealed class OurAirportsRunwayCatalog
{
    private static readonly Lazy<OurAirportsRunwayCatalog> DefaultCatalog = new(LoadDefaultCore);
    private readonly Dictionary<string, OurAirportsRunwayEnd> _ends;

    private OurAirportsRunwayCatalog(
        Dictionary<string, OurAirportsRunwayEnd> ends,
        string snapshotId,
        string? loadError,
        string? dataDirectory)
    {
        _ends = ends;
        SnapshotId = snapshotId;
        LoadError = loadError;
        DataDirectory = dataDirectory;
    }

    public static OurAirportsRunwayCatalog Default => DefaultCatalog.Value;
    public string SnapshotId { get; }
    public string? LoadError { get; }
    public string? DataDirectory { get; }
    public int RunwayEndCount => _ends.Count;
    public bool IsAvailable => _ends.Count > 0;

    public static OurAirportsRunwayCatalog Load(string airportsCsvPath, string runwaysCsvPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(airportsCsvPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runwaysCsvPath);
        if (!File.Exists(airportsCsvPath))
            throw new FileNotFoundException("OurAirports airports.csv was not found.", airportsCsvPath);
        if (!File.Exists(runwaysCsvPath))
            throw new FileNotFoundException("OurAirports runways.csv was not found.", runwaysCsvPath);

        var airportsById = new Dictionary<string, AirportRow>(StringComparer.OrdinalIgnoreCase);
        var airportsByIdent = new Dictionary<string, AirportRow>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadTable(airportsCsvPath))
        {
            var id = row.Get("id");
            var ident = NormalizeAirport(row.Get("ident"));
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(ident))
                continue;

            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ident };
            AddAlias(aliases, row.Get("icao_code"));
            AddAlias(aliases, row.Get("gps_code"));
            var airport = new AirportRow(
                id,
                ident,
                NormalizeCountry(row.Get("iso_country")),
                ParseFinite(row.Get("elevation_ft")),
                aliases);
            airportsById[id] = airport;
            airportsByIdent[ident] = airport;
        }

        var snapshot = BuildSnapshotId(airportsCsvPath, runwaysCsvPath);
        var ends = new Dictionary<string, OurAirportsRunwayEnd>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in ReadTable(runwaysCsvPath))
        {
            if (row.Get("closed") == "1")
                continue;

            var airportIdent = NormalizeAirport(row.Get("airport_ident"));
            AirportRow? airport = null;
            var airportRef = row.Get("airport_ref");
            if (!string.IsNullOrWhiteSpace(airportRef))
                airportsById.TryGetValue(airportRef, out airport);
            if (airport is null && !string.IsNullOrWhiteSpace(airportIdent))
                airportsByIdent.TryGetValue(airportIdent, out airport);
            if (string.IsNullOrWhiteSpace(airportIdent))
                airportIdent = airport?.Ident ?? "";
            if (string.IsNullOrWhiteSpace(airportIdent))
                continue;

            var length = ParseFinite(row.Get("length_ft"));
            if (length is not > 0)
                continue;
            var width = ParseFinite(row.Get("width_ft")) ?? 0;

            var lowLat = ParseLatitude(row.Get("le_latitude_deg"));
            var lowLon = ParseLongitude(row.Get("le_longitude_deg"));
            var highLat = ParseLatitude(row.Get("he_latitude_deg"));
            var highLon = ParseLongitude(row.Get("he_longitude_deg"));
            var lowHeading = ParseHeading(row.Get("le_heading_degT"));
            var highHeading = ParseHeading(row.Get("he_heading_degT"));
            if (lowHeading is null && lowLat is not null && lowLon is not null
                                   && highLat is not null && highLon is not null)
                lowHeading = InitialBearing(lowLat.Value, lowLon.Value, highLat.Value, highLon.Value);
            if (highHeading is null && lowLat is not null && lowLon is not null
                                    && highLat is not null && highLon is not null)
                highHeading = InitialBearing(highLat.Value, highLon.Value, lowLat.Value, lowLon.Value);

            AddEnd(
                ends,
                airport,
                airportIdent,
                row.Get("le_ident"),
                lowLat,
                lowLon,
                ParseFinite(row.Get("le_elevation_ft")) ?? airport?.ElevationFeet,
                lowHeading,
                ParseNonNegative(row.Get("le_displaced_threshold_ft")) ?? 0,
                length.Value,
                width,
                snapshot);
            AddEnd(
                ends,
                airport,
                airportIdent,
                row.Get("he_ident"),
                highLat,
                highLon,
                ParseFinite(row.Get("he_elevation_ft")) ?? airport?.ElevationFeet,
                highHeading,
                ParseNonNegative(row.Get("he_displaced_threshold_ft")) ?? 0,
                length.Value,
                width,
                snapshot);
        }

        return new OurAirportsRunwayCatalog(
            ends,
            snapshot,
            loadError: null,
            Path.GetDirectoryName(Path.GetFullPath(runwaysCsvPath)));
    }

    public bool TryGetRunwayEnd(
        string airportIdent,
        string runwayId,
        out OurAirportsRunwayEnd runwayEnd)
    {
        var airport = NormalizeAirport(airportIdent);
        var runway = NormalizeRunway(runwayId);
        return _ends.TryGetValue(Key(airport, runway), out runwayEnd!);
    }

    public static string NormalizeAirport(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

    public static string NormalizeRunway(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.Trim().ToUpperInvariant();
        if (normalized.StartsWith("RUNWAY", StringComparison.Ordinal))
            normalized = normalized[6..];
        else if (normalized.StartsWith("RWY", StringComparison.Ordinal))
            normalized = normalized[3..];
        normalized = normalized.Replace(" ", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal);

        var digits = new string(normalized.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length == 0 || !int.TryParse(digits, out var number))
            return normalized;
        var suffix = normalized[digits.Length..];
        return number.ToString("00", CultureInfo.InvariantCulture) + suffix;
    }

    private static OurAirportsRunwayCatalog LoadDefaultCore()
    {
        foreach (var directory in DefaultDirectories())
        {
            var airports = Path.Combine(directory, "airports.csv");
            var runways = Path.Combine(directory, "runways.csv");
            if (!File.Exists(airports) || !File.Exists(runways))
                continue;
            try
            {
                return Load(airports, runways);
            }
            catch (Exception ex)
            {
                return Empty($"Could not load OurAirports data from '{directory}': {ex.Message}", directory);
            }
        }

        return Empty("Bundled OurAirports airports.csv/runways.csv were not found.", null);
    }

    private static IEnumerable<string> DefaultDirectories()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "data", "ourairports");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "data", "ourairports"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "data", "ourairports"));
        yield return Path.Combine(Directory.GetCurrentDirectory(), "data", "ourairports");
    }

    private static OurAirportsRunwayCatalog Empty(string error, string? directory) =>
        new(new Dictionary<string, OurAirportsRunwayEnd>(StringComparer.OrdinalIgnoreCase),
            "unavailable", error, directory);

    private static void AddEnd(
        Dictionary<string, OurAirportsRunwayEnd> ends,
        AirportRow? airport,
        string airportIdent,
        string runwayId,
        double? latitude,
        double? longitude,
        double? elevationFeet,
        double? heading,
        double displacementFeet,
        double lengthFeet,
        double widthFeet,
        string snapshot)
    {
        var normalizedRunway = NormalizeRunway(runwayId);
        if (string.IsNullOrWhiteSpace(normalizedRunway)
            || latitude is null || longitude is null || heading is null)
            return;
        var lda = lengthFeet - displacementFeet;
        if (!double.IsFinite(lda) || lda <= 0)
            return;

        var threshold = Project(latitude.Value, longitude.Value, heading.Value,
            displacementFeet * 0.3048);
        var candidate = new OurAirportsRunwayEnd(
            airportIdent,
            airport?.CountryCode ?? "",
            normalizedRunway,
            latitude.Value,
            longitude.Value,
            threshold.Latitude,
            threshold.Longitude,
            elevationFeet ?? 0,
            NormalizeHeading(heading.Value),
            lengthFeet,
            widthFeet,
            displacementFeet,
            lda,
            snapshot);

        var aliases = airport?.Aliases ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { airportIdent };
        foreach (var alias in aliases.Append(airportIdent).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var key = Key(alias, normalizedRunway);
            if (!ends.TryGetValue(key, out var existing)
                || candidate.LandingDistanceAvailableFeet > existing.LandingDistanceAvailableFeet)
                ends[key] = candidate;
        }
    }

    private static (double Latitude, double Longitude) Project(
        double latitude,
        double longitude,
        double bearingDeg,
        double distanceMeters)
    {
        const double earthRadiusMeters = 6_371_000;
        if (distanceMeters <= 0) return (latitude, longitude);
        var angular = distanceMeters / earthRadiusMeters;
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

    private static double InitialBearing(double lat1, double lon1, double lat2, double lon2)
    {
        var phi1 = lat1 * Math.PI / 180.0;
        var phi2 = lat2 * Math.PI / 180.0;
        var deltaLon = (lon2 - lon1) * Math.PI / 180.0;
        var y = Math.Sin(deltaLon) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2)
                - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(deltaLon);
        return NormalizeHeading(Math.Atan2(y, x) * 180.0 / Math.PI);
    }

    private static IEnumerable<CsvRow> ReadTable(string path)
    {
        using var records = CsvRecords(path).GetEnumerator();
        if (!records.MoveNext())
            yield break;
        var headers = records.Current
            .Select((name, index) => (Name: name.Trim().TrimStart('\uFEFF'), Index: index))
            .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);
        while (records.MoveNext())
            yield return new CsvRow(headers, records.Current);
    }

    private static IEnumerable<string[]> CsvRecords(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var fields = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        while (reader.Read() is var next && next >= 0)
        {
            var c = (char)next;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        reader.Read();
                        field.Append('"');
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
                continue;
            }

            if (c == '"' && field.Length == 0)
            {
                quoted = true;
            }
            else if (c == ',')
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && reader.Peek() == '\n') reader.Read();
                fields.Add(field.ToString());
                field.Clear();
                yield return fields.ToArray();
                fields.Clear();
            }
            else
            {
                field.Append(c);
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            fields.Add(field.ToString());
            yield return fields.ToArray();
        }
    }

    private static string BuildSnapshotId(string airportsPath, string runwaysPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendFile(hash, airportsPath);
        AppendFile(hash, runwaysPath);
        return $"ourairports-sha256-{Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant()}";
    }

    private static void AppendFile(IncrementalHash hash, string path)
    {
        var buffer = new byte[64 * 1024];
        using var stream = File.OpenRead(path);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, read);
    }

    private static void AddAlias(HashSet<string> aliases, string value)
    {
        var normalized = NormalizeAirport(value);
        if (!string.IsNullOrWhiteSpace(normalized)) aliases.Add(normalized);
    }

    private static string NormalizeCountry(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : value.Trim().ToUpperInvariant();

    private static double? ParseFinite(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
        && double.IsFinite(parsed) ? parsed : null;

    private static double? ParseNonNegative(string value) => ParseFinite(value) is { } parsed && parsed >= 0
        ? parsed
        : null;

    private static double? ParseLatitude(string value) => ParseFinite(value) is { } parsed
                                                         && parsed is >= -90 and <= 90
        ? parsed
        : null;

    private static double? ParseLongitude(string value) => ParseFinite(value) is { } parsed
                                                          && parsed is >= -180 and <= 180
        ? parsed
        : null;

    private static double? ParseHeading(string value) => ParseFinite(value) is { } parsed
                                                        && parsed is >= 0 and <= 360
        ? NormalizeHeading(parsed)
        : null;

    private static double NormalizeHeading(double value)
    {
        value %= 360;
        return value < 0 ? value + 360 : value;
    }

    private static double NormalizeLongitude(double value)
    {
        while (value > 180) value -= 360;
        while (value < -180) value += 360;
        return value;
    }

    private static string Key(string airport, string runway) => $"{NormalizeAirport(airport)}:{NormalizeRunway(runway)}";

    private sealed record AirportRow(
        string Id,
        string Ident,
        string CountryCode,
        double? ElevationFeet,
        HashSet<string> Aliases);

    private readonly record struct CsvRow(
        IReadOnlyDictionary<string, int> Headers,
        IReadOnlyList<string> Fields)
    {
        public string Get(string name) =>
            Headers.TryGetValue(name, out var index) && index >= 0 && index < Fields.Count
                ? Fields[index]
                : "";
    }
}
