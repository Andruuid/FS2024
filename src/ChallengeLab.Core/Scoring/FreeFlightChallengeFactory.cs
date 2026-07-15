using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>Builds the immutable, inferred scoring identity for one free-flight attempt.</summary>
public static class FreeFlightChallengeFactory
{
    public static ChallengeConfig Create(FreeFlightTarget target, TelemetrySample sample)
    {
        var airport = target.Runway.Airport.Icao.Trim().ToUpperInvariant();
        var runway = target.Runway.RunwayId.Trim().ToUpperInvariant();
        return new ChallengeConfig
        {
            Id = $"free-{SanitizeIdPart(airport)}-{SanitizeIdPart(runway)}",
            Mode = ChallengeMode.FreeFlight.ToConfigKey(),
            Title = $"Free · {airport} RWY {runway}",
            Subtitle = "Automatically detected free-flight landing",
            Description = "Aircraft-generic landing evaluation inferred from simulator facilities.",
            Available = true,
            Runway = target.Runway.ToRunwayConfig(),
            RequireGearDown = !target.Runway.IsWater
                              && sample.IsGearRetractable
                              && sample.IsGearWheels,
            AircraftSetup = new AircraftSetupConfig
            {
                Unpause = true,
                VappKts = null
            },
            HudTips =
            [
                "Runway locked from true ground track. Fly the landing normally.",
                "Clear releases this runway and starts detection again from your current flight."
            ]
        };
    }

    private static string SanitizeIdPart(string value)
    {
        var chars = value.ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
