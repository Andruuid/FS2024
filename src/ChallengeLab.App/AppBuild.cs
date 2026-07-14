namespace ChallengeLab.App;

/// <summary>Visible build stamp for window title / diagnostics. Bump on every ship/test run.</summary>
public static class AppBuild
{
    public const int Number = 2227;

    public static string Tag => $"BUILD {Number}";

    public static string WindowTitleDefault => $"Challenge Lab — {Tag}";
}
