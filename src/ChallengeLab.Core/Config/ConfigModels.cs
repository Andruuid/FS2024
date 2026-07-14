using System.Text.Json.Serialization;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Config;

public sealed class CatalogConfig
{
    public string AppName { get; set; } = "Challenge Lab";
    public List<ModeConfig> Modes { get; set; } = new();
    public List<string> ChallengeFiles { get; set; } = new();
}

public sealed class ModeConfig
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class ChallengeConfig
{
    public string Id { get; set; } = "";
    public string Mode { get; set; } = "hardcore_landings";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Description { get; set; } = "";
    public string DifficultyBlurb { get; set; } = "";
    public bool Available { get; set; } = true;
    public string ComingSoonNote { get; set; } = "";
    public List<string> AircraftTitles { get; set; } = new();
    public string FlightFile { get; set; } = "";
    public string ScoringProfile { get; set; } = "";
    public SpawnConfig Spawn { get; set; } = new();
    public WeatherConfig Weather { get; set; } = new();
    public AircraftSetupConfig AircraftSetup { get; set; } = new();
    public List<string> HudTips { get; set; } = new();
    public RunwayConfig Runway { get; set; } = new();

    [JsonIgnore]
    public ChallengeMode ModeEnum => ChallengeModeExtensions.FromConfigKey(Mode);
}

public sealed class SpawnConfig
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double AltitudeFeet { get; set; }
    public double HeadingDeg { get; set; }
    public double AirspeedKts { get; set; } = 140;
    public double PitchDeg { get; set; } = -3;
    public double BankDeg { get; set; }
}

public sealed class WeatherConfig
{
    public bool UseLiveWeather { get; set; }
    public double WindDirectionDeg { get; set; } = 340;
    public double WindVelocityKts { get; set; } = 28;
    public double GustKts { get; set; } = 35;
    public int VisibilitySm { get; set; } = 6;
    public string? Metar { get; set; }
    public string? WeatherPresetFile { get; set; }
}

public sealed class AircraftSetupConfig
{
    public bool GearDown { get; set; } = true;
    public int FlapsHandleIndex { get; set; } = 4;
    public bool Unpause { get; set; } = true;
}

public sealed class RunwayConfig
{
    public string AirportIcao { get; set; } = "";
    public string RunwayId { get; set; } = "";
    public double ThresholdLatitude { get; set; }
    public double ThresholdLongitude { get; set; }
    public double HeadingTrueDeg { get; set; }
    public double ElevationFeet { get; set; }
    public double LengthM { get; set; } = 3500;
    public double WidthM { get; set; } = 60;
}

public sealed class ScoringProfileConfig
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public double SettledGroundSpeedKmh { get; set; } = 50;
    public double SettledHoldSeconds { get; set; } = 1.0;
    public double FlareAglFeet { get; set; } = 50;
    public List<CriterionConfig> Criteria { get; set; } = new();
}

public sealed class CriterionConfig
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double Weight { get; set; } = 1.0;
    public string Metric { get; set; } = "";
    public string Evaluator { get; set; } = "range";
    public string SampleAt { get; set; } = "touchdown";
    public List<string> Levels { get; set; } = new() { "easy", "strict" };
    public string? Unit { get; set; }
    public string? Note { get; set; }
    public Dictionary<string, double> Params { get; set; } = new();
    public bool FailIfOutside { get; set; }

    public bool AppliesTo(DifficultyLevel level)
    {
        var key = level.ToConfigKey();
        return Levels.Count == 0 || Levels.Any(l => string.Equals(l, key, StringComparison.OrdinalIgnoreCase));
    }
}
