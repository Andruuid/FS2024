using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.App.ViewModels;

public sealed class ChallengeCardViewModel : ViewModelBase
{
    public ChallengeConfig Config { get; }

    public ChallengeCardViewModel(ChallengeConfig config) => Config = config;

    public string Id => Config.Id;
    public string Title => Config.Title;
    public string Subtitle => Config.Subtitle;
    public string Description => Config.Description;
    public bool Available => Config.Available;
    public string ComingSoonNote => Config.ComingSoonNote;
    public ChallengeMode Mode => Config.ModeEnum;
    public string ModeTitle => Mode.ToDisplayName();
    public string StatusLabel => Available ? "READY" : "COMING SOON";
    public IReadOnlyList<string> HudTips => Config.HudTips;
}
