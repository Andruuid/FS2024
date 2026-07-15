using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ChallengeLab.Core.Config;

public sealed record EffectiveEvaluationProfile(
    LandingEvaluationKey Key,
    string ProfileHash)
{
    public string BucketId(string challengeId) =>
        $"{challengeId}|{Key.Id}|v{Key.Version}|{ProfileHash}";
}

public static class EffectiveEvaluationProfileBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static EffectiveEvaluationProfile Build(
        LandingEvaluationKey baseKey,
        ChallengeConfig? challenge = null)
    {
        ArgumentNullException.ThrowIfNull(baseKey);
        var json = JsonSerializer.Serialize(baseKey, JsonOptions);
        var key = JsonSerializer.Deserialize<LandingEvaluationKey>(json, JsonOptions)
                  ?? throw new InvalidOperationException("Could not clone evaluation key.");

        if (challenge?.ContactMapping is { } contact)
        {
            key.ContactMapping = new LandingContactMapping
            {
                LeftMainGearIndex = contact.LeftMainGearIndex,
                RightMainGearIndex = contact.RightMainGearIndex,
                NoseGearIndex = contact.NoseGearIndex,
                IsAircraftSpecific = true
            };
        }

        ApplyMetricOverrides(key, challenge?.ScoringOverrides);
        var errors = EvaluationKeyValidator.Validate(key);
        if (errors.Count > 0)
            throw new ArgumentException("Effective evaluation key is invalid: " + string.Join(" | ", errors));

        var canonical = JsonSerializer.Serialize(key, JsonOptions);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16]
            .ToLowerInvariant();
        return new EffectiveEvaluationProfile(key, hash);
    }

    private static void ApplyMetricOverrides(
        LandingEvaluationKey key,
        ChallengeScoringOverrides? overrides)
    {
        if (overrides is null) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var metrics = key.Phases.SelectMany(p => p.Metrics)
            .ToDictionary(m => m.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var item in overrides.Metrics)
        {
            if (string.IsNullOrWhiteSpace(item.Id))
                throw new ArgumentException("scoringOverrides.metrics.id is required.");
            if (!seen.Add(item.Id))
                throw new ArgumentException($"Duplicate scoring override for metric '{item.Id}'.");
            if (!metrics.TryGetValue(item.Id, out var metric))
                throw new ArgumentException($"Scoring override references unknown metric '{item.Id}'.");

            foreach (var pair in item.Params)
            {
                if (!metric.Params.ContainsKey(pair.Key))
                    throw new ArgumentException(
                        $"Scoring override for '{item.Id}' contains unknown parameter '{pair.Key}'.");
                metric.Params[pair.Key] = pair.Value;
            }

            foreach (var pair in item.Curves)
            {
                if (!metric.Curves.ContainsKey(pair.Key))
                    throw new ArgumentException(
                        $"Scoring override for '{item.Id}' contains unknown curve '{pair.Key}'.");
                metric.Curves[pair.Key] = pair.Value.Select(p => new ScorePoint { V = p.V, S = p.S }).ToList();
            }
        }
    }
}
