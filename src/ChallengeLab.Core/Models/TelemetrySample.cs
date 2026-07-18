namespace ChallengeLab.Core.Models;

/// <summary>Normalized flight telemetry sample used by scoring (units documented per property).</summary>
public sealed class TelemetrySample
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Pause-aware seconds since the simulation started. Scoring windows use this monotonic clock;
    /// <see cref="Timestamp"/> remains for display and persistence.
    /// </summary>
    public double SimulationTimeSeconds { get; init; } = double.NaN;

    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double AltitudeFeet { get; init; }
    public double AglFeet { get; init; }

    public double HeadingTrueDeg { get; init; }

    public double PitchDeg { get; init; }
    public double BankDeg { get; init; }

    /// <summary>Indicated airspeed (knots).</summary>
    public double AirspeedKts { get; init; }

    /// <summary>Ground speed (knots).</summary>
    public double GroundSpeedKts { get; init; }

    /// <summary>Vertical speed (feet per minute). Negative = descending.</summary>
    public double VerticalSpeedFpm { get; init; }

    public double GForce { get; init; }
    public bool GForceAvailable { get; init; } = true;
    public bool SimOnGround { get; init; }

    /// <summary>Last touchdown velocity along the ground normal, in feet/second.</summary>
    public double? TouchdownNormalVelocityFps { get; init; }

    /// <summary>Indexed GEAR IS ON GROUND values (0-15). Null means not sampled.</summary>
    public IReadOnlyDictionary<int, bool>? GearOnGroundByIndex { get; init; }

    /// <summary>
    /// Bounded raw contact-point probes used to correlate suspension compression with
    /// logical nose-gear contact. Landing traces intentionally omit these collections.
    /// </summary>
    public IReadOnlyDictionary<int, bool>? ContactPointOnGroundByIndex { get; init; }
    public IReadOnlyDictionary<int, double>? ContactPointCompressionByIndex { get; init; }
    public bool ContactPointTelemetryAvailable { get; init; }

    public double GearHandlePosition { get; init; }
    public bool IsGearRetractable { get; init; }
    public bool IsGearWheels { get; init; }
    public bool IsGearFloats { get; init; }
    public bool IsTailDragger { get; init; }
    public int FlapsHandleIndex { get; init; }

    /// <summary>Aircraft capability values sampled by MSFS and frozen when Free Flight arms.</summary>
    public int? FlapsHandlePositionCount { get; init; }
    public bool? SpoilersAvailable { get; init; }
    public bool? AutopilotAvailable { get; init; }
    public double? ThrottleLowerLimitPercent { get; init; }

    /// <summary>
    /// Spoilers lever / handle (prefer 0–1 percent-over-100). May be non-zero when
    /// surfaces are still stowed (e.g. Airbus ground-spoiler arm). Prefer
    /// <see cref="SpoilersSurfacePosition"/> for “are they actually out?”.
    /// </summary>
    public double SpoilersHandlePosition { get; init; }

    /// <summary>
    /// Max of left/right spoiler surface deflection (0–1). Null if not sampled.
    /// This is the ground truth for retracted vs deployed.
    /// </summary>
    public double? SpoilersSurfacePosition { get; init; }

    /// <summary>Individual spoiler surface deflection (0-1). Null when not sampled.</summary>
    public double? SpoilersLeftPosition { get; init; }
    public double? SpoilersRightPosition { get; init; }

    /// <summary>Manual toe-brake pedal input (0-1), excluding autobrake where supported.</summary>
    public double? ManualBrakeLeftPosition { get; init; }
    public double? ManualBrakeRightPosition { get; init; }

    /// <summary>
    /// True while autobrakes are applying brake pressure. Combines stock
    /// <c>AUTOBRAKES ACTIVE</c> with airliner L-vars (FBW <c>A32NX_AUTOBRAKES_ACTIVE</c>,
    /// iniBuilds <c>INI_AUTOBRAKE_BRAKING</c> / <c>INI_AUTOBRAKE_ENGAGED</c>). Null when not sampled.
    /// </summary>
    public bool? AutoBrakesActive { get; init; }

    /// <summary>Installed engine count (1-4). Null when engine telemetry was not sampled.</summary>
    public int? EngineCount { get; init; }

    /// <summary>Per-engine combustion state used to identify engines operating at touchdown.</summary>
    public IReadOnlyDictionary<int, bool>? EngineCombustionByIndex { get; init; }

    /// <summary>Per-engine reverse-thruster selected state.</summary>
    public IReadOnlyDictionary<int, bool>? ReverseThrustEngagedByIndex { get; init; }

    /// <summary>Per-engine physical reverse-nozzle deployment normalized to 0-1.</summary>
    public IReadOnlyDictionary<int, double>? ReverseNozzlePositionByIndex { get; init; }

    /// <summary>Per-engine throttle-lever position in percent; negative values are powered reverse.</summary>
    public IReadOnlyDictionary<int, double>? ThrottleLeverPositionPercentByIndex { get; init; }

    public double WindDirectionDeg { get; init; }
    public double WindVelocityKts { get; init; }

    public double RadioHeightFeet { get; init; }

    /// <summary>True when RADIO HEIGHT was present in the telemetry definition.</summary>
    public bool RadioHeightAvailable { get; init; }

    public bool? AutopilotHeadingHoldActive { get; init; }
    public bool? AutopilotAltitudeHoldActive { get; init; }
    public bool? AutopilotMasterActive { get; init; }
    public bool? AutopilotChannel1Active { get; init; }
    public bool? AutopilotChannel2Active { get; init; }
    public bool? AutothrustActive { get; init; }
    public bool? AutothrustArmed { get; init; }

    /// <summary>Internal simulation-time rate; 1 is normal real-time speed.</summary>
    public double? SimulationRate { get; init; }

    /// <summary>
    /// MSFS <c>CAMERA STATE</c> enum when sampled. Cockpit is 2; exterior/chase/drone and
    /// menus use other values. Null when the SimVar was not present in the sample.
    /// </summary>
    public int? CameraState { get; init; }

    /// <summary>Pause_EX1 state copied into every telemetry sample.</summary>
    public bool PauseStateAvailable { get; init; }
    public bool NormalPauseActive { get; init; }
    public bool ActivePauseActive { get; init; }

    /// <summary>
    /// Increments on every transition into a Pause_EX1 paused state. This detects a pause
    /// even when visual-frame telemetry stopped until after the simulator was resumed.
    /// </summary>
    public long PauseGeneration { get; init; }

    /// <summary>
    /// Live SimConnect TITLE string for the user aircraft (e.g. "Airbus A320 neo").
    /// Used by Free Flight to look up typical approach speed from the aircraft VAPP DB.
    /// </summary>
    public string? AircraftTitle { get; init; }

    /// <summary>Stall speed landing config (DESIGN SPEED VS0), if available.</summary>
    public double DesignSpeedVs0Kts { get; init; }

    /// <summary>True while the simulator's STALL WARNING SimVar is active.</summary>
    public bool StallWarningActive { get; init; }
    public bool StallWarningAvailable { get; init; } = true;

    /// <summary>True while the simulator's OVERSPEED WARNING SimVar is active.</summary>
    public bool OverspeedWarningActive { get; init; }

    /// <summary>
    /// True when OVERSPEED WARNING was captured for this sample. Defaults false so
    /// pre-v30 flight tapes are not silently treated as warning-free when replayed.
    /// </summary>
    public bool OverspeedWarningAvailable { get; init; }

    /// <summary>Total weight (lbs), if available.</summary>
    public double? TotalWeightLbs { get; init; }

    /// <summary>Ground speed in km/h (derived convenience).</summary>
    public double GroundSpeedKmh => GroundSpeedKts * 1.852;
}
