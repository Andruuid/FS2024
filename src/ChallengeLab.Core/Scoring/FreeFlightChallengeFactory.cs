using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Facilities;

namespace ChallengeLab.Core.Scoring;

/// <summary>Builds the immutable, inferred scoring identity for one free-flight attempt.</summary>
public static class FreeFlightChallengeFactory
{
    public static ChallengeConfig Create(
        FreeFlightTarget target,
        TelemetrySample sample,
        RunwayReferenceResolver? runwayResolver = null)
    {
        var airport = target.Runway.Airport.Icao.Trim().ToUpperInvariant();
        var runway = target.Runway.RunwayId.Trim().ToUpperInvariant();
        var capabilities = FreeFlightCapabilityResolver.Freeze(sample, target.Runway.IsWater);
        var runwayConfig = target.Runway.ToRunwayConfig();
        runwayResolver ??= new RunwayReferenceResolver();
        if (!runwayResolver.TryApplyCsv(runwayConfig))
            RunwayReferenceResolver.ApplyAimingPoint(runwayConfig, "SimConnect", "Medium");

        return new ChallengeConfig
        {
            Id = $"free-{SanitizeIdPart(airport)}-{SanitizeIdPart(runway)}",
            Mode = ChallengeMode.FreeFlight.ToConfigKey(),
            Title = $"Free · {airport} RWY {runway}",
            Subtitle = "Automatically detected free-flight landing",
            Description = "Aircraft-generic landing evaluation inferred from simulator facilities.",
            Available = true,
            Runway = runwayConfig,
            RequireGearDown = capabilities.DecisionFor(FreeFlightGateIds.Gear)?.Applicability
                              != FreeFlightGateApplicability.NotApplicable,
            FreeFlightCapabilities = capabilities,
            AircraftSetup = new AircraftSetupConfig
            {
                Unpause = true,
                VappKts = null
            },
            HudTips =
            [
                "Runway locked from position and aircraft heading. Fly the landing normally.",
                $"Path angle {target.Runway.GlideslopeDeg:0.##}° ({target.Runway.GlideslopeSource}).",
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
