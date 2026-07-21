using System.Globalization;
using System.Text;

namespace ChallengeLab.Core.Facilities;

/// <summary>One selectable ILS transmitter from the bundled frequency catalogue.</summary>
public sealed record IlsRunwayOption(
    string AirportIcao,
    string Runway,
    string Ident,
    decimal FrequencyMhz,
    double HeadingDegrees,
    int CourseDegrees,
    string AmbiguityWarning)
{
    public string DisplayName =>
        $"RW{Runway} · {Ident} · {FrequencyMhz:0.00} MHz · {CourseDegrees:000}°";

    public bool HasAmbiguity => !string.IsNullOrWhiteSpace(AmbiguityWarning);
}

/// <summary>Airport search result with every distinct ILS runway/transmitter.</summary>
public sealed record IlsAirportOption(
    string Icao,
    string Name,
    string Municipality,
    string CountryCode,
    string IataCode,
    IReadOnlyList<IlsRunwayOption> Runways)
{
    public string DisplayName
    {
        get
        {
            var location = string.Join(", ", new[] { Municipality, CountryCode }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            var detail = string.Join(" · ", new[] { Name, location }
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            return string.IsNullOrWhiteSpace(detail) ? Icao : $"{Icao} · {detail}";
        }
    }
}

/// <summary>
/// Read-only airport/name index over data/ils_frequencies.csv. The frequency file is the
/// tuning source of truth; bundled OurAirports metadata supplies friendly search labels.
/// </summary>
public sealed class IlsFrequencyCatalog
{
    private static readonly Lazy<IlsFrequencyCatalog> DefaultCatalog = new(LoadDefaultCore);
    private readonly IReadOnlyList<AirportSearchEntry> _airports;

    private IlsFrequencyCatalog(IReadOnlyList<AirportSearchEntry> airports, string? loadError)
    {
        _airports = airports;
        LoadError = loadError;
    }

    public static IlsFrequencyCatalog Default => DefaultCatalog.Value;
    public string? LoadError { get; }
    public bool IsAvailable => _airports.Count > 0;
    public int AirportCount => _airports.Count;
    public int TransmitterCount => _airports.Sum(airport => airport.Option.Runways.Count);

    public static IlsFrequencyCatalog Load(string ilsCsvPath, string? airportsCsvPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ilsCsvPath);
        if (!File.Exists(ilsCsvPath))
            throw new FileNotFoundException("ILS frequency catalogue was not found.", ilsCsvPath);
        if (!string.IsNullOrWhiteSpace(airportsCsvPath) && !File.Exists(airportsCsvPath))
            throw new FileNotFoundException("OurAirports airports.csv was not found.", airportsCsvPath);

        var metadata = LoadAirportMetadata(airportsCsvPath);
        var rowsByAirport = new Dictionary<string, List<ParsedIlsRow>>(StringComparer.OrdinalIgnoreCase);
        var exactRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in OurAirportsRunwayCatalog.ReadTable(ilsCsvPath))
        {
            var airport = OurAirportsRunwayCatalog.NormalizeAirport(row.Get("icao"));
            var runway = NormalizeIlsRunway(row.Get("runway"));
            var ident = row.Get("ident").Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(airport) || string.IsNullOrWhiteSpace(runway))
                continue;
            if (!decimal.TryParse(row.Get("frequency_mhz"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var frequency)
                || frequency is < 108.10m or > 111.95m)
                continue;
            if (!double.TryParse(row.Get("heading_deg"), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var heading)
                || !double.IsFinite(heading))
                continue;

            heading = NormalizeHeading(heading);
            var course = RoundCourse(heading);
            var exactKey = string.Join("\u001f", airport, runway, ident,
                frequency.ToString("0.00##", CultureInfo.InvariantCulture),
                heading.ToString("R", CultureInfo.InvariantCulture));
            if (!exactRows.Add(exactKey))
                continue;

            if (!rowsByAirport.TryGetValue(airport, out var airportRows))
            {
                airportRows = new List<ParsedIlsRow>();
                rowsByAirport[airport] = airportRows;
            }
            airportRows.Add(new ParsedIlsRow(airport, runway, ident, frequency, heading, course));
        }

        var airports = new List<AirportSearchEntry>(rowsByAirport.Count);
        foreach (var (icao, parsedRows) in rowsByAirport)
        {
            metadata.TryGetValue(icao, out var airportMetadata);
            var ambiguityByFrequency = parsedRows
                .GroupBy(row => row.FrequencyMhz)
                .Where(group => group.Select(row => row.CourseDegrees).Distinct().Count() > 1)
                .ToDictionary(
                    group => group.Key,
                    group => "Same frequency has different courses at this airport: "
                             + string.Join(", ", group
                                 .OrderBy(row => row.Runway, StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(row => row.CourseDegrees)
                                 .Select(row => $"RW{row.Runway} {row.CourseDegrees:000}°")
                                 .Distinct(StringComparer.OrdinalIgnoreCase))
                             + ".");

            var runways = parsedRows
                .OrderBy(row => row.Runway, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.Ident, StringComparer.OrdinalIgnoreCase)
                .ThenBy(row => row.FrequencyMhz)
                .ThenBy(row => row.CourseDegrees)
                .Select(row => new IlsRunwayOption(
                    row.AirportIcao,
                    row.Runway,
                    row.Ident,
                    row.FrequencyMhz,
                    row.HeadingDegrees,
                    row.CourseDegrees,
                    ambiguityByFrequency.GetValueOrDefault(row.FrequencyMhz, "")))
                .ToArray();

            var option = new IlsAirportOption(
                icao,
                airportMetadata?.Name ?? "",
                airportMetadata?.Municipality ?? "",
                airportMetadata?.CountryCode ?? "",
                airportMetadata?.IataCode ?? "",
                runways);
            airports.Add(new AirportSearchEntry(option, FoldForSearch(string.Join(" ",
                option.Icao,
                option.Name,
                option.Municipality,
                option.CountryCode,
                option.IataCode))));
        }

        airports.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.Option.Icao, right.Option.Icao));
        return new IlsFrequencyCatalog(airports, loadError: null);
    }

    /// <summary>Accent-insensitive airport lookup. Queries shorter than two characters return no rows.</summary>
    public IReadOnlyList<IlsAirportOption> Search(string? query, int maxResults = 20)
    {
        if (maxResults <= 0) return Array.Empty<IlsAirportOption>();
        var folded = FoldForSearch(query ?? "").Trim();
        if (folded.Length < 2) return Array.Empty<IlsAirportOption>();

        return _airports
            .Where(airport => airport.SearchText.Contains(folded, StringComparison.Ordinal))
            .OrderByDescending(airport =>
                string.Equals(FoldForSearch(airport.Option.Icao), folded, StringComparison.Ordinal))
            .ThenByDescending(airport =>
                string.Equals(FoldForSearch(airport.Option.IataCode), folded, StringComparison.Ordinal))
            .ThenByDescending(airport =>
                FoldForSearch(airport.Option.Icao).StartsWith(folded, StringComparison.Ordinal))
            .ThenByDescending(airport =>
                FoldForSearch(airport.Option.Name).StartsWith(folded, StringComparison.Ordinal))
            .ThenBy(airport => airport.Option.Icao, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(airport => airport.Option)
            .ToArray();
    }

    public IlsAirportOption? FindExact(string? icao) =>
        _airports.FirstOrDefault(airport =>
            string.Equals(airport.Option.Icao, icao?.Trim(), StringComparison.OrdinalIgnoreCase))?.Option;

    public static int RoundCourse(double headingDegrees)
    {
        var normalized = NormalizeHeading(headingDegrees);
        var rounded = (int)Math.Round(normalized, MidpointRounding.AwayFromZero) % 360;
        return rounded == 0 ? 360 : rounded;
    }

    private static Dictionary<string, AirportMetadata> LoadAirportMetadata(string? path)
    {
        var result = new Dictionary<string, AirportMetadata>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(path)) return result;

        foreach (var row in OurAirportsRunwayCatalog.ReadTable(path))
        {
            if (string.Equals(row.Get("type"), "closed", StringComparison.OrdinalIgnoreCase))
                continue;
            var metadata = new AirportMetadata(
                row.Get("name").Trim(),
                row.Get("municipality").Trim(),
                row.Get("iso_country").Trim().ToUpperInvariant(),
                row.Get("iata_code").Trim().ToUpperInvariant());
            foreach (var alias in new[]
                     {
                         row.Get("ident"), row.Get("icao_code"), row.Get("gps_code")
                     })
            {
                var normalized = OurAirportsRunwayCatalog.NormalizeAirport(alias);
                if (!string.IsNullOrWhiteSpace(normalized))
                    result.TryAdd(normalized, metadata);
            }
        }
        return result;
    }

    private static IlsFrequencyCatalog LoadDefaultCore()
    {
        foreach (var dataDirectory in DefaultDataDirectories())
        {
            var ilsPath = Path.Combine(dataDirectory, "ils_frequencies.csv");
            if (!File.Exists(ilsPath)) continue;
            var airportsPath = Path.Combine(dataDirectory, "ourairports", "airports.csv");
            try
            {
                return Load(ilsPath, File.Exists(airportsPath) ? airportsPath : null);
            }
            catch (Exception ex)
            {
                return new IlsFrequencyCatalog(Array.Empty<AirportSearchEntry>(),
                    $"Could not load ILS catalogue from '{ilsPath}': {ex.Message}");
            }
        }

        return new IlsFrequencyCatalog(Array.Empty<AirportSearchEntry>(),
            "Bundled data/ils_frequencies.csv was not found.");
    }

    private static IEnumerable<string> DefaultDataDirectories()
    {
        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "data");
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "data"));
        yield return Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "data"));
        yield return Path.Combine(Directory.GetCurrentDirectory(), "data");
    }

    private static string NormalizeIlsRunway(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var normalized = value.Trim().ToUpperInvariant();
        var runwayMarker = normalized.LastIndexOf("RW", StringComparison.Ordinal);
        if (runwayMarker >= 0)
            normalized = normalized[(runwayMarker + 2)..];
        return OurAirportsRunwayCatalog.NormalizeRunway(normalized);
    }

    private static double NormalizeHeading(double heading)
    {
        var normalized = heading % 360.0;
        if (normalized < 0) normalized += 360.0;
        return normalized;
    }

    private static string FoldForSearch(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                folded.Append(char.ToUpperInvariant(character));
        }
        return folded.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed record AirportSearchEntry(IlsAirportOption Option, string SearchText);
    private sealed record AirportMetadata(string Name, string Municipality, string CountryCode, string IataCode);
    private sealed record ParsedIlsRow(
        string AirportIcao,
        string Runway,
        string Ident,
        decimal FrequencyMhz,
        double HeadingDegrees,
        int CourseDegrees);
}
