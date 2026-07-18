using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChallengeLab.Core.Config;

/// <summary>One family / type row from the fixed aircraft VAPP database.</summary>
public sealed record AircraftVappEntry(
    string Id,
    string Label,
    double VappKts,
    IReadOnlyList<string> MatchTokens);

/// <summary>Result of matching a live SimConnect TITLE against the VAPP database.</summary>
public sealed record AircraftVappMatch(
    AircraftVappEntry Entry,
    string MatchedToken,
    string AircraftTitle);

/// <summary>
/// Fixed TITLE→VAPP table used when Free Flight (and fallback paths) know the live aircraft
/// string but do not have a challenge-authored approach speed.
/// </summary>
public sealed class AircraftVappCatalog
{
    private static readonly Lazy<AircraftVappCatalog> DefaultCatalog = new(LoadDefaultCore);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IReadOnlyList<AircraftVappEntry> _entries;

    private AircraftVappCatalog(
        IReadOnlyList<AircraftVappEntry> entries,
        string? path,
        string? loadError)
    {
        _entries = entries;
        Path = path;
        LoadError = loadError;
    }

    public static AircraftVappCatalog Default => DefaultCatalog.Value;
    public static AircraftVappCatalog Empty { get; } = new([], null, null);

    public string? Path { get; }
    public string? LoadError { get; }
    public int EntryCount => _entries.Count;
    public bool IsAvailable => _entries.Count > 0;
    public IReadOnlyList<AircraftVappEntry> Entries => _entries;

    public static AircraftVappCatalog Load(string jsonPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jsonPath);
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("Aircraft VAPP database was not found.", jsonPath);

        var json = File.ReadAllText(jsonPath);
        var doc = JsonSerializer.Deserialize<AircraftVappDbDocument>(json, JsonOptions)
                  ?? throw new InvalidOperationException($"Failed to deserialize {jsonPath}");

        var entries = new List<AircraftVappEntry>();
        foreach (var raw in doc.Entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw.Id))
                continue;
            if (!double.IsFinite(raw.VappKts) || raw.VappKts is <= 50 or >= 250)
                continue;

            var tokens = (raw.Match ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => t.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (tokens.Count == 0)
                continue;

            entries.Add(new AircraftVappEntry(
                raw.Id.Trim(),
                string.IsNullOrWhiteSpace(raw.Label) ? raw.Id.Trim() : raw.Label.Trim(),
                raw.VappKts,
                tokens));
        }

        return new AircraftVappCatalog(entries, System.IO.Path.GetFullPath(jsonPath), null);
    }

    /// <summary>
    /// First entry whose match token is a case-insensitive substring of <paramref name="aircraftTitle"/>.
    /// The live title must contain the configured token; a short/generic title must not match a
    /// longer, more-specific catalog token.
    /// </summary>
    public AircraftVappMatch? TryMatch(string? aircraftTitle)
    {
        if (string.IsNullOrWhiteSpace(aircraftTitle) || _entries.Count == 0)
            return null;

        var title = aircraftTitle.Trim();
        foreach (var entry in _entries)
        {
            foreach (var token in entry.MatchTokens)
            {
                if (title.Contains(token, StringComparison.OrdinalIgnoreCase))
                {
                    return new AircraftVappMatch(entry, token, title);
                }
            }
        }

        return null;
    }

    private static AircraftVappCatalog LoadDefaultCore()
    {
        try
        {
            var path = ResolveDefaultPath();
            if (path is null)
                return new AircraftVappCatalog([], null, "aircraft-vapp-db.json not found under config/.");
            return Load(path);
        }
        catch (Exception ex)
        {
            return new AircraftVappCatalog([], null, ex.Message);
        }
    }

    private static string? ResolveDefaultPath()
    {
        var relative = "scoring/aircraft-vapp-db.json";
        foreach (var root in CandidateConfigRoots())
        {
            var candidate = System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
                return System.IO.Path.GetFullPath(candidate);
        }

        return null;
    }

    private static IEnumerable<string> CandidateConfigRoots()
    {
        var roots = new List<string>
        {
            System.IO.Path.Combine(AppContext.BaseDirectory, "config"),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config")),
            System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "config")),
            System.IO.Path.Combine(Directory.GetCurrentDirectory(), "config")
        };
        try
        {
            roots.Add(ConfigLoader.FindDefaultRoot());
        }
        catch
        {
            // Ignore — other candidates already cover common layouts.
        }

        return roots;
    }

    private sealed class AircraftVappDbDocument
    {
        public int Version { get; set; }
        public string? Description { get; set; }
        public List<AircraftVappDbEntry>? Entries { get; set; }
    }

    private sealed class AircraftVappDbEntry
    {
        public string Id { get; set; } = "";
        public string? Label { get; set; }
        public double VappKts { get; set; }
        public List<string>? Match { get; set; }
    }
}
