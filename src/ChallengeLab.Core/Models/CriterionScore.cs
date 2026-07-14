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

    public double ScorePercent => Score01 * 100.0;
}
