using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Computes approach / touchdown airspeed targets for scoring.
/// Target touchdown IAS = VAPP − offset (default 5 kt).
/// </summary>
public static class SpeedTargetCalculator
{
    /// <summary>
    /// Resolve VAPP and target touchdown IAS for this landing.
    /// Priority: challenge override → profile default → Vs0 estimate → A330 normal default.
    /// </summary>
    public static (double VappKts, double TargetTouchdownIasKts, string Source) Resolve(
        ChallengeConfig challenge,
        LandingSessionSettings settings,
        TelemetrySample? sample = null)
    {
        var offset = settings.TouchdownOffsetKts;

        double vapp;
        string source;

        if (challenge.AircraftSetup.VappKts is > 50 and < 250)
        {
            vapp = challenge.AircraftSetup.VappKts.Value;
            source = "challenge config";
        }
        else if (sample?.DesignSpeedVs0Kts is > 40 and < 200)
        {
            var factor = settings.Vs0Factor;
            vapp = sample.DesignSpeedVs0Kts * factor;
            // Keep A330-typical band if estimate is wild
            vapp = Math.Clamp(vapp, 120, 160);
            source = $"Vs0×{factor:0.##}";
        }
        else if (sample?.TotalWeightLbs is > 200_000 and < 600_000)
        {
            vapp = EstimateVappFromWeight(sample.TotalWeightLbs.Value);
            source = "weight band";
        }
        else
        {
            vapp = settings.DefaultVappKts;
            source = "evaluation key default";
        }

        var targetTd = vapp - offset;
        return (Math.Round(vapp, 1), Math.Round(targetTd, 1), source);
    }

    /// <summary>
    /// Rough A330-family VAPP by landing weight (lbs). Used when no better source exists.
    /// </summary>
    public static double EstimateVappFromWeight(double weightLbs) => weightLbs switch
    {
        < 340_000 => 130, // light → ~125–135 TD with −5
        < 400_000 => 143, // normal → ~138–142 TD
        < 460_000 => 150, // heavy → ~145–155 TD
        _ => 155
    };
}
