using ChallengeLab.Core.Config;
using ChallengeLab.Core.Scenarios;

namespace ChallengeLab.Core.Tests;

public class FltScenarioBuilderTests
{
    [Fact]
    public void BuildContent_UsesSpawnIasAndAircraftFromJson()
    {
        var challenge = SampleChallenge(airspeed: 170);
        var flt = FltScenarioBuilder.BuildContent(challenge);

        Assert.Contains("ZVelBodyAxis_IAS=170", flt);
        Assert.Contains("ZVelBodyAxis=170", flt);
        Assert.Contains("Sim=A330-200 (RR)", flt);
        Assert.Contains("GearHandle=0", flt);
        Assert.Contains("Latitude=", flt);
        Assert.Contains("Longitude=", flt);
        Assert.Contains("Altitude=", flt);
    }

    [Fact]
    public void BuildContent_ReflectsIasChange_SingleSourceOfTruth()
    {
        var a = FltScenarioBuilder.BuildContent(SampleChallenge(airspeed: 150));
        var b = FltScenarioBuilder.BuildContent(SampleChallenge(airspeed: 200));

        Assert.Contains("ZVelBodyAxis_IAS=150", a);
        Assert.DoesNotContain("ZVelBodyAxis_IAS=200", a);
        Assert.Contains("ZVelBodyAxis_IAS=200", b);
    }

    [Fact]
    public void Write_CreatesFileOnDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ChallengeLabTests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = FltScenarioBuilder.Write(SampleChallenge(160), dir);
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Contains("ZVelBodyAxis_IAS=160", text);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveFlightFile_GeneratesWhenFlightFileEmpty()
    {
        var challenge = SampleChallenge(175);
        challenge.FlightFile = "";
        var (path, generated) = FltScenarioBuilder.ResolveFlightFile(challenge);
        Assert.True(generated);
        Assert.True(File.Exists(path));
        Assert.Contains("ZVelBodyAxis_IAS=175", File.ReadAllText(path));
    }

    [Fact]
    public void ResolveFlightFile_UsesOverrideWhenPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ChallengeLabTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var overridePath = Path.Combine(dir, "hand.FLT");
        File.WriteAllText(overridePath, "[Main]\nTitle=override\nZVelBodyAxis_IAS=999\n");

        try
        {
            var challenge = SampleChallenge(170);
            challenge.FlightFile = "hand.FLT";
            var (path, generated) = FltScenarioBuilder.ResolveFlightFile(
                challenge,
                _ => overridePath);
            Assert.False(generated);
            Assert.Equal(Path.GetFullPath(overridePath), path);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FormatLatitudeLongitude_UsesHemisphereAndDms()
    {
        var lat = FltScenarioBuilder.FormatLatitude(41.3285);
        var lon = FltScenarioBuilder.FormatLongitude(2.1285);
        Assert.StartsWith("N", lat);
        Assert.StartsWith("E", lon);
        Assert.Contains("°", lat);
        Assert.Contains("'", lat);

        var sLat = FltScenarioBuilder.FormatLatitude(-33.9);
        var wLon = FltScenarioBuilder.FormatLongitude(-118.4);
        Assert.StartsWith("S", sLat);
        Assert.StartsWith("W", wLon);
    }

    private static ChallengeConfig SampleChallenge(double airspeed) => new()
    {
        Id = "test-challenge",
        Title = "Test Challenge",
        Description = "Unit test",
        AircraftTitles = new List<string> { "A330-200 (RR)" },
        FlightFile = "",
        Spawn = new SpawnConfig
        {
            Latitude = 43.346046,
            Longitude = 5.344348,
            AltitudeFeet = 3594.47,
            HeadingDeg = 313.71,
            AirspeedKts = airspeed,
            PitchDeg = -1.57,
            BankDeg = 0.77
        },
        AircraftSetup = new AircraftSetupConfig
        {
            GearDown = false,
            FlapsHandleIndex = 2,
            Unpause = true,
            VappKts = 143
        },
        Runway = new RunwayConfig { AirportIcao = "LFML" },
        Weather = new WeatherConfig { UseLiveWeather = false }
    };
}
