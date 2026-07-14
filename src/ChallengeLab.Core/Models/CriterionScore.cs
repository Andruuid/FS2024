namespace ChallengeLab.Core.Models;

public sealed class CriterionScore
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public double Weight { get; init; }
    public double Score01 { get; init; }
    public double? RawValue { get; init; }
    public string? Unit { get; init; }
    public string? Note { get; init; }
    public bool Applied { get; init; } = true;

    /// <summary>Phase this metric belongs to (e.g. touchdown, approach, rollout).</summary>
    public string? PhaseId { get; init; }

    /// <summary>Phase display name for hierarchical results (Touchdown, Approach, …).</summary>
    public string? PhaseDisplayName { get; init; }

    /// <summary>Share of the phase (importancePercent from evaluation key).</summary>
    public double ImportancePercent { get; init; }

    public double ScorePercent => Score01 * 100.0;
}
