using System.Collections.ObjectModel;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.ViewModels;

/// <summary>Detail report for a selected highscore / landing attempt.</summary>
public sealed class LandingReportViewModel : ViewModelBase
{
    public LandingReportViewModel(HighscoreEntry entry)
    {
        Entry = entry;
        Title = entry.ChallengeTitle;
        Subtitle = $"{entry.Utc:yyyy-MM-dd HH:mm} UTC  ·  {entry.Level}  ·  Grade {entry.Grade}";
        ScorePercent = entry.ScorePercent;
        Grade = entry.Grade;
        Notes = entry.Notes ?? "";
        CriteriaStored = entry.Criteria?.Count ?? 0;

        VerticalSpeedRaw = entry.ResolveVerticalSpeedFpm();
        VerticalSpeedDisplay = entry.VerticalSpeedDisplay;

        var firmness = entry.Criteria?.FirstOrDefault(c =>
            c.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
            c.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase));
        VerticalSpeedScorePercent = firmness?.ScorePercent;
        VerticalSpeedExplanation = EnrichNote(firmness)
            ?? (VerticalSpeedRaw is null
                ? "Vertical speed at main-gear touchdown was not recorded for this attempt."
                : MetricExplanations.DefaultCatalog("touchdown_vs", "Touchdown firmness")
                  + $" Measured: {VerticalSpeedRaw:0} fpm.");

        Metrics = new ObservableCollection<ReportMetricViewModel>();

        // Prefer full criteria list (including not-applied) so Strict-only rows still appear with notes
        var source = entry.CriteriaForReport;
        if (source.Count == 0 && entry.Criteria is { Count: > 0 })
            source = entry.Criteria;

        foreach (var c in source)
            Metrics.Add(new ReportMetricViewModel(c, isPrimary: IsVs(c)));

        if (Metrics.Count == 0 && VerticalSpeedRaw is not null)
        {
            Metrics.Add(new ReportMetricViewModel(
                new HighscoreCriterionDetail
                {
                    Id = "touchdown_vs",
                    DisplayName = "Touchdown firmness",
                    ScorePercent = VerticalSpeedScorePercent ?? 0,
                    RawValue = VerticalSpeedRaw,
                    Unit = "fpm",
                    Applied = true,
                    Note = VerticalSpeedExplanation,
                    Weight = 1.6
                },
                isPrimary: true));
        }

        MetricCount = Metrics.Count;
        HasDetail = MetricCount > 0;
        DetailHint = HasDetail
            ? $"METRICS: {MetricCount}  (stored criteria: {CriteriaStored}) — scroll down for every score + explanation"
            : $"NO METRIC BREAKDOWN (stored criteria: {CriteriaStored}). This attempt was saved without per-metric data.";
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

    public string VerticalSpeedHeadline =>
        VerticalSpeedRaw is null
            ? "Vertical speed at touchdown: not recorded"
            : $"Vertical speed at touchdown: {VerticalSpeedRaw:0} fpm";

    public string VerticalSpeedScoreLabel =>
        VerticalSpeedScorePercent is null ? "" : $"Score contribution: {VerticalSpeedScorePercent:0}%";

    public ObservableCollection<ReportMetricViewModel> Metrics { get; }

    private static string? EnrichNote(HighscoreCriterionDetail? c)
    {
        if (c is null) return null;
        var stored = c.Note?.Trim() ?? "";
        var catalog = MetricExplanations.DefaultCatalog(c.Id, c.DisplayName);
        if (string.IsNullOrWhiteSpace(stored))
            return BuildFallbackNote(c, catalog);
        if (stored.Length > 80 || stored.Contains("Measured:", StringComparison.OrdinalIgnoreCase))
            return stored;
        return BuildFallbackNote(c, catalog + " " + stored);
    }

    private static string BuildFallbackNote(HighscoreCriterionDetail c, string catalog)
    {
        var measured = c.RawValue is null
            ? ""
            : c.Unit is null
                ? $" Measured: {c.RawValue:0.##}."
                : $" Measured: {c.RawValue:0.##} {c.Unit}.";
        var score = c.Applied ? $" Score: {c.ScorePercent:0}%." : " Not scored on this difficulty.";
        return (catalog + measured + score).Trim();
    }

    private static bool IsVs(HighscoreCriterionDetail c) =>
        c.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
        c.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase);
}

public sealed class ReportMetricViewModel
{
    public ReportMetricViewModel(HighscoreCriterionDetail c, bool isPrimary)
    {
        Id = c.Id;
        DisplayName = c.DisplayName + (c.Applied ? "" : " (not scored on this level)");
        ScorePercent = c.ScorePercent;
        Weight = c.Weight;
        IsPrimary = isPrimary;
        Applied = c.Applied;
        RawDisplay = c.RawValue is null
            ? "—"
            : c.Unit is null
                ? $"{c.RawValue:0.##}"
                : $"{c.RawValue:0.##} {c.Unit}";

        var catalog = MetricExplanations.DefaultCatalog(c.Id, c.DisplayName);
        var stored = c.Note?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(stored))
            Note = catalog + (c.RawValue is null ? "" : $" Measured: {RawDisplay}.") + $" Score: {c.ScorePercent:0}%.";
        else if (stored.Length < 80 && !stored.Contains("Measured:", StringComparison.OrdinalIgnoreCase))
            Note = catalog + " " + stored + (c.RawValue is null ? "" : $" Measured: {RawDisplay}.") + $" Score: {c.ScorePercent:0}%.";
        else
            Note = stored;

        HasNote = !string.IsNullOrWhiteSpace(Note);
        Verdict = c.Applied
            ? c.ScorePercent switch
            {
                >= 95 => "Excellent",
                >= 85 => "Very good",
                >= 70 => "Good",
                >= 55 => "Acceptable",
                >= 40 => "Weak",
                > 0 => "Poor",
                _ => "Failed"
            }
            : "N/A";
    }

    public string Id { get; }
    public string DisplayName { get; }
    public double ScorePercent { get; }
    public double Weight { get; }
    public string RawDisplay { get; }
    public string Note { get; }
    public string Verdict { get; }
    public bool IsPrimary { get; }
    public bool Applied { get; }
    public bool HasNote { get; }
}
