using System.Collections.ObjectModel;
using ChallengeLab.Core.Highscores;

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

        VerticalSpeedRaw = entry.ResolveVerticalSpeedFpm();
        VerticalSpeedDisplay = entry.VerticalSpeedDisplay;

        var firmness = entry.Criteria.FirstOrDefault(c =>
            c.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
            c.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase));
        VerticalSpeedScorePercent = firmness?.ScorePercent;
        VerticalSpeedExplanation = firmness?.Note
            ?? (VerticalSpeedRaw is null
                ? "Vertical speed at main-gear touchdown was not recorded for this attempt."
                : "Vertical speed at main-gear touchdown. Ideal for an A330 is about −150 fpm (−100…−180). Hard landings and ultra-soft floats both score poorly.");

        Metrics.Clear();
        foreach (var c in entry.CriteriaForReport)
            Metrics.Add(new ReportMetricViewModel(c, isPrimary: IsVs(c)));

        HasDetail = Metrics.Count > 0;
        DetailHint = HasDetail
            ? $"All {Metrics.Count} metrics with explanations"
            : "This attempt has no per-metric breakdown (saved before full reports were enabled).";
    }

    public HighscoreEntry Entry { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public double ScorePercent { get; }
    public string Grade { get; }
    public string Notes { get; }
    public bool HasDetail { get; }
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

    public ObservableCollection<ReportMetricViewModel> Metrics { get; } = new();

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
        Note = string.IsNullOrWhiteSpace(c.Note)
            ? "No explanation was stored for this metric."
            : c.Note.Trim();
        IsPrimary = isPrimary;
        Applied = c.Applied;
        HasNote = !string.IsNullOrWhiteSpace(Note);
        RawDisplay = c.RawValue is null
            ? "—"
            : c.Unit is null
                ? $"{c.RawValue:0.##}"
                : $"{c.RawValue:0.##} {c.Unit}";
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
