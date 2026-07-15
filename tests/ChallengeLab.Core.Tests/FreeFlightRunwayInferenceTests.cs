using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class FreeFlightRunwayInferenceTests
{
    private static readonly AirportFacility Airport = new("TEST", "ZZ", 0, 0, 10);

    [Fact]
    public void BuildEnds_UsesStartsAndCreatesReciprocalDirections()
    {
        var detail = Detail(
            starts:
            [
                new RunwayStartFacility(0, -0.01, 12, 90, 9, 1, 1),
                new RunwayStartFacility(0, 0.01, 14, 270, 27, 2, 1)
            ]);

        var ends = RunwayFacilityGeometry.BuildEnds(detail);

        Assert.Equal(2, ends.Count);
        var primary = Assert.Single(ends, e => e.RunwayId == "09L");
        var secondary = Assert.Single(ends, e => e.RunwayId == "27R");
        Assert.Equal(-0.01, primary.ThresholdLongitude, 6);
        Assert.Equal(0.01, secondary.ThresholdLongitude, 6);
        Assert.Equal(90, primary.HeadingTrueDeg, 6);
        Assert.Equal(270, secondary.HeadingTrueDeg, 6);
        Assert.Equal(12 * 3.280839895, primary.ElevationFeet, 5);
    }

    [Fact]
    public void BuildEnds_UsesWaterStartAsThreshold()
    {
        var runway = Runway() with { Surface = 27 };
        var start = new RunwayStartFacility(.002, -.012, 4, 90, 9, 1, 2);
        var detail = new AirportRunwayFacility(Airport, [runway], [start]);

        var primary = Assert.Single(
            RunwayFacilityGeometry.BuildEnds(detail),
            e => e.RunwayId == "09L");

        Assert.Equal(-.012, primary.ThresholdLongitude, 6);
        Assert.True(primary.IsWater);
    }

    [Fact]
    public void BuildEnds_FallbackPlacesThresholdsOnOppositeSidesOfCenter()
    {
        var ends = RunwayFacilityGeometry.BuildEnds(Detail());
        var primary = Assert.Single(ends, e => e.RunwayId == "09L");
        var secondary = Assert.Single(ends, e => e.RunwayId == "27R");

        Assert.True(primary.ThresholdLongitude < 0);
        Assert.True(secondary.ThresholdLongitude > 0);
        Assert.Equal(Math.Abs(primary.ThresholdLongitude), Math.Abs(secondary.ThresholdLongitude), 5);
    }

    [Theory]
    [InlineData(9, 1, 90, "09L")]
    [InlineData(27, 2, 270, "27R")]
    [InlineData(0, 0, 0, "36")]
    [InlineData(0, 0, 184, "18")]
    public void RunwayIdFormatting_HandlesNumbersDesignatorsAndHeadingFallback(
        int number, int designator, double heading, string expected)
        => Assert.Equal(expected, RunwayFacilityGeometry.FormatRunwayId(number, designator, heading));

    [Fact]
    public void BuildEnds_FiltersClosedOrLandingDisabledEndsAndMarksWater()
    {
        var runway = Runway() with
        {
            Surface = 27,
            PrimaryClosed = true,
            SecondaryLandingAllowed = true
        };
        var ends = RunwayFacilityGeometry.BuildEnds(new AirportRunwayFacility(Airport, [runway], []));

        var end = Assert.Single(ends);
        Assert.Equal("27R", end.RunwayId);
        Assert.True(end.IsWater);
    }

    [Fact]
    public void RankNearbyAirports_OrdersByAircraftDistance()
    {
        var inference = new FreeFlightRunwayInference();
        var sample = Sample(longitude: 0, track: 90);
        var far = new AirportFacility("FAR", "ZZ", 0, 1, 0);
        var near = new AirportFacility("NEAR", "ZZ", 0, .1, 0);

        var ranked = inference.RankNearbyAirports(sample, [far, near]);

        Assert.Equal("NEAR", ranked[0].Airport.Icao);
        Assert.True(ranked[0].DistanceNm < ranked[1].DistanceNm);
    }

    [Fact]
    public void Update_UsesGroundTrackNotCrabHeading_AndLocksAfterThreeSamples()
    {
        var inference = new FreeFlightRunwayInference();
        var detail = Detail(starts: [new RunwayStartFacility(0, 0, 0, 90, 9, 1, 1)]);
        var sample = Sample(longitude: -0.04, track: 90, heading: 125);
        var nearby = inference.RankNearbyAirports(sample, [Airport]);

        var first = inference.Update(sample, nearby, [detail]);
        var second = inference.Update(sample, nearby, [detail]);
        var third = inference.Update(sample, nearby, [detail]);

        Assert.NotNull(first.Candidate);
        Assert.Null(first.LockedTarget);
        Assert.Null(second.LockedTarget);
        var locked = Assert.IsType<FreeFlightTarget>(third.LockedTarget);
        Assert.Equal("09L", locked.Runway.RunwayId);
        Assert.Equal(0, locked.TrackErrorDeg, 6);

        var moved = Sample(longitude: .04, track: 270);
        var immutable = inference.Update(
            moved,
            inference.RankNearbyAirports(moved, [Airport]),
            []);
        Assert.Same(locked, immutable.LockedTarget);
    }

    [Fact]
    public void Update_RanksRunwaysAcrossCompetingNearbyAirports()
    {
        var inference = new FreeFlightRunwayInference();
        var sample = Sample(longitude: -.04, track: 90);
        var closestAirport = new AirportFacility("NEAR", "ZZ", 0, -.039, 0);
        var alignedAirport = new AirportFacility("BEST", "ZZ", 0, 0, 0);
        var nearDetail = new AirportRunwayFacility(
            closestAirport,
            [Runway()],
            [new RunwayStartFacility(.018, 0, 0, 90, 9, 1, 1)]);
        var bestDetail = new AirportRunwayFacility(
            alignedAirport,
            [Runway()],
            [new RunwayStartFacility(0, 0, 0, 90, 9, 1, 1)]);
        var nearby = inference.RankNearbyAirports(sample, [closestAirport, alignedAirport]);

        var result = inference.Update(sample, nearby, [nearDetail, bestDetail]);

        Assert.Equal("NEAR", result.NearestAirport!.Airport.Icao);
        Assert.Equal("BEST", result.Candidate!.Runway.Airport.Icao);
    }

    [Fact]
    public void Update_ChoosesBestParallelRunwayByCrossTrack()
    {
        var inference = new FreeFlightRunwayInference();
        var sample = Sample(longitude: -.04, track: 90, latitude: .001);
        var left = Runway();
        var right = Runway() with
        {
            PrimaryDesignator = 2,
            SecondaryDesignator = 1
        };
        var detail = new AirportRunwayFacility(
            Airport,
            [left, right],
            [
                new RunwayStartFacility(.001, 0, 0, 90, 9, 1, 1),
                new RunwayStartFacility(.009, 0, 0, 90, 9, 2, 1)
            ]);

        var result = inference.Update(
            sample,
            inference.RankNearbyAirports(sample, [Airport]),
            [detail]);

        Assert.Equal("09L", result.Candidate!.Runway.RunwayId);
    }

    [Fact]
    public void Update_RejectsAircraftBehindThresholdOrTrackingAway()
    {
        var inference = new FreeFlightRunwayInference();
        var detail = Detail(starts: [new RunwayStartFacility(0, 0, 0, 90, 9, 1, 1)]);
        var behind = Sample(longitude: 0.02, track: 90);
        var trackingAway = Sample(longitude: -0.02, track: 270);

        Assert.Null(inference.Update(
            behind,
            inference.RankNearbyAirports(behind, [Airport]),
            [detail]).Candidate);
        Assert.Null(inference.Update(
            trackingAway,
            inference.RankNearbyAirports(trackingAway, [Airport]),
            [detail]).Candidate);
    }

    [Fact]
    public void Update_RejectsExcessHeadingErrorAndCrossTrack()
    {
        var detail = Detail(starts: [new RunwayStartFacility(0, 0, 0, 90, 9, 1, 1)]);
        var badTrack = Sample(longitude: -.04, track: 121);
        var badCrossTrack = Sample(longitude: -.04, track: 90, latitude: .03);

        var first = new FreeFlightRunwayInference();
        Assert.Null(first.Update(
            badTrack,
            first.RankNearbyAirports(badTrack, [Airport]),
            [detail]).Candidate);
        var second = new FreeFlightRunwayInference();
        Assert.Null(second.Update(
            badCrossTrack,
            second.RankNearbyAirports(badCrossTrack, [Airport]),
            [detail]).Candidate);
    }

    [Fact]
    public void Update_RequiresAirborneAndAtLeastThirtyKnotsGroundSpeed()
    {
        var detail = Detail(starts: [new RunwayStartFacility(0, 0, 0, 90, 9, 1, 1)]);
        var onGround = Sample(-.04, 90, onGround: true);
        var slow = Sample(-.04, 90, groundSpeed: 29.9);

        var first = new FreeFlightRunwayInference();
        Assert.Null(first.Update(
            onGround,
            first.RankNearbyAirports(onGround, [Airport]),
            [detail]).Candidate);
        var second = new FreeFlightRunwayInference();
        Assert.Null(second.Update(
            slow,
            second.RankNearbyAirports(slow, [Airport]),
            [detail]).Candidate);
    }

    [Fact]
    public void Reset_ReleasesImmutableLockAndRequiresStabilityAgain()
    {
        var inference = new FreeFlightRunwayInference();
        var detail = Detail(starts: [new RunwayStartFacility(0, 0, 0, 90, 9, 1, 1)]);
        var sample = Sample(longitude: -0.03, track: 90);
        var nearby = inference.RankNearbyAirports(sample, [Airport]);
        for (var i = 0; i < 3; i++) inference.Update(sample, nearby, [detail]);
        Assert.NotNull(inference.LockedTarget);

        inference.Reset();
        var otherAirport = new AirportFacility("OTHER", "ZZ", 0, 0, 10);
        var otherDetail = new AirportRunwayFacility(otherAirport, [Runway()], detail.Starts);
        var otherNearby = inference.RankNearbyAirports(sample, [otherAirport]);
        var afterReset = inference.Update(sample, otherNearby, [otherDetail]);

        Assert.Null(afterReset.LockedTarget);
        Assert.Equal(1, afterReset.StableSamples);
        inference.Update(sample, otherNearby, [otherDetail]);
        var reacquired = inference.Update(sample, otherNearby, [otherDetail]);
        Assert.Equal("OTHER", reacquired.LockedTarget!.Runway.Airport.Icao);
    }

    [Theory]
    [InlineData(false, true, true, false, true)]
    [InlineData(false, false, true, false, false)]
    [InlineData(false, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void ChallengeFactory_AppliesGearGateOnlyToRetractableWheelsOnLand(
        bool water,
        bool retractable,
        bool wheels,
        bool floats,
        bool expected)
    {
        var end = new RunwayEndFacility(
            Airport, "09L", 0, 0, 10, 90, 2000, 45, water ? 27 : 4, water);
        var target = new FreeFlightTarget(end, 4, 0, 0);
        var sample = Sample(
            -.04,
            90,
            retractable: retractable,
            wheels: wheels,
            floats: floats);

        var challenge = FreeFlightChallengeFactory.Create(target, sample);

        Assert.Equal(expected, challenge.RequireGearDown);
        Assert.Equal("free-test-09l", challenge.Id);
        Assert.Equal("Free · TEST RWY 09L", challenge.Title);
        Assert.Equal(ChallengeMode.FreeFlight, challenge.ModeEnum);
    }

    [Fact]
    public void GenericSpeedTarget_UsesSmallAircraftVs0WithoutA330Clamp()
    {
        var challenge = new ChallengeConfig { Mode = "free_flight" };
        var settings = Settings(defaultVapp: 70);
        var sample = new TelemetrySample { DesignSpeedVs0Kts = 45 };

        var result = SpeedTargetCalculator.Resolve(challenge, settings, sample);

        Assert.Equal(58.5, result.VappKts, 1);
        Assert.Equal(53.5, result.TargetTouchdownIasKts, 1);
        Assert.Contains("DESIGN SPEED VS0", result.Source);
    }

    [Fact]
    public void GenericSpeedTarget_FallsBackToFreeProfileDefault()
    {
        var challenge = new ChallengeConfig { Mode = "free_flight" };
        var result = SpeedTargetCalculator.Resolve(
            challenge,
            Settings(defaultVapp: 70),
            new TelemetrySample { TotalWeightLbs = 400_000 });
        Assert.Equal(70, result.VappKts);
        Assert.Contains("VS0 unavailable", result.Source);
    }

    private static AirportRunwayFacility Detail(IReadOnlyList<RunwayStartFacility>? starts = null)
        => new(Airport, [Runway()], starts ?? []);

    private static RunwayFacility Runway() => new(
        CenterLatitude: 0,
        CenterLongitude: 0,
        AltitudeMeters: 10,
        HeadingTrueDeg: 90,
        LengthMeters: 2000,
        WidthMeters: 45,
        Surface: 4,
        PrimaryNumber: 9,
        PrimaryDesignator: 1,
        SecondaryNumber: 27,
        SecondaryDesignator: 2,
        PrimaryClosed: false,
        SecondaryClosed: false,
        PrimaryLandingAllowed: true,
        SecondaryLandingAllowed: true);

    private static TelemetrySample Sample(
        double longitude,
        double track,
        double heading = 90,
        double latitude = 0,
        bool retractable = false,
        bool wheels = false,
        bool floats = false,
        double groundSpeed = 100,
        bool onGround = false) => new()
    {
        Latitude = latitude,
        Longitude = longitude,
        AltitudeFeet = 1000,
        AglFeet = 1000,
        RadioHeightFeet = 1000,
        GroundTrackTrueDeg = track,
        HeadingTrueDeg = heading,
        GroundSpeedKts = groundSpeed,
        AirspeedKts = 100,
        SimOnGround = onGround,
        IsGearRetractable = retractable,
        IsGearWheels = wheels,
        IsGearFloats = floats
    };

    private static LandingSessionSettings Settings(double defaultVapp) => new(
        SettledGroundSpeedKts: 50,
        SettledHoldSeconds: 1,
        GroundTrackWindowBeforeSeconds: 3,
        GroundTrackWindowAfterSeconds: 3,
        PostTouchdownAlignmentDelaySeconds: 2,
        FlareAglFeet: 50,
        PostArmIgnoreSeconds: 4,
        RequireAirborneBeforeTouchdown: true,
        MinAirborneAglFeet: 80,
        MinAirborneSamples: 8,
        ApproachPathMinDistNm: .2,
        ApproachPathMaxDistNm: 4.5,
        DefaultVappKts: defaultVapp,
        TouchdownOffsetKts: 5,
        Vs0Factor: 1.3);
}
