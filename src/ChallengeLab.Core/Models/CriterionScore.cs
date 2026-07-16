namespace ChallengeLab.Core.Models;

public enum MetricStatus
{
    Scored,
    Informational,
    Unavailable,
    GateFailed,
    Degraded,
    Assumed,
    NotApplicable
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
    public double? AppliedMultiplier { get; init; }

    public string? PhaseId { get; set; }
    public string? PhaseDisplayName { get; set; }
    public double PhaseImportancePercent { get; set; }
    public double PhaseWeightPercent { get; set; }
    public double MaxOverallPoints { get; set; }

    public bool Applied => Status is MetricStatus.Scored or MetricStatus.Degraded or MetricStatus.Assumed;
    public double? ScorePercent => Score01 * 100.0;
}
