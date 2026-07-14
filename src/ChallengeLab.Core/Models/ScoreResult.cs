namespace ChallengeLab.Core.Models;

public sealed class ScoreResult
{
    public required string ChallengeId { get; init; }
    public required string ChallengeTitle { get; init; }
    public double ScorePercent { get; init; }
    public string Grade { get; init; } = "F";
    public IReadOnlyList<CriterionScore> Criteria { get; init; } = Array.Empty<CriterionScore>();
    public DateTimeOffset ScoredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? Summary { get; init; }

    /// <summary>Weighted average before safety gates (e.g. gear-up multiplier).</summary>
    public double ScoreBeforeGatesPercent { get; init; }

    /// <summary>True when gear was required and up — overall score was heavily reduced.</summary>
    public bool GearUpPenaltyApplied { get; init; }

    /// <summary>Per-phase scores (0–100) before phase weights are applied.</summary>
    public IReadOnlyList<PhaseScore> PhaseScores { get; init; } = Array.Empty<PhaseScore>();

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
    public double ScorePercent { get; init; }
    public bool Used { get; init; }
    public string? Note { get; init; }
}
