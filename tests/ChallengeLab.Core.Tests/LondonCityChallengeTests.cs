using ChallengeLab.Core.Config;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public class LondonCityChallengeTests
{
    private const string ChallengePath = "challenges/london-city-night-steep-final.json";

    [Fact]
    public void Catalog_LoadsPlayableChallengeWithRequestedConfiguration()
    {
        var loader = new ConfigLoader(FindConfig());
        var catalog = loader.LoadCatalog();

        Assert.Contains(ChallengePath, catalog.ChallengeFiles);
        var challenge = loader.LoadAllChallenges(catalog)
            .Single(item => item.Id == "london-city-night-steep-final");

        Assert.Equal("london-city-night-steep-final", challenge.Id);
        Assert.Equal("hardcore_landings", challenge.Mode);
        Assert.Equal("London City Night Steep Final", challenge.Title);
        Assert.True(challenge.Available);
        Assert.True(challenge.RequireGearDown);
        Assert.Equal(
            ["A330-200 (RR)", "Airbus A330-200", "A330"],
            challenge.AircraftTitles);
        Assert.Equal("200 AIRBUS RR", challenge.AircraftLivery);

        Assert.Equal(2600, challenge.Spawn.AltitudeFeet);
        Assert.Equal(92.88, challenge.Spawn.HeadingDeg);
        Assert.Equal(143, challenge.Spawn.AirspeedKts);
        Assert.Equal(-5.5, challenge.Spawn.PitchDeg);
        Assert.Equal(0, challenge.Spawn.BankDeg);

        Assert.True(challenge.AircraftSetup.GearDown);
        Assert.Equal(4, challenge.AircraftSetup.FlapsHandleIndex);
        Assert.True(challenge.AircraftSetup.SpoilersRetracted);
        Assert.False(challenge.AircraftSetup.ParkingBrakeOn);
        Assert.False(challenge.AircraftSetup.Unpause);
        Assert.Equal(143, challenge.AircraftSetup.VappKts);

        Assert.False(challenge.Weather.UseLiveWeather);
        Assert.Equal(0, challenge.Weather.WindVelocityKts);
        Assert.Equal(0, challenge.Weather.GustKts);
        Assert.Equal(10, challenge.Weather.VisibilitySm);
        Assert.Equal(".\\WeatherPresets\\clearsky.WPR", challenge.Weather.WeatherPresetFile);
        Assert.Equal("EGLC 160000Z 00000KT CAVOK 15/10 Q1015", challenge.Weather.Metar);

        Assert.Equal(1, challenge.TimeOfDay.Hour);
        Assert.Equal(0, challenge.TimeOfDay.Minute);
        Assert.False(challenge.TimeOfDay.UseZuluTime);
        Assert.Equal(2026, challenge.TimeOfDay.Year);
        Assert.Equal(197, challenge.TimeOfDay.DayOfYear);

        Assert.Equal("EGLC", challenge.Runway.AirportIcao);
        Assert.Equal("09", challenge.Runway.RunwayId);
        Assert.Equal(51.505527778, challenge.Runway.ThresholdLatitude);
        Assert.Equal(0.045758333, challenge.Runway.ThresholdLongitude);
        Assert.Equal(92.88, challenge.Runway.HeadingTrueDeg);
        Assert.Equal(17, challenge.Runway.ElevationFeet);
        Assert.Equal(1508, challenge.Runway.LengthM);
        Assert.Equal(30, challenge.Runway.WidthM);
        Assert.Equal(5.5, challenge.Runway.GlideslopeDeg);
        Assert.Equal("challenge", challenge.Runway.GlideslopeSource);

        Assert.DoesNotContain(challenge.Id, catalog.Career!.AssignmentChallengeIds);
        Assert.DoesNotContain(catalog.Career.Ranks,
            rank => string.Equals(rank.RewardChallengeId, challenge.Id, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Spawn_IsCenteredOnExactSteepPath_AndReverseIsProhibited()
    {
        var loader = new ConfigLoader(FindConfig());
        var challenge = loader.LoadChallenge(ChallengePath);

        Assert.True(RunwayPathGeometry.TryGetState(
            challenge.Spawn.Latitude,
            challenge.Spawn.Longitude,
            challenge.Spawn.AltitudeFeet,
            challenge.Runway,
            out var state));

        Assert.InRange(state.ApproachDistanceNm, 4.2502, 4.2504);
        Assert.InRange(Math.Abs(state.LateralMeters), 0, 0.01);
        Assert.InRange(Math.Abs(state.AltitudeErrorFeet), 0, 0.01);

        var loadedKey = loader.LoadEvaluationKey();
        Assert.True(loadedKey.IsValid, string.Join("; ", loadedKey.Errors));
        var effective = EffectiveEvaluationProfileBuilder.Build(loadedKey.Key!, challenge);
        var reverse = effective.Key.Phases
            .Single(phase => phase.Id == "rollout")
            .Penalties!.ReverseThrust!;

        Assert.Equal(ReverseThrustPolicies.Prohibited, reverse.Policy);
        Assert.Contains("night-noise", reverse.ExceptionReason, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "config", "catalog.json")))
                return Path.Combine(directory.FullName, "config");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("config not found");
    }
}
