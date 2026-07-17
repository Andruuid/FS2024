namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Hardcoded steep / non-standard approach path angles for free-flight scoring.
/// Looked up before facility VASI/PAPI and the 3° default.
/// Matching is <b>ICAO + runway end</b> only — unlisted ends fall through to VASI/default.
/// </summary>
public static class SteepApproachGlideslopeCatalog
{
    public const string SourceCatalog = "catalog";

    /// <summary>One curated steep approach: airport + runway end → path angle.</summary>
    public readonly record struct Entry(
        string Icao,
        string RunwayId,
        double Degrees,
        string AirportName,
        string Country);

    /// <summary>
    /// Famous steep / non-standard approaches (runway-specific).
    /// Multi-end rows (e.g. 09/27) are expanded to one entry per end.
    /// </summary>
    private static readonly Entry[] Entries =
    [
        new("KSBS", "32", 7.75, "Steamboat Springs Airport", "USA"),
        new("KSEE", "27R", 6.88, "Gillespie Field", "USA"),
        new("LSZA", "01", 6.65, "Lugano Airport", "Switzerland"),
        new("KASE", "15", 6.59, "Aspen-Pitkin County Airport", "USA"),
        new("LSGS", "25", 6.00, "Sion Airport", "Switzerland"),
        new("EGLC", "09", 5.50, "London City Airport", "United Kingdom"),
        new("EGLC", "27", 5.50, "London City Airport", "United Kingdom"),
        new("VNLK", "06", 5.50, "Tenzing-Hillary Airport", "Nepal"),
        new("KJAC", "19", 5.50, "Jackson Hole Airport", "USA"),
        new("VNKT", "02", 5.30, "Tribhuvan International Airport", "Nepal"),
        new("BIAR", "01", 5.30, "Akureyri Airport", "Iceland"),
        new("LXGB", "27", 5.00, "Gibraltar International Airport", "Gibraltar"),
        new("VILH", "07", 5.00, "Leh Kushok Bakula Rimpochee Airport", "India"),
        new("SKSP", "02", 4.90, "Gustavo Artunduaga Paredes Airport", "Colombia"),
        new("CYTZ", "08", 4.80, "Billy Bishop Toronto City Airport", "Canada"),
        new("CYTZ", "26", 4.80, "Billy Bishop Toronto City Airport", "Canada"),
        new("LEXJ", "29", 4.75, "Santander Airport", "Spain"),
        new("NZCH", "02", 4.70, "Christchurch International Airport", "New Zealand"),
        new("TEXC", "09", 4.50, "Telluride Regional Airport", "USA"),
        new("KTEX", "09", 4.50, "Telluride Regional Airport", "USA"), // real ICAO alias
        new("SPHZ", "28", 4.50, "Alejandro Velasco Astete Intl", "Peru"),
        new("CYYF", "16", 4.50, "Penticton Regional Airport", "Canada"),
        new("SIAL", "04R", 4.50, "Sialkot International Airport", "Pakistan"),
        new("VOML", "24", 4.50, "Mangaluru International Airport", "India"),
        new("PAJN", "08", 4.50, "Juneau International Airport", "USA"),
        new("KMMH", "25", 4.50, "Mammoth Yosemite Airport", "USA"),
        new("OPKC", "25L", 4.50, "Jinnah International Airport", "Pakistan"),
        new("LFLB", "14", 4.46, "Chambery-Savoie Airport", "France"),
        new("LOKW", "17", 4.40, "Wolfsberg Airfield", "Austria"),
        new("VNDP", "15", 4.20, "Dipayal Airport", "Nepal"),
        new("LOWI", "08", 4.00, "Innsbruck Airport", "Austria"),
        new("LPMA", "05", 4.00, "Madeira Airport", "Portugal"),
        new("VMMC", "34", 4.00, "Macau International Airport", "Macau"),
        new("LFML", "31R", 4.00, "Marseille Provence Airport", "France"),
        new("LXGB", "09", 4.00, "Gibraltar International Airport", "Gibraltar"),
        new("KTVL", "18", 4.00, "Lake Tahoe Airport", "USA"),
        new("SLLP", "10", 4.00, "El Alto International Airport", "Bolivia"),
        new("VOMM", "07", 4.00, "Chennai International Airport", "India"),
        new("OAKB", "29", 4.00, "Kabul International Airport", "Afghanistan"),
        new("DAAG", "05", 4.00, "Houari Boumediene Airport", "Algeria"),
        new("GMMH", "14", 4.00, "Dakhla Airport", "Morocco"),
        new("BIHN", "18", 4.00, "Höfn Hornafjörður Airport", "Iceland"),
        new("LIRE", "14", 4.00, "Pratica di Mare Air Base", "Italy"),
        new("PANC", "07R", 4.00, "Ted Stevens Anchorage Intl", "USA"),
        new("LILC", "21", 3.95, "Cuneo Levaldigi Airport", "Italy"),
        new("KHDN", "10", 3.80, "Yampa Valley Airport", "USA"),
        new("KEGE", "25", 3.75, "Eagle County Regional Airport", "USA"),
        new("KPVU", "13", 3.75, "Provo Municipal Airport", "USA"), // list had "Ywy 13" typo
        new("LIPB", "01", 3.55, "Bolzano Airport", "Italy"),
        new("KMTN", "15", 3.51, "Martin State Airport", "USA"),
        new("EDDF", "07R", 3.51, "Frankfurt Airport", "Germany"),
        new("EDDF", "25L", 3.51, "Frankfurt Airport", "Germany"),
        new("KPSO", "01", 3.51, "Stevens Field", "USA"),
        new("LXGB", "01", 3.50, "Gibraltar International Airport", "Gibraltar"),
        new("KBUR", "08", 3.50, "Hollywood Burbank Airport", "USA"),
        new("VQLN", "16", 3.50, "Lhuntse Heliport / Approach", "Bhutan"),
        new("EDDI", "27R", 3.50, "Berlin Tempelhof", "Germany"),
        new("LEAM", "25", 3.50, "Almería Airport", "Spain"),
    ];

