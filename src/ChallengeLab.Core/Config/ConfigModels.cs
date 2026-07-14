using System.Text.Json.Serialization;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Config;

public sealed class CatalogConfig
{
    public string AppName { get; set; } = "Challenge Lab";
    public List<ModeConfig> Modes { get; set; } = new();
    public List<string> ChallengeFiles { get; set; } = new();

    /// <summary>
    /// Path under config/ to the phase-weighted evaluation key JSON (editable without recompiling).
    /// Default: scoring/profiles/landing-evaluation-key.json
    /// </summary>
    public string EvaluationKey { get; set; } = "scoring/profiles/landing-evaluation-key.json";
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

    /// <summary>
    /// When true (default), gear-down is a safety gate: no score credit if down;
    /// gear-up applies a heavy overall score multiplier. Set false for belly/water landings.
    /// </summary>
    public bool RequireGearDown { get; set; } = true;

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

    /// <summary>
    /// Optional approach speed (VAPP) in KIAS for this challenge.
    /// When set, target touchdown IAS = VAPP − profile offset (default 5 kt).
    /// </summary>
    public double? VappKts { get; set; }
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

    /// <summary>Score when on ground and groundspeed stays below this (knots).</summary>
    public double SettledGroundSpeedKts { get; set; } = 50;

    /// <summary>Legacy alias (km/h). Used only if SettledGroundSpeedKts was not set in older configs.</summary>
    public double SettledGroundSpeedKmh { get; set; }

    public double SettledHoldSeconds { get; set; } = 1.0;
    public double FlareAglFeet { get; set; } = 50;

    /// <summary>Seconds before touchdown included in ground-track path scoring.</summary>
    public double GroundTrackWindowBeforeSeconds { get; set; } = 3.0;

    /// <summary>Seconds after touchdown included in ground-track path scoring.</summary>
    public double GroundTrackWindowAfterSeconds { get; set; } = 3.0;

    /// <summary>
    /// Seconds after touchdown before rollout heading/crab alignment scoring starts
    /// (pilot should be de-crabbed and on rudder by then).
    /// </summary>
    public double PostTouchdownAlignmentDelaySeconds { get; set; } = 2.0;

    /// <summary>Default VAPP (KIAS) when challenge does not override and Vs0 is unavailable.</summary>
    public double DefaultVappKts { get; set; } = 143;

    /// <summary>Target touchdown IAS = VAPP − this offset (kt). Default 5.</summary>
    public double VappToTouchdownOffsetKts { get; set; } = 5;

    /// <summary>When estimating VAPP from DESIGN SPEED VS0: VAPP ≈ Vs0 × factor.</summary>
    public double Vs0ToVappFactor { get; set; } = 1.3;

    /// <summary>
    /// If gear is required and up at touchdown, final score is multiplied by this (default 0.1 = 90% cut).
    /// </summary>
    public double GearUpScoreMultiplier { get; set; } = 0.1;

    public List<CriterionConfig> Criteria { get; set; } = new();

    /// <summary>Effective settle threshold in knots.</summary>
    public double GetSettledGroundSpeedKts()
    {
        if (SettledGroundSpeedKts > 0) return SettledGroundSpeedKts;
        if (SettledGroundSpeedKmh > 0) return SettledGroundSpeedKmh / 1.852;
        return 50;
    }
}

public sealed class CriterionConfig
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double Weight { get; set; } = 1.0;
    public string Metric { get; set; } = "";
    public string Evaluator { get; set; } = "range";
    public string SampleAt { get; set; } = "touchdown";
    public string? Unit { get; set; }
    public string? Note { get; set; }
    public Dictionary<string, double> Params { get; set; } = new();

    /// <summary>
    /// Optional control points for the <c>piecewise</c> evaluator: linear interpolation of score by metric value.
    /// </summary>
    public List<ScorePoint>? Points { get; set; }

    public bool FailIfOutside { get; set; }
}

/// <summary>Control point: metric value → metric score percent.</summary>
public sealed class ScorePoint
{
    /// <summary>Metric value (e.g. vertical speed fpm, IAS error kts).</summary>
    public double V { get; set; }

    /// <summary>
    /// Metric score at this value, preferably 0–100 (percent).
    /// Example: <c>{ "v": -100, "s": 100 }</c> = perfect. Engine converts to 0–1 internally.
    /// Legacy 0–1 fractions still work if every point has s ≤ 1.
    /// </summary>
    public double S { get; set; }
}
