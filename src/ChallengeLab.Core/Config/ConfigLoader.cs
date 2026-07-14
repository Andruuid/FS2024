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

    private static readonly JsonSerializerOptions StrictKeyJsonOptions = new(JsonOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public string RootPath { get; }

    public ConfigLoader(string? rootPath = null)
    {
        RootPath = rootPath ?? FindDefaultRoot();
    }

    public static string FindDefaultRoot()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "config"),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "..", "config")),
            Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "config")),
            Path.Combine(Directory.GetCurrentDirectory(), "config")
        };

        foreach (var candidate in candidates)
        {
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "catalog.json")))
                return candidate;
        }

        return Path.Combine(baseDir, "config");
    }

    public CatalogConfig LoadCatalog() =>
        LoadJson<CatalogConfig>(Path.Combine(RootPath, "catalog.json"));

    public ChallengeConfig LoadChallenge(string relativeOrAbsolutePath) =>
        LoadJson<ChallengeConfig>(ResolvePath(relativeOrAbsolutePath));

    /// <summary>
    /// Loads and validates the single evaluation key. The catalog path is authoritative;
    /// failure never selects a fallback scoring path.
    /// </summary>
    public EvaluationKeyLoadResult LoadEvaluationKey(string? relativeOrAbsolutePath = null)
    {
        string? path = null;
        try
        {
            var relative = relativeOrAbsolutePath;
            if (string.IsNullOrWhiteSpace(relative))
            {
                var catalog = LoadCatalog();
                if (string.IsNullOrWhiteSpace(catalog.EvaluationKey))
                    return EvaluationKeyLoadResult.Failure(null, "catalog.json must define evaluationKey.");
                relative = catalog.EvaluationKey;
            }

            path = Path.IsPathRooted(relative)
                ? relative
                : Path.Combine(RootPath, relative.Replace('/', Path.DirectorySeparatorChar));
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
                return EvaluationKeyLoadResult.Failure(path, $"Evaluation key not found: {path}");

            var key = LoadJson<LandingEvaluationKey>(path, StrictKeyJsonOptions);
            var errors = EvaluationKeyValidator.Validate(key);
            return errors.Count == 0
                ? EvaluationKeyLoadResult.Success(key, path)
                : EvaluationKeyLoadResult.Failure(path, errors);
        }
        catch (Exception ex)
        {
            return EvaluationKeyLoadResult.Failure(path, ex.Message);
        }
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

        var challenges = Path.Combine(RootPath, "challenges", Path.GetFileName(relativeOrAbsolute));
        if (File.Exists(challenges)) return challenges;

        return underConfig;
    }

    public string ResolveFlightPath(string flightFileRelative)
    {
        var baseDir = AppContext.BaseDirectory;
        var relative = flightFileRelative.Replace('/', Path.DirectorySeparatorChar);
        var candidates = new[]
        {
            Path.Combine(baseDir, relative),
            Path.Combine(baseDir, "flights", Path.GetFileName(relative)),
            Path.Combine(baseDir, "AutoSaveReal", Path.GetFileName(relative)),
            Path.GetFullPath(Path.Combine(RootPath, "..", relative)),
            Path.GetFullPath(Path.Combine(RootPath, "..", "flights", Path.GetFileName(relative))),
            Path.GetFullPath(Path.Combine(RootPath, "..", "AutoSaveReal", Path.GetFileName(relative))),
            Path.IsPathRooted(flightFileRelative) ? flightFileRelative : string.Empty
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrEmpty(candidate) && File.Exists(candidate))
                return Path.GetFullPath(candidate);
        }

        return Path.GetFullPath(candidates[0]);
    }

    private static T LoadJson<T>(string path, JsonSerializerOptions? options = null)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Config file not found: {path}", path);

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, options ?? JsonOptions)
               ?? throw new InvalidOperationException($"Failed to deserialize {path}");
    }
}

public sealed class EvaluationKeyLoadResult
{
    private EvaluationKeyLoadResult(LandingEvaluationKey? key, string? path, IReadOnlyList<string> errors)
    {
        Key = key;
        Path = path;
        Errors = errors;
    }

    public LandingEvaluationKey? Key { get; }
    public string? Path { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Key is not null && Errors.Count == 0;

    public static EvaluationKeyLoadResult Success(LandingEvaluationKey key, string path) =>
        new(key, path, Array.Empty<string>());

    public static EvaluationKeyLoadResult Failure(string? path, params string[] errors) =>
        new(null, path, errors);

    public static EvaluationKeyLoadResult Failure(string? path, IReadOnlyList<string> errors) =>
        new(null, path, errors);
}
