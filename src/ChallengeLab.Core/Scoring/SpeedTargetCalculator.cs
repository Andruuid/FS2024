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
        ScoringProfileConfig profile,
        TelemetrySample? sample = null)
    {
        var offset = profile.VappToTouchdownOffsetKts > 0
            ? profile.VappToTouchdownOffsetKts
            : 5.0;

        double vapp;
        string source;

        if (challenge.AircraftSetup.VappKts is > 50 and < 250)
        {
            vapp = challenge.AircraftSetup.VappKts.Value;
            source = "challenge config";
        }
        else if (profile.DefaultVappKts is > 50 and < 250)
        {
            vapp = profile.DefaultVappKts;
            source = "scoring profile default";
        }
        else if (sample?.DesignSpeedVs0Kts is > 40 and < 200)
        {
            var factor = profile.Vs0ToVappFactor > 1.0 ? profile.Vs0ToVappFactor : 1.3;
            vapp = sample.DesignSpeedVs0Kts * factor;
            // Keep A330-typical band if estimate is wild
            vapp = Math.Clamp(vapp, 120, 160);
            source = $"Vs0×{factor:0.##}";
        }
        else
        {
            // Representative normal A330 landing weight VAPP
            vapp = 143;
            source = "A330 normal default";
        }

        // Optional weight-based nudge when we have gross weight
        if (sample?.TotalWeightLbs is > 200_000 and < 600_000 &&
            challenge.AircraftSetup.VappKts is null &&
            profile.DefaultVappKts <= 0)
        {
            vapp = EstimateVappFromWeight(sample.TotalWeightLbs.Value);
            source = "weight band";
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
