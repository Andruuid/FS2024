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
    /// After this many seconds, unlock GO when IAS matches even if gear/flaps/spoilers
    /// still disagree. Under SET PAUSE many airframes (A330) ignore surface animation —
    /// handles and panels only finish after resume. Soft-ready is best-effort, not a fail.
    /// </summary>
    public const double SoftTimeoutSeconds = 12.0;

    /// <summary>
    /// Absolute ceiling: unlock GO with any telemetry so PREPARING never spins forever
    /// (e.g. user ESC-resumed, or IAS sample stuck low).
    /// </summary>
    public const double HardTimeoutSeconds = 18.0;

    /// <summary>
    /// Evaluate whether the aircraft matches spawn speed and start configuration.
    /// </summary>
    /// <param name="elapsedSeconds">Seconds since prep started (for soft/hard timeout).</param>
    public static SpawnReadinessResult Evaluate(
        SpawnConfig spawn,
        AircraftSetupConfig setup,
        TelemetrySample? sample,
        double elapsedSeconds = 0)
    {
        if (sample is null)
            return new SpawnReadinessResult(
                Ready: false,
                CriticalReady: false,
                SoftReady: false,
                ForceReady: false,
                Detail: "waiting for telemetry…");

        var targetIas = Math.Max(0, spawn.AirspeedKts);
        var liveIas = sample.AirspeedKts;
        var iasOk = IsAirspeedReady(liveIas, targetIas);

        var gearDown = sample.GearHandlePosition > 0.5;
        var gearOk = gearDown == setup.GearDown;

        var flaps = sample.FlapsHandleIndex;
        var wantFlaps = Math.Clamp(setup.FlapsHandleIndex, 0, 5);
        var flapsOk = flaps == wantFlaps;

        var spoilersOk = IsSpoilersReady(setup, sample);
        var spoilerLabel = FormatSpoilers(sample);

        var detail =
            $"IAS {liveIas:0}/{targetIas:0}{(iasOk ? " ok" : "")} · " +
            $"gear={(gearDown ? "down" : "up")}{(gearOk ? " ok" : " want " + (setup.GearDown ? "down" : "up"))} · " +
            $"flaps={flaps}{(flapsOk ? " ok" : " want " + wantFlaps)} · " +
            $"spoilers={spoilerLabel}{(spoilersOk ? " ok" : " want in")}";

        // Critical: speed + primary config. Surfaces often stick under SET PAUSE.
        var criticalReady = iasOk && gearOk && flapsOk;
        var hardReady = criticalReady && spoilersOk;

        // Soft: IAS ok long enough — gear/flaps/spoilers may only finish after GO/unpause.
        var softReady = !hardReady
                        && iasOk
                        && elapsedSeconds >= SoftTimeoutSeconds;

        // Hard: never block the pilot forever (ESC-resume, stuck handles, missing IAS edge).
        var forceReady = !hardReady
                         && !softReady
                         && elapsedSeconds >= HardTimeoutSeconds;

        var ready = hardReady || softReady || forceReady;
        if (softReady && !hardReady)
            detail += " · soft-ready (surfaces may finish after GO)";
        if (forceReady)
            detail += " · force-ready (timeout)";

        return new SpawnReadinessResult(ready, criticalReady, softReady, forceReady, detail);
    }

    public static bool IsAirspeedReady(double liveIasKts, double targetIasKts)
    {
        if (targetIasKts <= 0)
            return true;

        var minByFraction = targetIasKts * MinIasFraction;
        var minByAbsolute = targetIasKts - MinIasAbsoluteToleranceKts;
        var threshold = Math.Min(minByFraction, Math.Max(0, minByAbsolute));
        return liveIasKts + 1e-6 >= threshold;
    }

    public static bool IsSpoilersReady(AircraftSetupConfig setup, TelemetrySample sample)
    {
        if (!setup.SpoilersRetracted)
            return true;

        // Prefer actual wing surfaces — handle alone is unreliable on Airbus (armed ≠ deployed).
        if (sample.SpoilersSurfacePosition is double surface)
            return !IsNormalizedOut(NormalizeSpoiler01(surface));

        return !IsSpoilersOut(sample.SpoilersHandlePosition);
    }

    /// <summary>Legacy single-value API used by SimConnect config settle.</summary>
    public static bool IsSpoilersReady(AircraftSetupConfig setup, double spoilersHandle)
    {
        if (!setup.SpoilersRetracted)
            return true;
        return !IsSpoilersOut(spoilersHandle);
    }

    /// <summary>
    /// True when a raw spoiler reading means "extended".
    /// Accepts 0–1, 0–100%, or 0–16383 position units.
    /// </summary>
    public static bool IsSpoilersOut(double spoilersHandle)
        => IsNormalizedOut(NormalizeSpoiler01(spoilersHandle));

    /// <summary>
    /// Normalize any common spoiler scale to 0–1 (0 = retracted, 1 = full).
    /// </summary>
    public static double NormalizeSpoiler01(double raw)
    {
        if (!double.IsFinite(raw) || raw <= 0)
            return 0;

        // 16K position units (MSFS "position" enum for many control surfaces).
        if (raw > 100.5)
            return Math.Clamp(raw / 16383.0, 0, 1);

        // Percent 0–100.
        if (raw > 1.5)
            return Math.Clamp(raw / 100.0, 0, 1);

        // Already percent-over-100 / 0–1.
        return Math.Clamp(raw, 0, 1);
    }

    private static bool IsNormalizedOut(double spoiler01)
        // Surfaces/handle beyond ~15% count as "out". Small residuals and "armed"
        // lever noise stay "in" so a visually stowed A330 unlocks GO.
        => spoiler01 > 0.15;

    private static string FormatSpoilers(TelemetrySample sample)
    {
        if (sample.SpoilersSurfacePosition is double surface)
        {
            var s01 = NormalizeSpoiler01(surface);
            var label = IsNormalizedOut(s01) ? "out" : "in";
            return $"{label}(surf {s01:0%})";
        }

        var h01 = NormalizeSpoiler01(sample.SpoilersHandlePosition);
        var hLabel = IsNormalizedOut(h01) ? "out" : "in";
        return $"{hLabel}(h {h01:0%})";
    }
}

public readonly record struct SpawnReadinessResult(
    bool Ready,
    bool CriticalReady,
    bool SoftReady,
    bool ForceReady,
    string Detail);
