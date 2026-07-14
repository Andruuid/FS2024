using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChallengeLab.Core.Config;

public sealed class ConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string RootPath { get; }

    public ConfigLoader(string? rootPath = null)
    {
        RootPath = rootPath ?? FindDefaultRoot();
    }

    public static string FindDefaultRoot()
    {
        // Prefer output-dir config (copied on build), then walk up for repo config/
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "config"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "config")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "config")),
            Path.Combine(Directory.GetCurrentDirectory(), "config")
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "catalog.json")))
                return c;
        }

        return Path.Combine(baseDir, "config");
    }

    public CatalogConfig LoadCatalog()
    {
        var path = Path.Combine(RootPath, "catalog.json");
        return LoadJson<CatalogConfig>(path);
    }

    public ChallengeConfig LoadChallenge(string relativeOrAbsolutePath)
    {
        var path = ResolvePath(relativeOrAbsolutePath);
        return LoadJson<ChallengeConfig>(path);
    }

    public ScoringProfileConfig LoadScoringProfile(string relativeOrAbsolutePath)
    {
        var path = ResolvePath(relativeOrAbsolutePath);
        // profiles may be under config/scoring/profiles relative to challenge field
        if (!File.Exists(path))
        {
            var alt = Path.Combine(RootPath, relativeOrAbsolutePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(alt)) path = alt;
        }

        return LoadJson<ScoringProfileConfig>(path);
    }

    public IReadOnlyList<ChallengeConfig> LoadAllChallenges(CatalogConfig? catalog = null)
    {
        catalog ??= LoadCatalog();
        var list = new List<ChallengeConfig>();
        foreach (var file in catalog.ChallengeFiles)
        {
            try
            {
                list.Add(LoadChallenge(file));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load challenge {file}: {ex.Message}");
            }
        }

        return list;
    }

    public string ResolvePath(string relativeOrAbsolute)
    {
        if (Path.IsPathRooted(relativeOrAbsolute) && File.Exists(relativeOrAbsolute))
            return relativeOrAbsolute;

        var underConfig = Path.Combine(RootPath, relativeOrAbsolute.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(underConfig)) return underConfig;

        // challenges often referenced as challenges/foo.json
        var challenges = Path.Combine(RootPath, "challenges", Path.GetFileName(relativeOrAbsolute));
        if (File.Exists(challenges)) return challenges;

        var scoring = Path.Combine(RootPath, "scoring", "profiles", Path.GetFileName(relativeOrAbsolute));
        if (File.Exists(scoring)) return scoring;

        return underConfig;
    }

    public string ResolveFlightPath(string flightFileRelative)
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, flightFileRelative.Replace('/', Path.DirectorySeparatorChar)),
            Path.Combine(baseDir, "flights", Path.GetFileName(flightFileRelative)),
            Path.GetFullPath(Path.Combine(RootPath, "..", flightFileRelative.Replace('/', Path.DirectorySeparatorChar))),
            Path.GetFullPath(Path.Combine(RootPath, "..", "flights", Path.GetFileName(flightFileRelative)))
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        return candidates[0];
    }

    private static T LoadJson<T>(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}", path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize {path}");
    }
}
