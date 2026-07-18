namespace ChallengeLab.App.ViewModels;

/// <summary>
/// The HUD workflow selected by the pilot. Default is Free; Normal is entered when a challenge
/// (or career assignment) is loaded. Intentionally not persisted across app restarts.
/// </summary>
public enum HudOperatingMode
{
    Normal,
    Free
}
