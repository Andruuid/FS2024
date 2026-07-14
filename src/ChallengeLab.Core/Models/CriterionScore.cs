namespace ChallengeLab.Core.Models;

public enum MetricStatus
{
    Scored,
    Informational,
    Unavailable,
    GateFailed
}

public sealed class CriterionScore
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public double? Score01 { get; init; }
    public double? RawValue { get; init; }
    public string? Unit { get; init; }
    public string? Note { get; init; }
    public MetricStatus Status { get; init; } = MetricStatus.Scored;
    public string? UnavailableReason { get; init; }

    public string? PhaseId { get; init; }
    public string? PhaseDisplayName { get; init; }
    public double PhaseImportancePercent { get; init; }
    public double PhaseWeightPercent { get; init; }
    public double MaxOverallPoints { get; init; }

    public bool Applied => Status == MetricStatus.Scored;
    public double? ScorePercent => Score01 * 100.0;
}
