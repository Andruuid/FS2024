using System.Collections.ObjectModel;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.ViewModels;

public sealed class LandingReportViewModel : ViewModelBase
{
    public LandingReportViewModel(HighscoreEntry entry)
    {
        Entry = entry;
        Title = entry.ChallengeTitle;
        Subtitle = $"{entry.Utc:yyyy-MM-dd HH:mm} UTC  ·  Grade {entry.Grade}" +
                   (entry.IsLegacy ? "  ·  LEGACY RECORD" : "");
        ScorePercent = entry.ScorePercent;
        Grade = entry.Grade;
        Notes = entry.Breakdown;
        CriteriaStored = entry.Criteria?.Count ?? 0;

        VerticalSpeedRaw = entry.ResolveVerticalSpeedFpm();
        VerticalSpeedDisplay = entry.VerticalSpeedDisplay;
        var firmness = entry.Criteria?.FirstOrDefault(IsVs);
        VerticalSpeedScorePercent = firmness?.ScorePercent;
        VerticalSpeedExplanation = EnrichNote(firmness)
            ?? (VerticalSpeedRaw is null
                ? "Vertical speed at main-gear touchdown was not recorded for this attempt."
                : MetricExplanations.DefaultCatalog("touchdown_vs", "Touchdown firmness") +
                  $" Measured: {VerticalSpeedRaw:0} fpm. Historical score unavailable.");

        Metrics = new ObservableCollection<ReportMetricViewModel>();
        var source = entry.CriteriaForReport;
        if (source.Count == 0 && entry.Criteria is { Count: > 0 }) source = entry.Criteria;
        foreach (var criterion in source)
            Metrics.Add(new ReportMetricViewModel(criterion, IsVs(criterion)));

        MetricCount = Metrics.Count;
        HasDetail = MetricCount > 0;
        DetailHint = HasDetail
            ? $"METRICS: {MetricCount} (stored criteria: {CriteriaStored}) — scroll for every result and explanation"
            : $"NO METRIC BREAKDOWN (stored criteria: {CriteriaStored}). This attempt predates metric storage.";
    }

    public HighscoreEntry Entry { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public double ScorePercent { get; }
    public string Grade { get; }
    public string Notes { get; }
    public bool HasDetail { get; }
    public int MetricCount { get; }
    public int CriteriaStored { get; }
    public string DetailHint { get; }
    public double? VerticalSpeedRaw { get; }
    public string VerticalSpeedDisplay { get; }
    public double? VerticalSpeedScorePercent { get; }
    public string VerticalSpeedExplanation { get; }
    public string VerticalSpeedHeadline => VerticalSpeedRaw is null
        ? "Vertical speed at touchdown: not recorded"
        : $"Vertical speed at touchdown: {VerticalSpeedRaw:0} fpm";
    public string VerticalSpeedScoreLabel => VerticalSpeedScorePercent is null
        ? "Score: N/A"
        : $"Metric score: {VerticalSpeedScorePercent:0}%";
    public ObservableCollection<ReportMetricViewModel> Metrics { get; }

    private static string? EnrichNote(HighscoreCriterionDetail? criterion)
    {
        if (criterion is null) return null;
        var stored = criterion.Note?.Trim() ?? "";
        var catalog = MetricExplanations.DefaultCatalog(criterion.Id, criterion.DisplayName);
        if (string.IsNullOrWhiteSpace(stored)) return BuildFallbackNote(criterion, catalog);
        if (stored.Length > 80 || stored.Contains("Measured:", StringComparison.OrdinalIgnoreCase)) return stored;
        return BuildFallbackNote(criterion, catalog + " " + stored);
    }

    private static string BuildFallbackNote(HighscoreCriterionDetail criterion, string catalog)
    {
        var measured = criterion.RawValue is null
            ? ""
            : criterion.Unit is null
                ? $" Measured: {criterion.RawValue:0.##}."
                : $" Measured: {criterion.RawValue:0.##} {criterion.Unit}.";
        var score = criterion.ScorePercent is null ? " Score unavailable." : $" Score: {criterion.ScorePercent:0}%.";
        return (catalog + measured + score).Trim();
    }

    private static bool IsVs(HighscoreCriterionDetail criterion) =>
        criterion.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
        criterion.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase);
}

public sealed class ReportMetricViewModel
{
    public ReportMetricViewModel(HighscoreCriterionDetail criterion, bool isPrimary)
    {
        Id = criterion.Id;
        Status = criterion.EffectiveStatus;
        DisplayName = criterion.DisplayName + (Status switch
        {
            MetricStatus.Informational => " (informational)",
            MetricStatus.Unavailable => " (score unavailable)",
            _ => ""
        });
        ScorePercent = criterion.ScorePercent;
        ScoreDisplay = ScorePercent is null ? "N/A" : $"{ScorePercent:0}%";
        IsPrimary = isPrimary;
        RawDisplay = criterion.RawValue is null
            ? "—"
            : criterion.Unit is null
                ? $"{criterion.RawValue:0.##}"
                : $"{criterion.RawValue:0.##} {criterion.Unit}";
        InfluenceDisplay = criterion.MaxOverallPoints > 0
            ? $"{criterion.PhaseImportancePercent:0.##}% of {criterion.PhaseDisplayName} · {criterion.MaxOverallPoints:0.##} max overall points"
            : criterion.Weight > 0
                ? $"Legacy weight {criterion.Weight:0.##}"
                : Status switch
                {
                    MetricStatus.Informational => "Informational · no score credit",
                    MetricStatus.GateFailed => "Safety gate",
                    MetricStatus.Unavailable => "Score unavailable",
                    _ => ""
                };

        var catalog = MetricExplanations.DefaultCatalog(criterion.Id, criterion.DisplayName);
        var stored = criterion.Note?.Trim() ?? "";
        Note = string.IsNullOrWhiteSpace(stored)
            ? catalog + (criterion.RawValue is null ? "" : $" Measured: {RawDisplay}.") +
              (ScorePercent is null ? " Score unavailable." : $" Score: {ScorePercent:0}%.")
            : stored;
        Verdict = Status switch
        {
            MetricStatus.Unavailable => "N/A",
            MetricStatus.Informational => "INFO",
            MetricStatus.GateFailed => "FAILED GATE",
            _ => ScorePercent switch
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

    public string Id { get; }
    public string DisplayName { get; }
    public double? ScorePercent { get; }
    public string ScoreDisplay { get; }
    public string RawDisplay { get; }
    public string Note { get; }
    public string Verdict { get; }
    public string InfluenceDisplay { get; }
    public bool IsPrimary { get; }
    public MetricStatus Status { get; }
    public bool IsScored => Status == MetricStatus.Scored;
    public double BarValue => ScorePercent ?? 0;
}
