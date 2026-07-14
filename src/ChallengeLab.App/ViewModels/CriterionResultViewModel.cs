using ChallengeLab.Core.Models;

namespace ChallengeLab.App.ViewModels;

public sealed class CriterionResultViewModel
{
    public CriterionResultViewModel(CriterionScore score)
    {
        DisplayName = score.DisplayName;
        ScorePercent = score.ScorePercent;
        Applied = score.Applied;
        Note = score.Note ?? "";
        RawDisplay = score.RawValue is null
            ? "—"
            : score.Unit is null
                ? $"{score.RawValue:0.##}"
                : $"{score.RawValue:0.##} {score.Unit}";
    }

    public string DisplayName { get; }
    public double ScorePercent { get; }
    public bool Applied { get; }
    public string Note { get; }
    public string RawDisplay { get; }
    public double BarWidth => Applied ? ScorePercent : 0;
}
