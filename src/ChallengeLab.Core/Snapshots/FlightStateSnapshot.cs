using System.Text.Json.Serialization;

namespace ChallengeLab.Core.Snapshots;

/// <summary>
/// Full "store flight state" snapshot (STORE tab). Captured in one SimConnect read and
/// restored through the safe-apply pipeline (pause + freeze → teleport → velocity →
/// config settle → verify). Never restored via FlightLoad (MSFS 2024 CTD risk).
/// Groups are nullable so older files keep loading as the format grows.
/// </summary>
public sealed class FlightStateSnapshot
{
    public const string FormatId = "challengelab.flightsnapshot/v1";

    // --- Identity / gating (never written to the sim) ---
    public string Format { get; set; } = FormatId;
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset CreatedUtc { get; set; }
    /// <summary>User-editable display name (also embedded in the filename).</summary>
    public string Name { get; set; } = "";
    /// <summary>Live TITLE at capture. Restore refuses to run in a different aircraft.</summary>
    public string AircraftTitle { get; set; } = "";
    public SnapshotPauseContext PauseContext { get; set; } = SnapshotPauseContext.Unknown;
    public string AppBuildTag { get; set; } = "";
    public SnapshotAirportInfo? Airport { get; set; }

    // --- INITPOSITION group (teleport target) ---
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    /// <summary>Feet MSL.</summary>
    public double AltitudeFeet { get; set; }
    public double PitchDeg { get; set; }
    public double BankDeg { get; set; }
    public double HeadingTrueDeg { get; set; }
    /// <summary>INITPOSITION OnGround: ground snapshots re-seat the aircraft on its wheels.</summary>
    public bool OnGround { get; set; }
    public double IasKts { get; set; }

    // --- Velocity group (exact body-axis restore) ---
    public double BodyVelXMs { get; set; }
    public double BodyVelYMs { get; set; }
    public double BodyVelZMs { get; set; }
    public double RotVelXRadS { get; set; }
    public double RotVelYRadS { get; set; }
    public double RotVelZRadS { get; set; }
    public double TasKts { get; set; }
    public double GroundSpeedKts { get; set; }
    public double VerticalSpeedFpm { get; set; }
    public double AglFeet { get; set; }

    // --- Event-restored config group ---
    public bool GearHandleDown { get; set; }
    public bool IsGearRetractable { get; set; }
    /// <summary>0–1 physical gear extension at capture (settle diagnostic on restore).</summary>
    public double GearTotalPctExtended { get; set; }
    public int FlapsHandleIndex { get; set; }
    /// <summary>FLAPS NUM HANDLE POSITIONS at capture — scales FLAPS_SET correctly per airframe.</summary>
    public int FlapsHandleCount { get; set; }
    public double SpoilersHandle01 { get; set; }
    public double SpoilersLeft01 { get; set; }
    public double SpoilersRight01 { get; set; }
    public bool ParkingBrakeOn { get; set; }
    /// <summary>Rate at capture (informational — restore always normalizes to 1x).</summary>
    public double SimulationRate { get; set; } = 1.0;

    // --- SetData-restored groups ---
    public SnapshotTrim? Trim { get; set; }
    public SnapshotFuel? Fuel { get; set; }
    public SnapshotEngines? Engines { get; set; }
    public SnapshotLights? Lights { get; set; }
    public SnapshotPayload? Payload { get; set; }

    // --- Event-restored autopilot group (targets set first, then modes engaged) ---
    public SnapshotAutopilot? Autopilot { get; set; }

    // --- Time group (restored via ZULU_*_SET after teleport; zulu is timezone-independent) ---
    public SnapshotTime? Time { get; set; }

    // --- Weather group (best effort; restored as reconstructed METAR) ---
    public SnapshotWeather? Weather { get; set; }

    // --- Informational only (never restored) ---
    public SnapshotInfo? Info { get; set; }

    [JsonIgnore]
    public bool IsAirborne => !OnGround;
}

/// <summary>How the sim was paused at capture (informational; restore entry is normalized anyway).</summary>
public enum SnapshotPauseContext
{
    Unknown = 0,
    Flying = 1,
    NormalPause = 2,
    ActivePause = 3
}

/// <summary>Nearest airport resolved at capture (null when unresolvable).</summary>
public sealed class SnapshotAirportInfo
{
    public string Icao { get; set; } = "";
    public string? Name { get; set; }
    public string? Municipality { get; set; }
    /// <summary>ISO 3166 two-letter code (OurAirports iso_country).</summary>
    public string? CountryCode { get; set; }
    public double? DistanceNm { get; set; }

    /// <summary>"LSZH Zurich (CH)" style label; falls back to just the ident.</summary>
    [JsonIgnore]
    public string Label
    {
        get
        {
            var place = !string.IsNullOrWhiteSpace(Municipality) ? Municipality : Name;
            var country = string.IsNullOrWhiteSpace(CountryCode) ? "" : $" ({CountryCode})";
            return string.IsNullOrWhiteSpace(place) ? $"{Icao}{country}" : $"{Icao} {place}{country}";
        }
    }
}

public sealed class SnapshotTrim
{
    public double ElevatorTrimRad { get; set; }
    public double AileronTrimPct01 { get; set; }
    public double RudderTrimPct01 { get; set; }
}

