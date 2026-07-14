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
    public void BuildContent_EmitsDateTimeSeason_LocalNoonByDefault()
    {
        var challenge = SampleChallenge(airspeed: 170);
        // Defaults: hour 12, local (UseZuluTime false)
        var flt = FltScenarioBuilder.BuildContent(challenge);

        Assert.Contains("[DateTimeSeason]", flt);
        Assert.Contains("Hours=12", flt);
        Assert.Contains("Minutes=0", flt);
        Assert.Contains("UseZuluTime=False", flt);
        Assert.Contains("Day=180", flt);
    }

    [Fact]
    public void BuildContent_RespectsTimeOfDayConfig()
    {
        var challenge = SampleChallenge(airspeed: 170);
        challenge.TimeOfDay = new TimeOfDayConfig
        {
            Hour = 15,
            Minute = 30,
            UseZuluTime = true,
            Year = 2024,
            DayOfYear = 100
        };
        var flt = FltScenarioBuilder.BuildContent(challenge);

        Assert.Contains("Hours=15", flt);
        Assert.Contains("Minutes=30", flt);
        Assert.Contains("UseZuluTime=True", flt);
        Assert.Contains("Year=2024", flt);
        Assert.Contains("Day=100", flt);
    }

    [Fact]
    public void BuildContent_IncludesLiveryAndAsciiSafeTitle()
    {
        var challenge = SampleChallenge(airspeed: 170);
        challenge.Title = "Test — special";
        challenge.AircraftLivery = "200 AIRBUS RR";
        var flt = FltScenarioBuilder.BuildContent(challenge);

        Assert.Contains("Livery=200 AIRBUS RR", flt);
        Assert.Contains("Title=Challenge Lab - Test - special", flt);
        Assert.DoesNotContain("\u2014", flt); // em dash stripped
        Assert.Contains("\u00B0", flt); // degree symbol present for lat/lon
    }

    [Fact]
    public void Write_UsesUtf8BomLikeMsfsAutosaves()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ChallengeLabTests", Guid.NewGuid().ToString("N"));
        try
        {
            var path = FltScenarioBuilder.Write(SampleChallenge(160), dir);
            var bytes = File.ReadAllBytes(path);
            // UTF-8 BOM
            Assert.True(bytes.Length >= 3);
            Assert.Equal(0xEF, bytes[0]);
            Assert.Equal(0xBB, bytes[1]);
            Assert.Equal(0xBF, bytes[2]);
            Assert.Contains("Sim=A330-200 (RR)", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void BuildContent_IsMinimal_NoCabinServiceOrLvarDump()
    {
        var flt = FltScenarioBuilder.BuildContent(SampleChallenge(170));
        Assert.DoesNotContain("CabinService", flt);
        Assert.DoesNotContain("INI_", flt);
        Assert.Contains("[Sim.0]", flt);
        Assert.Contains("[SimVars.0]", flt);
        Assert.Contains("[DateTimeSeason]", flt);
    }

    [Fact]
    public void ResolveFlightFile_DoesNotRequireCustomFlightSlot()
    {
        var challenge = SampleChallenge(180);
        // Even if someone passes true, we must not depend on overwriting CustomFlight.
        var (path, generated) = FltScenarioBuilder.ResolveFlightFile(
            challenge, resolveOverridePath: null, useMsfsCustomFlightSlot: true);
        Assert.True(generated);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain("CustomFlight", path, StringComparison.OrdinalIgnoreCase);
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
        // Do not touch real MSFS CustomFlight during unit tests.
        var (path, generated) = FltScenarioBuilder.ResolveFlightFile(
            challenge, resolveOverridePath: null, useMsfsCustomFlightSlot: false);
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
                _ => overridePath,
                useMsfsCustomFlightSlot: false);
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
