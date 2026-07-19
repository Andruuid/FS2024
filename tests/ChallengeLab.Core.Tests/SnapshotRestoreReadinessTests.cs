using ChallengeLab.Core.Snapshots;

namespace ChallengeLab.Core.Tests;

public sealed class SnapshotRestoreReadinessTests
{
    [Fact]
    public void Evaluate_RequiresPhysicalGearExtension_NotOnlyDownHandle()
    {
        var snapshot = BuildAirborneSnapshot();
        var live = ReadyReadback() with { GearTotalPctExtended = 0.42 };

        var result = SnapshotRestoreReadiness.Evaluate(snapshot, live);

        Assert.False(result.GearReady);
        Assert.False(result.Ready);
        Assert.Contains("want down fully", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_RequiresSpeedToMatchInBothDirections()
    {
        var snapshot = BuildAirborneSnapshot();

        var tooSlow = SnapshotRestoreReadiness.Evaluate(
            snapshot,
            ReadyReadback() with { IasKts = 125 });
        var tooFast = SnapshotRestoreReadiness.Evaluate(
            snapshot,
            ReadyReadback() with { IasKts = 155 });

        Assert.False(tooSlow.SpeedReady);
        Assert.False(tooFast.SpeedReady);
        Assert.False(tooSlow.Ready);
        Assert.False(tooFast.Ready);
    }

    [Fact]
    public void Evaluate_RequiresFlapsSpoilersAndParkingBrake()
    {
        var snapshot = BuildAirborneSnapshot();
        var live = ReadyReadback() with
        {
            FlapsHandleIndex = 1,
            SpoilersLeft01 = 0.6,
            SpoilersRight01 = 0.6,
            ParkingBrakeOn = true
        };

        var result = SnapshotRestoreReadiness.Evaluate(snapshot, live);

        Assert.False(result.FlapsReady);
        Assert.False(result.SpoilersReady);
        Assert.False(result.ParkingBrakeReady);
        Assert.False(result.ConfigurationReady);
        Assert.False(result.Ready);
    }

    [Fact]
    public void Evaluate_UsesStoredSpoilerSurfacesWhenHandleWasStowed()
    {
        var snapshot = BuildAirborneSnapshot();
        snapshot.SpoilersLeft01 = 1;
        snapshot.SpoilersRight01 = 1;

        var stowed = SnapshotRestoreReadiness.Evaluate(snapshot, ReadyReadback());
        var deployed = SnapshotRestoreReadiness.Evaluate(
            snapshot,
            ReadyReadback() with { SpoilersLeft01 = 1, SpoilersRight01 = 1 });

        Assert.False(stowed.SpoilersReady);
        Assert.True(deployed.SpoilersReady);
    }

    [Fact]
    public void Evaluate_ReadyWhenFullStoredStateMatches()
    {
        var result = SnapshotRestoreReadiness.Evaluate(
            BuildAirborneSnapshot(),
            ReadyReadback());

        Assert.True(result.SpeedReady);
        Assert.True(result.ConfigurationReady);
        Assert.True(result.Ready);
    }

    [Fact]
    public void Evaluate_NormalizesPercentScaleForGearReadback()
    {
        var result = SnapshotRestoreReadiness.Evaluate(
            BuildAirborneSnapshot(),
            ReadyReadback() with { GearTotalPctExtended = 100 });

        Assert.True(result.GearReady);
        Assert.True(result.Ready);
        Assert.Contains("gear=down 100% ok", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_GroundSnapshotUsesStoredGroundSpeed()
    {
        var snapshot = BuildAirborneSnapshot();
        snapshot.OnGround = true;
        snapshot.GroundSpeedKts = 0;
        snapshot.IasKts = 140;

        var result = SnapshotRestoreReadiness.Evaluate(
            snapshot,
            ReadyReadback() with { IasKts = 0, GroundSpeedKts = 2 });

        Assert.True(result.SpeedReady);
        Assert.True(result.Ready);
        Assert.StartsWith("GS ", result.Detail, StringComparison.Ordinal);
    }

    private static FlightStateSnapshot BuildAirborneSnapshot() => new()
    {
        OnGround = false,
        IasKts = 140,
        IsGearRetractable = true,
        GearHandleDown = true,
        GearTotalPctExtended = 1,
        FlapsHandleCount = 4,
        FlapsHandleIndex = 2,
        SpoilersHandle01 = 0,
        ParkingBrakeOn = false
    };

    private static SnapshotRestoreReadback ReadyReadback() => new(
        IasKts: 140,
        GroundSpeedKts: 140,
        GearHandleDown: true,
        GearTotalPctExtended: 1,
        FlapsHandleIndex: 2,
        SpoilersLeft01: 0,
        SpoilersRight01: 0,
        ParkingBrakeOn: false);
}