public sealed class SnapshotFuel
{
    public double TotalGallons { get; set; }
    public double TotalCapacityGallons { get; set; }
    /// <summary>Per-tank gallons keyed by simvar tank name (e.g. "CENTER", "LEFT MAIN").</summary>
    public Dictionary<string, double> Tanks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class SnapshotEngines
{
    public int Count { get; set; }
    /// <summary>ENGINE TYPE enum value (0 piston, 1 jet, 5 turboprop, …).</summary>
    public int EngineType { get; set; }
    public bool[] Combustion { get; set; } = new bool[4];
    /// <summary>Percent −100..100 (negative = reverse where supported).</summary>
    public double[] ThrottleLeverPct { get; set; } = new double[4];
    public double[] MixtureLeverPct { get; set; } = new double[4];
    public double[] PropellerLeverPct { get; set; } = new double[4];
}

public sealed class SnapshotLights
{
    public bool Beacon { get; set; }
    public bool Landing { get; set; }
    public bool Taxi { get; set; }
    public bool Nav { get; set; }
    public bool Strobe { get; set; }
    public bool Panel { get; set; }
    public bool Recognition { get; set; }
    public bool Wing { get; set; }
    public bool Logo { get; set; }
    public bool Cabin { get; set; }
}

/// <summary>
/// Standard-interface autopilot state. Restores fully on stock aircraft; complex addons
/// (custom FMGC/FBW logic in L:vars) honor targets and open modes best-effort only, and
/// NAV lock needs a flight plan, which snapshots do not carry.
/// </summary>
public sealed class SnapshotAutopilot
{
    public bool Master { get; set; }
    public bool FlightDirector { get; set; }
    public bool AutothrottleArmed { get; set; }
    public bool ManagedThrottleActive { get; set; }
    public bool YawDamper { get; set; }

    public bool HeadingLock { get; set; }
    /// <summary>Heading bug, degrees magnetic.</summary>
    public double HeadingBugDeg { get; set; }
    public bool Nav1Lock { get; set; }
    public bool ApproachHold { get; set; }
    public bool GlideslopeHold { get; set; }

    public bool AltitudeLock { get; set; }
    public double AltitudeTargetFeet { get; set; }
    public bool VerticalSpeedHold { get; set; }
    public double VerticalSpeedTargetFpm { get; set; }
    public bool FlightLevelChange { get; set; }

    public bool AirspeedHold { get; set; }
    public double AirspeedTargetKts { get; set; }
    public bool MachHold { get; set; }
    public double MachTarget { get; set; }
}

public sealed class SnapshotPayload
{
    public int StationCount { get; set; }
    public double[] StationWeightsLbs { get; set; } = Array.Empty<double>();
}

public sealed class SnapshotTime
{
    /// <summary>Seconds since zulu midnight at capture.</summary>
    public double ZuluTimeSeconds { get; set; }
    public int ZuluDayOfYear { get; set; }
    public int ZuluYear { get; set; }
    public double LocalTimeSeconds { get; set; }
    public double TimeZoneOffsetSeconds { get; set; }

    [JsonIgnore]
    public int ZuluHour => (int)Math.Clamp(ZuluTimeSeconds / 3600.0, 0, 23);

    [JsonIgnore]
    public int ZuluMinute => (int)Math.Clamp(ZuluTimeSeconds % 3600.0 / 60.0, 0, 59);
}

public sealed class SnapshotWeather
{
    public double WindDirDeg { get; set; }
    public double WindKts { get; set; }
    public double SeaLevelPressureMb { get; set; }
    public double AmbientTempC { get; set; }
    public double VisibilityM { get; set; }
    public double PrecipState { get; set; }
    /// <summary>Built at capture in the GLOB observation format ApplyWeather understands.</summary>
    public string? ReconstructedMetar { get; set; }
}

/// <summary>Captured for display/diagnostics only; restore never writes these.</summary>
public sealed class SnapshotInfo
{
    public double[] N1Pct { get; set; } = new double[4];
    public double[] N2Pct { get; set; } = new double[4];
    public double CameraState { get; set; }
    public double TotalWeightLbs { get; set; }
}

/// <summary>Per-load options (bound to STORE tab checkboxes).</summary>
public sealed class SnapshotRestoreOptions
{
    public bool RestoreWeather { get; set; } = true;
    public bool RestoreTime { get; set; } = true;
    public bool RestoreFuel { get; set; } = true;
    public bool RestoreEngines { get; set; } = true;
    public bool RestoreLights { get; set; } = true;
    public bool RestoreAutopilot { get; set; } = true;
    /// <summary>Default false: end SET-paused, pilot resumes when ready.</summary>
    public bool AutoResume { get; set; }
}

/// <summary>Row model for the STORE tab list.</summary>
public sealed class SnapshotListItem
{
    public Guid Id { get; init; }
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public DateTimeOffset CreatedUtc { get; init; }
    public string AircraftTitle { get; init; } = "";
    public string AirportLabel { get; init; } = "";
    public bool IsAirborne { get; init; }
    public string DisplayName { get; init; } = "";

    public static SnapshotListItem From(FlightStateSnapshot snapshot, string path)
    {
        var situation = snapshot.IsAirborne
            ? $"AIR {snapshot.AltitudeFeet:0} ft"
            : "GND";
        var aircraft = string.IsNullOrWhiteSpace(snapshot.AircraftTitle)
            ? "?"
            : snapshot.AircraftTitle;
        return new SnapshotListItem
        {
            Id = snapshot.Id,
            Path = path,
            Name = snapshot.Name,
            CreatedUtc = snapshot.CreatedUtc,
            AircraftTitle = snapshot.AircraftTitle,
            AirportLabel = snapshot.Airport?.Label ?? "",
            IsAirborne = snapshot.IsAirborne,
            DisplayName =
                $"{snapshot.Name}  ·  {snapshot.CreatedUtc:yyyy-MM-dd HH:mm}Z  ·  {aircraft}  ·  {situation}"
        };
    }
}
