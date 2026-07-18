using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;

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

        var aircraftTitle = string.IsNullOrWhiteSpace(sample.AircraftTitle)
            ? null
            : sample.AircraftTitle.Trim();
        var vappMatch = AircraftVappCatalog.Default.TryMatch(aircraftTitle);

        return new ChallengeConfig
        {
            Id = $"free-{SanitizeIdPart(airport)}-{SanitizeIdPart(runway)}",
            Mode = ChallengeMode.FreeFlight.ToConfigKey(),
            Title = $"Free · {airport} RWY {runway}",
            Subtitle = aircraftTitle is null
                ? "Automatically detected free-flight landing"
                : $"Free-flight landing · {aircraftTitle}",
            Description = "Aircraft-generic landing evaluation inferred from simulator facilities.",
            Available = true,
            Runway = runwayConfig,
            RequireGearDown = capabilities.DecisionFor(FreeFlightGateIds.Gear)?.Applicability
                              != FreeFlightGateApplicability.NotApplicable,
            FreeFlightCapabilities = capabilities,
            AircraftTitles = aircraftTitle is null ? [] : [aircraftTitle],
            AircraftSetup = new AircraftSetupConfig
            {
                Unpause = true,
                // Freeze DB VAPP into the attempt when TITLE matched; SpeedTargetCalculator
                // still re-resolves from live sample title if this is left null.
                VappKts = vappMatch?.Entry.VappKts
            },
            HudTips =
            [
                "Runway locked from position and aircraft heading. Fly the landing normally.",
                $"Path angle {target.Runway.GlideslopeDeg:0.##}° ({target.Runway.GlideslopeSource}).",
                aircraftTitle is null
                    ? "Aircraft TITLE not yet available — VAPP uses VS0 or the free profile fallback."
                    : vappMatch is null
                        ? $"Aircraft '{aircraftTitle}' not in VAPP DB — using VS0 or free profile fallback."
                        : $"Aircraft VAPP {vappMatch.Entry.VappKts:0} kt from DB ({vappMatch.Entry.Label}).",
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
