using System.Text.Json.Serialization;

namespace ChallengeLab.Core.Career;

public enum LandingAttemptOrigin
{
    DefaultChallenge,
    CareerAssignment,
    FreeFlight
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CareerOutcomeKind
{
    Retry,
    Unranked,
    Passed,
    Complete
}

public sealed class CareerOutcome
{
    public CareerOutcomeKind Kind { get; set; }
    public string ChallengeId { get; set; } = "";
    public string RankId { get; set; } = "";
    public string RankTitle { get; set; } = "";
    public double? ScorePercent { get; set; }
    public bool IsRanked { get; set; }
    public DateTimeOffset AttemptedAtUtc { get; set; }
    public string Message { get; set; } = "";

    [JsonIgnore]
    public bool Passed => Kind is CareerOutcomeKind.Passed or CareerOutcomeKind.Complete;
}

public sealed class CareerProgressState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public int CompletedStageCount { get; set; }
    public string? AcceptedAssignmentId { get; set; }
    public DateTimeOffset? AcceptedAtUtc { get; set; }
    public string? PreviousAssignmentId { get; set; }
    public int AttemptCount { get; set; }
    public CareerOutcome? LastResult { get; set; }
}

public interface IRandomIndexProvider
{
    int Next(int exclusiveUpperBound);
}

public sealed class SystemRandomIndexProvider : IRandomIndexProvider
{
    public int Next(int exclusiveUpperBound) => Random.Shared.Next(exclusiveUpperBound);
}
