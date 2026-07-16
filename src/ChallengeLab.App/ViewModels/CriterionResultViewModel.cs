using ChallengeLab.Core.Models;

namespace ChallengeLab.App.ViewModels;

public sealed class CriterionResultViewModel
{
    public CriterionResultViewModel(CriterionScore score, bool isPrimary = false)
    {
        DisplayName = score.DisplayName + score.Status switch
        {
            MetricStatus.Informational => " (informational)",
            MetricStatus.Unavailable => " (telemetry unavailable)",
            MetricStatus.Assumed => " (assumed fallback)",
            MetricStatus.NotApplicable => " (not applicable)",
            MetricStatus.Degraded => " (degraded telemetry)",
            _ => ""
        };
        ScorePercent = score.ScorePercent;
        ScoreDisplay = score.ScorePercent is null ? "N/A" : $"{score.ScorePercent:0}%";
        Status = score.Status;
        Note = string.IsNullOrWhiteSpace(score.Note) ? "No explanation available." : score.Note.Trim();
        IsPrimary = isPrimary;
        RawDisplay = score.RawValue is null
            ? "—"
            : score.Unit is null
                ? $"{score.RawValue:0.##}"
                : $"{score.RawValue:0.##} {score.Unit}";
        InfluenceDisplay = score.MaxOverallPoints > 0
            ? $"{score.PhaseImportancePercent:0.##}% of {score.PhaseDisplayName} · {score.MaxOverallPoints:0.##} max overall points"
            : score.Status switch
            {
                MetricStatus.Informational => "Informational · no score credit",
                MetricStatus.GateFailed => "Safety gate · applied to complete total",
                MetricStatus.Unavailable => "Required telemetry unavailable",
                MetricStatus.Assumed => score.AppliedMultiplier is { } assumedMultiplier
                    ? $"Assumed telemetry adjustment x {assumedMultiplier:0.###}"
                    : "Assumed 50% metric credit",
                MetricStatus.NotApplicable => "Not applicable - neutral",
                MetricStatus.Degraded => "Calculated proxy retained",
                _ => ""
            };
        Verdict = score.Status switch
        {
            MetricStatus.Unavailable => "N/A",
            MetricStatus.Informational => "INFO",
            MetricStatus.GateFailed => "FAILED GATE",
            MetricStatus.Degraded => "DEGRADED",
            MetricStatus.Assumed => "ASSUMED",
            MetricStatus.NotApplicable => "N/A",
            _ => score.ScorePercent switch
            {
                >= 95 => "Excellent",
                >= 85 => "Very good",
                >= 70 => "Good",
                >= 55 => "Acceptable",
                >= 40 => "Weak",
                > 0 => "Poor",
                _ => "Failed"
            }
        };
    }

    public string DisplayName { get; }
    public double? ScorePercent { get; }
    public string ScoreDisplay { get; }
    public MetricStatus Status { get; }
    public string Note { get; }
    public string RawDisplay { get; }
    public string Verdict { get; }
    public string InfluenceDisplay { get; }
    public bool IsPrimary { get; }
    public bool IsScored => Status is MetricStatus.Scored or MetricStatus.Degraded
                            || Status == MetricStatus.Assumed && ScorePercent is not null;
    public double BarValue => ScorePercent ?? 0;
}
