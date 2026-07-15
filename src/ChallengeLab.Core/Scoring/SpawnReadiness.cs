using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Pure checks for post-spawn GO gate: live IAS + gear/flaps/spoilers vs challenge JSON.
/// </summary>
public static class SpawnReadiness
{
    /// <summary>Live IAS must reach this fraction of spawn target (with a small absolute floor).</summary>
    public const double MinIasFraction = 0.95;

    /// <summary>Absolute IAS tolerance floor when target is low (e.g. light aircraft).</summary>
    public const double MinIasAbsoluteToleranceKts = 5.0;

    /// <summary>
    /// Evaluate whether the aircraft matches spawn speed and start configuration.
    /// </summary>
    public static SpawnReadinessResult Evaluate(
        SpawnConfig spawn,
        AircraftSetupConfig setup,
        TelemetrySample? sample)
    {
        if (sample is null)
            return new SpawnReadinessResult(false, "waiting for telemetry…");

        var targetIas = Math.Max(0, spawn.AirspeedKts);
        var liveIas = sample.AirspeedKts;
        var iasOk = IsAirspeedReady(liveIas, targetIas);

        var gearDown = sample.GearHandlePosition > 0.5;
        var gearOk = gearDown == setup.GearDown;

        var flaps = sample.FlapsHandleIndex;
        var wantFlaps = Math.Clamp(setup.FlapsHandleIndex, 0, 5);
        var flapsOk = flaps == wantFlaps;

        var spoilersOk = IsSpoilersReady(setup, sample.SpoilersHandlePosition);

        var detail =
            $"IAS {liveIas:0}/{targetIas:0}{(iasOk ? " ok" : "")} · " +
            $"gear={(gearDown ? "down" : "up")}{(gearOk ? " ok" : " want " + (setup.GearDown ? "down" : "up"))} · " +
            $"flaps={flaps}{(flapsOk ? " ok" : " want " + wantFlaps)} · " +
            $"spoilers={FormatSpoilers(sample.SpoilersHandlePosition)}{(spoilersOk ? " ok" : " want in")}";

        var ready = iasOk && gearOk && flapsOk && spoilersOk;
        return new SpawnReadinessResult(ready, detail);
    }

    public static bool IsAirspeedReady(double liveIasKts, double targetIasKts)
    {
        if (targetIasKts <= 0)
            return true;

        // Prefer fraction of target; also accept within absolute floor of target (low-speed cases).
        var minByFraction = targetIasKts * MinIasFraction;
        var minByAbsolute = targetIasKts - MinIasAbsoluteToleranceKts;
        var threshold = Math.Min(minByFraction, Math.Max(0, minByAbsolute));
        // For high targets (e.g. 270), fraction (256.5) is the tighter useful bar;
        // for low targets, absolute (target−5) can be more forgiving than 95%.
        // Plan: ≥95% of target OR within 5 kt if that is easier — use the lower of the two thresholds.
        return liveIasKts + 1e-6 >= threshold;
    }

    public static bool IsSpoilersReady(AircraftSetupConfig setup, double spoilersHandle)
    {
        if (!setup.SpoilersRetracted)
            return true;

        // Normalize spoiler handle to 0–1 (sim may report position or percent).
        var spoiler01 = spoilersHandle > 1.5 ? spoilersHandle / 100.0 : spoilersHandle;
        var spoilersOut = spoiler01 > 0.05;
        return !spoilersOut;
    }

    private static string FormatSpoilers(double handle)
    {
        var spoiler01 = handle > 1.5 ? handle / 100.0 : handle;
        return spoiler01 <= 0.05 ? "in" : "out";
    }
}

public readonly record struct SpawnReadinessResult(bool Ready, string Detail);
