using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Career;

public sealed class CareerConfigValidationResult
{
    private CareerConfigValidationResult(CareerConfig? config, IReadOnlyList<string> errors)
    {
        Config = config;
        Errors = errors;
    }

    public CareerConfig? Config { get; }
    public IReadOnlyList<string> Errors { get; }
    public bool IsValid => Config is not null && Errors.Count == 0;

    public static CareerConfigValidationResult Validate(
        CareerConfig? config,
        IReadOnlyList<ChallengeConfig> challenges)
    {
        var errors = new List<string>();
        if (config is null)
            return new CareerConfigValidationResult(null, new[] { "catalog.json does not define career." });

        if (!double.IsFinite(config.PassScorePercent)
            || config.PassScorePercent <= 0
            || config.PassScorePercent > 100)
            errors.Add("career.passScorePercent must be finite and in the range (0, 100].");

        if (config.AssignmentChallengeIds.Count != 3)
            errors.Add("career.assignmentChallengeIds must contain exactly three challenge IDs.");
        if (config.Ranks.Count != 5)
            errors.Add("career.ranks must contain exactly five ordered ranks.");

        var byId = challenges
            .Where(c => !string.IsNullOrWhiteSpace(c.Id))
            .GroupBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        ValidateUniqueNonBlank(
            config.AssignmentChallengeIds,
            "career.assignmentChallengeIds",
            errors);

        foreach (var assignmentId in config.AssignmentChallengeIds.Where(id => !string.IsNullOrWhiteSpace(id)))
        {
            if (!byId.TryGetValue(assignmentId, out var challenge))
                errors.Add($"Career assignment challenge '{assignmentId}' does not exist.");
            else if (!challenge.Available)
                errors.Add($"Career assignment challenge '{assignmentId}' must be playable (available=true).");
        }

        ValidateUniqueNonBlank(config.Ranks.Select(r => r.Id), "career rank IDs", errors);
        ValidateUniqueNonBlank(config.Ranks.Select(r => r.Title), "career rank titles", errors);
        ValidateUniqueNonBlank(config.Ranks.Select(r => r.RewardChallengeId), "career reward challenge IDs", errors);

        var assignmentIds = new HashSet<string>(
            config.AssignmentChallengeIds.Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var rank in config.Ranks)
        {
            if (string.IsNullOrWhiteSpace(rank.Id))
                errors.Add("Every career rank must define a non-empty id.");
            if (string.IsNullOrWhiteSpace(rank.Title))
                errors.Add($"Career rank '{rank.Id}' must define a non-empty title.");
            if (string.IsNullOrWhiteSpace(rank.RewardChallengeId))
            {
                errors.Add($"Career rank '{rank.Id}' must define rewardChallengeId.");
                continue;
            }

            if (assignmentIds.Contains(rank.RewardChallengeId))
                errors.Add($"Career reward '{rank.RewardChallengeId}' cannot also be in the playable assignment pool.");

            if (!byId.TryGetValue(rank.RewardChallengeId, out var reward))
                errors.Add($"Career reward challenge '{rank.RewardChallengeId}' does not exist.");
            else if (reward.Available)
                errors.Add($"Career reward challenge '{rank.RewardChallengeId}' is a preview and must use available=false.");
        }

        return new CareerConfigValidationResult(config, errors);
    }

    private static void ValidateUniqueNonBlank(
        IEnumerable<string> values,
        string label,
        ICollection<string> errors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{label} cannot contain blank values.");
                continue;
            }

            if (!seen.Add(value.Trim()))
                errors.Add($"{label} contains duplicate value '{value}'.");
        }
    }
}
