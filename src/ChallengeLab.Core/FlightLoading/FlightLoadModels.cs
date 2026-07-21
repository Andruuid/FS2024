using System.Text.Json.Serialization;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.FlightLoading;

public enum FlightLoadOutcome
{
    Succeeded,
    PartialSuccess,
    LoadedAwaitingReady,
    Blocked,
    TimedOut,
    Cancelled,
    Failed
}

public enum FlightLoadPhase
{
    Preflight,
    ReleasingPause,
    RequestSent,
    LoadAccepted,
    AwaitingReady,
    ValidatingState,
    Operational,
    Completed
}

public enum FlightLoadPausePolicy
{
    AutoRelease,
    RequireUnpaused
}

public enum FlightLoadSimulatorMode
{
    Unknown,
    MainMenu,
    ActiveFlight
}

public enum FlightLoadWeatherStatus
{
    NotRequested,
    DependencyMissing,
    DependencyAvailable,
    Unverified
}

public enum FlightLoadTimeStatus
{
    NotSpecified,
    Specified,
    Invalid
}

public sealed record FltDateTimeMetadata
{
    public FlightLoadTimeStatus Status { get; init; }
    public string? Season { get; init; }
    public int? Year { get; init; }
    public int? Day { get; init; }
    public int? Hours { get; init; }
    public int? Minutes { get; init; }
    public double? Seconds { get; init; }
    public bool? UseZuluTime { get; init; }

    [JsonIgnore]
    public string Description => Status switch
    {
        FlightLoadTimeStatus.NotSpecified => "No DateTimeSeason section; current simulator clock is preserved.",
        FlightLoadTimeStatus.Invalid => "DateTimeSeason is present but incomplete or invalid.",
        _ => $"{Year:0000} day {Day:000} {Hours:00}:{Minutes:00}:{Seconds:00} " +
             (UseZuluTime == true ? "Zulu" : "local")
    };
}

