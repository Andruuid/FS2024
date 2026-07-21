namespace ChallengeLab.Core.Snapshots;

/// <summary>
/// Strict read-back checks used before a restored snapshot may be resumed.
/// Position/on-ground checks stay in the SimConnect bridge; this evaluator owns the
/// speed and aircraft-configuration checks that can otherwise lag behind a teleport.
/// </summary>
public static class SnapshotRestoreReadiness
{
    public const double MinimumSpeedToleranceKts = 5.0;
    public const double SpeedToleranceFraction = 0.05;
    public const double GearExtendedThreshold = 0.95;
    public const double GearRetractedThreshold = 0.05;
    public const double SpoilersOutThreshold = 0.15;
    public const double MovingGroundThresholdKts = 2.0;
    private const double KnotsPerMeterPerSecond = 1.9438444924406;

    /// <summary>Accept either a normalized 0–1 value or an MSFS percent 0–100 value.</summary>
    public static double NormalizeGearExtension01(double raw)
    {
        if (!double.IsFinite(raw) || raw <= 0)
            return 0;
        return raw > 1.5 ? Math.Clamp(raw / 100.0, 0, 1) : Math.Clamp(raw, 0, 1);
    }

    public static SnapshotRestoreReadinessResult Evaluate(
        FlightStateSnapshot snapshot,
        SnapshotRestoreReadback live)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var targetSpeed = snapshot.OnGround
            ? Math.Max(0, snapshot.GroundSpeedKts)
            : Math.Max(0, snapshot.IasKts);
        var speedTolerance = Math.Max(MinimumSpeedToleranceKts, targetSpeed * SpeedToleranceFraction);

        // FREEZE_LATITUDE_LONGITUDE_SET deliberately prevents earth-relative movement,
        // so GROUND VELOCITY can read zero even when a rolling restore's exact body
        // velocity is correctly primed. For moving ground snapshots, verify all three
        // stored body-axis components instead; those are the velocities released on Resume.
        var storedBodySpeedMs = VectorMagnitude(
            snapshot.BodyVelXMs,
            snapshot.BodyVelYMs,
            snapshot.BodyVelZMs);
        var useBodyVelocity = snapshot.OnGround
                              && targetSpeed > MovingGroundThresholdKts
                              && double.IsFinite(storedBodySpeedMs)
                              && storedBodySpeedMs * KnotsPerMeterPerSecond
                              > MovingGroundThresholdKts;

        double liveSpeed;
        bool speedReady;
        if (useBodyVelocity)
        {
            var liveBodySpeedMs = VectorMagnitude(
                live.BodyVelXMs,
                live.BodyVelYMs,
                live.BodyVelZMs);
            liveSpeed = liveBodySpeedMs * KnotsPerMeterPerSecond;
            var componentToleranceMs = speedTolerance / KnotsPerMeterPerSecond;
            speedReady = double.IsFinite(liveBodySpeedMs)
                         && Math.Abs(live.BodyVelXMs - snapshot.BodyVelXMs) <= componentToleranceMs
                         && Math.Abs(live.BodyVelYMs - snapshot.BodyVelYMs) <= componentToleranceMs
                         && Math.Abs(live.BodyVelZMs - snapshot.BodyVelZMs) <= componentToleranceMs;
        }
        else
        {
            liveSpeed = snapshot.OnGround
                ? Math.Max(0, live.GroundSpeedKts)
                : Math.Max(0, live.IasKts);
            speedReady = double.IsFinite(liveSpeed)
                         && Math.Abs(liveSpeed - targetSpeed) <= speedTolerance;
        }

        var gearExtension01 = NormalizeGearExtension01(live.GearTotalPctExtended);
        var gearHandleReady = !snapshot.IsGearRetractable
                              || live.GearHandleDown == snapshot.GearHandleDown;
        var gearPhysicalReady = !snapshot.IsGearRetractable
                                || (snapshot.GearHandleDown
                                    ? gearExtension01 >= GearExtendedThreshold
                                    : gearExtension01 <= GearRetractedThreshold);
        var gearReady = gearHandleReady && gearPhysicalReady;

        var targetFlaps = Math.Clamp(
            snapshot.FlapsHandleIndex,
            0,
            Math.Max(1, snapshot.FlapsHandleCount));
        var flapsReady = live.FlapsHandleIndex == targetFlaps;

        var targetSpoilers01 = Math.Max(
            snapshot.SpoilersHandle01,
            Math.Max(snapshot.SpoilersLeft01, snapshot.SpoilersRight01));
        var wantSpoilersOut = targetSpoilers01 > SpoilersOutThreshold;
        var liveSpoilers01 = Math.Max(live.SpoilersLeft01, live.SpoilersRight01);
        var spoilersOut = liveSpoilers01 > SpoilersOutThreshold;
        var spoilersReady = spoilersOut == wantSpoilersOut;

        var parkingBrakeReady = live.ParkingBrakeOn == snapshot.ParkingBrakeOn;

        var speedLabel = useBodyVelocity ? "GS primed" : snapshot.OnGround ? "GS" : "IAS";
        var gearState = live.GearHandleDown ? "down" : "up";
        var wantedGearState = snapshot.GearHandleDown ? "down" : "up";
        var spoilerState = spoilersOut ? "out" : "in";
        var wantedSpoilerState = wantSpoilersOut ? "out" : "in";

        var detail =
            $"{speedLabel} {liveSpeed:0}/{targetSpeed:0} kt{(speedReady ? " ok" : $" want ±{speedTolerance:0}")} · " +
            $"gear={gearState} {gearExtension01:0%}" +
            $"{(gearReady ? " ok" : $" want {wantedGearState} fully")} · " +
            $"flaps={live.FlapsHandleIndex}{(flapsReady ? " ok" : $" want {targetFlaps}")} · " +
            $"spoilers={spoilerState} {liveSpoilers01:0%}" +
            $"{(spoilersReady ? " ok" : $" want {wantedSpoilerState}")} · " +
            $"park={(live.ParkingBrakeOn ? "on" : "off")}" +
            $"{(parkingBrakeReady ? " ok" : $" want {(snapshot.ParkingBrakeOn ? "on" : "off")}")}";

        return new SnapshotRestoreReadinessResult(
            speedReady,
            gearReady,
            flapsReady,
            spoilersReady,
            parkingBrakeReady,
            detail);
    }

    private static double VectorMagnitude(double x, double y, double z)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
            return double.NaN;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}

public readonly record struct SnapshotRestoreReadback(
    double IasKts,
    double GroundSpeedKts,
    bool GearHandleDown,
    double GearTotalPctExtended,
    int FlapsHandleIndex,
    double SpoilersLeft01,
    double SpoilersRight01,
    bool ParkingBrakeOn,
    double BodyVelXMs = double.NaN,
    double BodyVelYMs = double.NaN,
    double BodyVelZMs = double.NaN);

public readonly record struct SnapshotRestoreReadinessResult(
    bool SpeedReady,
    bool GearReady,
    bool FlapsReady,
    bool SpoilersReady,
    bool ParkingBrakeReady,
    string Detail)
{
    public bool ConfigurationReady =>
        GearReady && FlapsReady && SpoilersReady && ParkingBrakeReady;

    public bool Ready => SpeedReady && ConfigurationReady;
}
