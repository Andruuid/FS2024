namespace ChallengeLab.Core.Models;

public sealed class ScoreResult
{
    public required string ChallengeId { get; init; }
    public required string ChallengeTitle { get; init; }
    public double? ScorePercent { get; init; }
    public string Grade { get; init; } = "UNRANKED";
    public bool IsRanked { get; init; }
    public IReadOnlyList<string> IncompleteReasons { get; init; } = Array.Empty<string>();
    public IReadOnlyList<CriterionScore> Criteria { get; init; } = Array.Empty<CriterionScore>();
    public DateTimeOffset ScoredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Summary { get; init; }
    public double? ScoreBeforeGatesPercent { get; init; }
    public bool GearUpPenaltyApplied { get; init; }
    public IReadOnlyList<PhaseScore> PhaseScores { get; init; } = Array.Empty<PhaseScore>();

    /// <summary>
    /// True when this result is a live projection (missing metrics assumed 100%), not a final ranked score.
    /// </summary>
    public bool IsPreview { get; init; }

    public string ScoreDisplay => ScorePercent is null ? "UNRANKED" : $"{ScorePercent:0.0}%";

    public static string GradeFromPercent(double percent) => percent switch
    {
        >= 95 => "S",
        >= 85 => "A",
        >= 70 => "B",
        >= 55 => "C",
        >= 40 => "D",
        _ => "F"
    };
}

public sealed class PhaseScore
{
    public required string PhaseId { get; init; }
    public required string DisplayName { get; init; }
    public double WeightPercent { get; init; }
    public double? ScorePercent { get; init; }
    public bool IsComplete { get; init; }
    public string? Note { get; init; }

    public bool Used => IsComplete;
}