public sealed record FltFileMetadata
{
    public required string FilePath { get; init; }
    public string? Title { get; init; }
    public string? AircraftTitle { get; init; }
    public string? Livery { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AltitudeFeet { get; init; }
    public double? HeadingDegrees { get; init; }
    public double? AirspeedFeetPerSecond { get; init; }
    public double? AirspeedKts { get; init; }
    public bool? OnGround { get; init; }
    public bool UseWeatherFile { get; init; }
    public bool UseLiveWeather { get; init; }
    public string? WeatherPresetFile { get; init; }
    public string? WeatherPresetAbsolutePath { get; init; }
    public bool WeatherPresetExists { get; init; }
    public FltDateTimeMetadata DateTime { get; init; } = new()
    {
        Status = FlightLoadTimeStatus.NotSpecified
    };

    public FlightLoadWeatherStatus WeatherStatus => UseLiveWeather
        ? FlightLoadWeatherStatus.Unverified
        : !UseWeatherFile
            ? FlightLoadWeatherStatus.NotRequested
            : WeatherPresetExists
                ? FlightLoadWeatherStatus.DependencyAvailable
                : FlightLoadWeatherStatus.DependencyMissing;
}

public sealed record FlightLoadRequest
{
    public required string FlightFilePath { get; init; }
    public FlightLoadPausePolicy PausePolicy { get; init; } = FlightLoadPausePolicy.AutoRelease;
    public TimeSpan PauseReleaseTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan LiveSettleDelay { get; init; } = TimeSpan.FromMilliseconds(750);
    public TimeSpan AcceptanceTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ReadyTimeout { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan ValidationTimeout { get; init; } = TimeSpan.FromSeconds(15);
    /// <summary>Legacy alias retained for callers of the v1 diagnostic contract.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.Zero;
    public int RequiredConsecutiveSamples { get; init; } = 3;
}

public sealed record FlightLoadObservation
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public string? AircraftTitle { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AltitudeFeet { get; init; }
    public double? HeadingTrueDeg { get; init; }
    public double? AirspeedKts { get; init; }
    public bool? OnGround { get; init; }
    public bool PauseStateAvailable { get; init; }
    public uint? PauseStateFlags { get; init; }
    public bool NormalPauseActive { get; init; }
    public bool ActivePauseActive { get; init; }
    public bool? SimDisabled { get; init; }
    public bool? UserInputEnabled { get; init; }
    public bool? MotionSimulationActive { get; init; }
    public double? WindDirectionDeg { get; init; }
    public double? WindVelocityKts { get; init; }

    public static FlightLoadObservation FromTelemetry(TelemetrySample sample) => new()
    {
        TimestampUtc = sample.Timestamp,
        AircraftTitle = sample.AircraftTitle,
        Latitude = sample.Latitude,
        Longitude = sample.Longitude,
        AltitudeFeet = sample.AltitudeFeet,
        HeadingTrueDeg = sample.HeadingTrueDeg,
        AirspeedKts = sample.AirspeedKts,
        OnGround = sample.SimOnGround,
        PauseStateAvailable = sample.PauseStateAvailable,
        PauseStateFlags = sample.PauseStateFlags,
        NormalPauseActive = sample.NormalPauseActive,
        ActivePauseActive = sample.ActivePauseActive,
        SimDisabled = sample.SimDisabled,
        UserInputEnabled = sample.UserInputEnabled,
        MotionSimulationActive = sample.MotionSimulationActive,
        WindDirectionDeg = sample.WindDirectionDeg,
        WindVelocityKts = sample.WindVelocityKts
    };
}

public sealed record FlightLoadStartState
{
    public FlightLoadSimulatorMode SimulatorMode { get; init; }
    public string? AircraftTitle { get; init; }
    public FlightLoadObservation? Observation { get; init; }
    public bool? SimRunning { get; init; }
    public bool? DialogMode { get; init; }
}

public sealed record FlightLoadPauseNormalization
{
    public FlightLoadPausePolicy Policy { get; init; } = FlightLoadPausePolicy.AutoRelease;
    public uint? InitialFlags { get; init; }
    public bool? InitialDialogMode { get; init; }
    public bool PauseWasDetected { get; init; }
    public bool ActivePauseReleaseSent { get; init; }
    public bool NormalPauseReleaseSent { get; init; }
    public bool VerifiedUnpaused { get; init; }
    public int ConsecutiveUnpausedSamples { get; init; }
    public uint? FinalFlags { get; init; }
    public bool? FinalDialogMode { get; init; }
    public bool OriginalPauseRestored { get; init; }
    public string Message { get; init; } = "";
}

public sealed record FlightLoadOperationalReadiness
{
    public FlightLoadPhase FinalPhase { get; init; } = FlightLoadPhase.Preflight;
    public DateTimeOffset? LoadAcceptedUtc { get; init; }
    public DateTimeOffset? FlightStartedUtc { get; init; }
    public DateTimeOffset? OperationalUtc { get; init; }
    public bool FlightStartEventReceived { get; init; }
    public bool SimStartEventReceived { get; init; }
    public bool? SimRunning { get; init; }
    public bool? DialogMode { get; init; }
    public bool? SimDisabled { get; init; }
    public bool? UserInputEnabled { get; init; }
    public bool? MotionSimulationActive { get; init; }
    public int ConsecutiveOperationalSamples { get; init; }
    public bool UsedStableProbeFallback { get; init; }
    public string VisualVerification { get; init; } =
        "Confirm the flight displays, toolbar, ESC menu and flight controls visually in MSFS.";
}

public sealed record FlightLoadTimelineEntry
{
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public required string Stage { get; init; }
    public required string Message { get; init; }
}

public sealed record FlightLoadWeatherAssessment
{
    public FlightLoadWeatherStatus Status { get; init; }
    public bool RequestedFromFile { get; init; }
    public bool UseLiveWeather { get; init; }
    public string? PresetFile { get; init; }
    public string? PresetAbsolutePath { get; init; }
    public bool PresetExists { get; init; }
    public double? InitialWindDirectionDeg { get; init; }
    public double? InitialWindVelocityKts { get; init; }
    public double? FinalWindDirectionDeg { get; init; }
    public double? FinalWindVelocityKts { get; init; }
    public bool? ObservedWindChanged { get; init; }
    public string ManualVerification { get; init; } =
        "Clouds, gusts, precipitation and turbulence require visual confirmation in MSFS.";
}

public sealed record FlightLoadResult
{
    public string Format { get; init; } = "challengelab.flightloadtest/v2";
    public Guid AttemptId { get; init; } = Guid.NewGuid();
    public DateTimeOffset RequestedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedUtc { get; init; } = DateTimeOffset.UtcNow;
    public FlightLoadOutcome Outcome { get; init; }
    public FlightLoadPhase Phase { get; init; } = FlightLoadPhase.Completed;
    public required string FlightFilePath { get; init; }
    public string? FlightFileSha256 { get; init; }
    public FltFileMetadata? Target { get; init; }
    public FlightLoadStartState? StartState { get; init; }
    public FlightLoadObservation? FinalObservation { get; init; }
    public bool LoadIssued { get; init; }
    public bool LoadAccepted { get; init; }
    public bool FlightLoadedEventReceived { get; init; }
    public string? LoadedFilename { get; init; }
    public string? ConfirmedFlightStatePath { get; init; }
    public bool DisconnectedDuringLoad { get; init; }
    public bool ReconnectedDuringLoad { get; init; }
    public int ConsecutiveValidSamples { get; init; }
    public double ElapsedSeconds { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<string> ValidationIssues { get; init; } = Array.Empty<string>();
    public IReadOnlyList<FlightLoadTimelineEntry> Timeline { get; init; } = Array.Empty<FlightLoadTimelineEntry>();
    public FlightLoadWeatherAssessment? Weather { get; init; }
    public FlightLoadPauseNormalization? PauseNormalization { get; init; }
    public FlightLoadOperationalReadiness? OperationalReadiness { get; init; }
    public string? ReportPath { get; init; }
}

public sealed record FlightLoadSafetyDecision(bool Allowed, string Reason);

public sealed record FlightLoadSampleValidation
{
    public bool IsMatch { get; init; }
    public double? HorizontalErrorM { get; init; }
    public double? AltitudeErrorFeet { get; init; }
    public double? AirspeedErrorKts { get; init; }
    public double? HeadingErrorDegrees { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
}
