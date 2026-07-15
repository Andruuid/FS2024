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
    public string EvaluationKey { get; set; } = "";

    /// <summary>Aircraft-generic evaluation key used by automatic Free HUD mode.</summary>
    public string FreeFlightEvaluationKey { get; set; } = "";
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

    /// <summary>
    /// Optional livery name for [Sim.0] Livery= (from a working CustomFlight/autosave).
    /// Example: "200 AIRBUS RR" for the stock A330-200 (RR).
    /// </summary>
    public string AircraftLivery { get; set; } = "";

    /// <summary>
    /// Optional hand-crafted .FLT override. Leave empty (recommended) so the app
    /// generates a minimal .FLT from this JSON (spawn, aircraft, gear/flaps) at start.
    /// </summary>
    public string FlightFile { get; set; } = "";

    public SpawnConfig Spawn { get; set; } = new();
    public WeatherConfig Weather { get; set; } = new();
    public AircraftSetupConfig AircraftSetup { get; set; } = new();

    /// <summary>
    /// Fixed scenario clock applied on Start Challenge (FLT DateTimeSeason + SimConnect).
    /// Defaults to 12:00 local so restarts are always daytime at the airport.
    /// </summary>
    public TimeOfDayConfig TimeOfDay { get; set; } = new();

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

/// <summary>
/// Scenario time of day. Independent of METAR text (observation only).
/// When <see cref="UseZuluTime"/> is false, hour/minute are local at the spawn.
/// </summary>
public sealed class TimeOfDayConfig
{
    /// <summary>Hour 0–23. Default 12 (noon).</summary>
    public int Hour { get; set; } = 12;

    /// <summary>Minute 0–59. Default 0.</summary>
    public int Minute { get; set; }

    /// <summary>
    /// When false (default), Hours/Minutes are local time at the airport.
    /// When true, they are UTC (Zulu).
    /// </summary>
    public bool UseZuluTime { get; set; }

    /// <summary>Calendar year for DateTimeSeason. Null = current year.</summary>
    public int? Year { get; set; }

    /// <summary>Day of year 1–365 for DateTimeSeason. Null = 180 (mid-year / summer).</summary>
    public int? DayOfYear { get; set; }
}

public sealed class AircraftSetupConfig
{
    /// <summary>Landing gear handle down when true.</summary>
    public bool GearDown { get; set; } = true;

    /// <summary>Flaps handle index (0 = clean / up for most airliners).</summary>
    public int FlapsHandleIndex { get; set; }

    /// <summary>When true, command spoilers fully retracted at challenge start.</summary>
    public bool SpoilersRetracted { get; set; } = true;

    /// <summary>When true, set parking brake on; when false, ensure it is released.</summary>
    public bool ParkingBrakeOn { get; set; }

    /// <summary>
    /// When true, auto-unpause after spawn/config. Default false — pilot unpauses
    /// when ready so the aircraft can settle in a paused, reproducible state.
    /// </summary>
    public bool Unpause { get; set; }

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

/// <summary>Control point: metric value → metric score percent.</summary>
public sealed class ScorePoint
{
    /// <summary>Metric value (e.g. vertical speed fpm, IAS error kts).</summary>
    public double V { get; set; }

    /// <summary>
    /// Metric score at this value, always 0–100 percent.
    /// Example: <c>{ "v": -100, "s": 100 }</c> = perfect. Engine converts to 0–1 internally.
    /// </summary>
    public double S { get; set; }
}
