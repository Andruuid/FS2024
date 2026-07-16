using ChallengeLab.Core.Config;

namespace ChallengeLab.App.ViewModels;

public sealed class CareerRewardSlotViewModel
{
    public CareerRewardSlotViewModel(
        int stageNumber,
        CareerRankConfig rank,
        ChallengeConfig reward,
        bool isUnlocked)
    {
        StageNumber = stageNumber;
        RankTitle = rank.Title;
        _reward = reward;
        IsUnlocked = isUnlocked;
    }

    public int StageNumber { get; }
    public string RankTitle { get; }
    private readonly ChallengeConfig _reward;
    public bool IsUnlocked { get; }
    public string StageLabel => $"STAGE {StageNumber} · {RankTitle.ToUpperInvariant()}";
    public string DisplayTitle => IsUnlocked ? _reward.Title : "???";
    public string DisplaySubtitle => IsUnlocked ? _reward.Subtitle : "CLASSIFIED REWARD";
    public string Description => IsUnlocked
        ? _reward.Description
        : "Pass the classified promotion flight to reveal this future challenge.";
    public string StatusLabel => IsUnlocked ? "UNLOCKED · COMING SOON" : "LOCKED";
}
