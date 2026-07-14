namespace ChallengeLab.Core.Models;

public sealed class ScoreResult
{
    public required string ChallengeId { get; init; }
    public required string ChallengeTitle { get; init; }
    public DifficultyLevel Level { get; init; }
    public double ScorePercent { get; init; }
    public string Grade { get; init; } = "F";
    public IReadOnlyList<CriterionScore> Criteria { get; init; } = Array.Empty<CriterionScore>();
    public DateTimeOffset ScoredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Summary { get; init; }

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
