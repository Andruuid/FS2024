using ChallengeLab.Core.Models;

namespace ChallengeLab.App.ViewModels;

public sealed class CriterionResultViewModel
{
    public CriterionResultViewModel(CriterionScore score, bool isPrimary = false)
    {
        DisplayName = score.DisplayName + (score.Applied ? "" : " (not scored on this level)");
        ScorePercent = score.ScorePercent;
        Applied = score.Applied;
        Note = string.IsNullOrWhiteSpace(score.Note)
            ? "No explanation available."
            : score.Note.Trim();
        Weight = score.Weight;
        IsPrimary = isPrimary;
        RawDisplay = score.RawValue is null
            ? "—"
            : score.Unit is null
                ? $"{score.RawValue:0.##}"
                : $"{score.RawValue:0.##} {score.Unit}";
        Verdict = score.Applied
            ? score.ScorePercent switch
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

    public string DisplayName { get; }
    public double ScorePercent { get; }
    public double Weight { get; }
    public bool Applied { get; }
    public string Note { get; }
    public string RawDisplay { get; }
    public string Verdict { get; }
    public bool IsPrimary { get; }
    public double BarWidth => Applied ? ScorePercent : 0;
}
