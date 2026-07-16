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

            var key = LoadEvaluationKeyDocument(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
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

    private static LandingEvaluationKey LoadEvaluationKeyDocument(
        string path,
        HashSet<string> inheritanceChain)
    {
        path = Path.GetFullPath(path);
        if (!inheritanceChain.Add(path))
            throw new InvalidOperationException($"Evaluation-key inheritance cycle detected at {path}.");

        try
        {
            var json = File.ReadAllText(path);
            var corruption = FindCorruption(json);
            if (corruption is not null)
                throw new InvalidOperationException(
                    $"Evaluation key contains text-encoding corruption sequence '{corruption}'.");

            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var isOverlay = document.RootElement.EnumerateObject().Any(property =>
                property.Name.Equals("inherits", StringComparison.OrdinalIgnoreCase));
            if (!isOverlay)
                return JsonSerializer.Deserialize<LandingEvaluationKey>(json, StrictKeyJsonOptions)
                       ?? throw new InvalidOperationException($"Failed to deserialize {path}");

            var overlay = JsonSerializer.Deserialize<FreeFlightEvaluationOverlay>(json, StrictKeyJsonOptions)
                          ?? throw new InvalidOperationException($"Failed to deserialize overlay {path}");
            if (string.IsNullOrWhiteSpace(overlay.Inherits))
                throw new InvalidOperationException("Evaluation-key overlay must define inherits.");
            if (string.IsNullOrWhiteSpace(overlay.Id))
                throw new InvalidOperationException("Evaluation-key overlay must define id.");
            if (overlay.Version < 1)
                throw new InvalidOperationException("Evaluation-key overlay version must be at least 1.");
            if (overlay.FreeMode is null)
                throw new InvalidOperationException("A Free Flight evaluation-key overlay must define freeMode.");

            var basePath = Path.IsPathRooted(overlay.Inherits)
                ? overlay.Inherits
                : Path.Combine(Path.GetDirectoryName(path)!, overlay.Inherits);
            if (!File.Exists(basePath))
                throw new FileNotFoundException($"Inherited evaluation key not found: {basePath}", basePath);

            var key = LoadEvaluationKeyDocument(basePath, inheritanceChain);
            key.Id = overlay.Id.Trim();
            key.Version = overlay.Version;
            if (!string.IsNullOrWhiteSpace(overlay.Description))
                key.Description = overlay.Description.Trim();
            if (overlay.SpeedTarget?.DefaultVappKts is { } defaultVapp)
                key.SpeedTarget!.DefaultVappKts = defaultVapp;
            key.FreeMode = overlay.FreeMode;
            return key;
        }
        finally
        {
            inheritanceChain.Remove(path);
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

    private static string? FindCorruption(string text)
    {
        foreach (var sequence in new[] { "Ã", "Â", "â€", "â€“", "â€”", "â†", "�" })
            if (text.Contains(sequence, StringComparison.Ordinal)) return sequence;
        return null;
    }
}

/// <summary>
/// Deliberately narrow Free Flight overlay. Strict deserialization rejects phase,
/// metric, timing, gate, contact-mapping, or other structural overrides.
/// </summary>
internal sealed class FreeFlightEvaluationOverlay
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public string Description { get; set; } = "";
    public string Inherits { get; set; } = "";
    public FreeFlightSpeedTargetOverlay? SpeedTarget { get; set; }
    public FreeModeScoringPolicy? FreeMode { get; set; }
}

internal sealed class FreeFlightSpeedTargetOverlay
{
    public double? DefaultVappKts { get; set; }
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