    private static readonly Dictionary<string, double> ByIcaoAndRunway;

    static SteepApproachGlideslopeCatalog()
    {
        ByIcaoAndRunway = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in Entries)
        {
            var icao = NormalizeIcao(entry.Icao);
            var rwy = NormalizeRunwayId(entry.RunwayId);
            if (icao.Length == 0 || rwy.Length == 0)
                continue;

            ByIcaoAndRunway[RunwayKey(icao, rwy)] =
                RunwayPathGeometry.SanitizeGlideslopeDeg(entry.Degrees);
        }
    }

    /// <summary>
    /// Resolve a catalog angle only when both ICAO and runway end match.
    /// </summary>
    public static bool TryResolve(
        string? airportIcao,
        string? runwayId,
        out GlideslopeAngleResolver.Resolution resolution)
    {
        resolution = default;
        var icao = NormalizeIcao(airportIcao);
        var rwy = NormalizeRunwayId(runwayId);
        if (icao.Length == 0 || rwy.Length == 0)
            return false;

        if (!ByIcaoAndRunway.TryGetValue(RunwayKey(icao, rwy), out var deg))
            return false;

        resolution = new GlideslopeAngleResolver.Resolution(
            Math.Round(deg, 2),
            SourceCatalog);
        return true;
    }

    /// <summary>All curated entries (for diagnostics / tests).</summary>
    public static IReadOnlyList<Entry> AllEntries => Entries;

    public static bool ContainsAirport(string? airportIcao)
    {
        var icao = NormalizeIcao(airportIcao);
        if (icao.Length == 0)
            return false;

        var prefix = icao + ":";
        foreach (var key in ByIcaoAndRunway.Keys)
        {
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string RunwayKey(string icao, string runwayId) => icao + ":" + runwayId;

    private static string NormalizeIcao(string? icao)
    {
        if (string.IsNullOrWhiteSpace(icao))
            return "";
        return icao.Trim().ToUpperInvariant();
    }

    /// <summary>
    /// Normalize runway ids so "29", "RWY 29", "rwy29", "07R" match facility formatting.
    /// </summary>
    public static string NormalizeRunwayId(string? runwayId)
    {
        if (string.IsNullOrWhiteSpace(runwayId))
            return "";

        var chars = runwayId.Trim().ToUpperInvariant()
            .Where(c => char.IsLetterOrDigit(c))
            .ToArray();
        if (chars.Length == 0)
            return "";

        var s = new string(chars);
        // Strip leading "RWY" / "RW"
        if (s.StartsWith("RWY", StringComparison.Ordinal))
            s = s[3..];
        else if (s.StartsWith("RW", StringComparison.Ordinal) && s.Length > 2 && char.IsDigit(s[2]))
            s = s[2..];
        // Tolerate the "Ywy" typo from source lists
        else if (s.StartsWith("YWY", StringComparison.Ordinal))
            s = s[3..];

        // Pad single-digit numbers: "9" -> "09", "9L" -> "09L"
        if (s.Length >= 1 && char.IsDigit(s[0]) && (s.Length == 1 || !char.IsDigit(s[1])))
            s = "0" + s;

        return s;
    }
}
