namespace ChallengeLab.Core.Models;

public enum ChallengeMode
{
    HardcoreLandings,
    Disasters
}

public static class ChallengeModeExtensions
{
    public static string ToConfigKey(this ChallengeMode mode) => mode switch
    {
        ChallengeMode.HardcoreLandings => "hardcore_landings",
        ChallengeMode.Disasters => "disasters",
        _ => "hardcore_landings"
    };

    public static string ToDisplayName(this ChallengeMode mode) => mode switch
    {
        ChallengeMode.HardcoreLandings => "Hardcore Landings",
        ChallengeMode.Disasters => "Disasters",
        _ => mode.ToString()
    };

    public static ChallengeMode FromConfigKey(string? key) => key?.ToLowerInvariant() switch
    {
        "disasters" => ChallengeMode.Disasters,
        _ => ChallengeMode.HardcoreLandings
    };
}
