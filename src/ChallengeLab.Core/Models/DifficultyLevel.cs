namespace ChallengeLab.Core.Models;

public enum DifficultyLevel
{
    Easy,
    Strict
}

public static class DifficultyLevelExtensions
{
    public static string ToConfigKey(this DifficultyLevel level) => level switch
    {
        DifficultyLevel.Easy => "easy",
        DifficultyLevel.Strict => "strict",
        _ => "easy"
    };

    public static string ToDisplayName(this DifficultyLevel level) => level switch
    {
        DifficultyLevel.Easy => "Easy",
        DifficultyLevel.Strict => "Strict",
        _ => level.ToString()
    };
}
