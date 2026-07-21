using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.FlightLoading;

public enum FlightLoadOutcome
{
    Succeeded,
    PartialSuccess,
    Blocked,
    TimedOut,
    Failed
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
    public double? AirspeedKts { get; init; }
    public bool? OnGround { get; init; }
    public bool UseWeatherFile { get; init; }
    public bool UseLiveWeather { get; init; }
    public string? WeatherPresetFile { get; init; }
    public string? WeatherPresetAbsolutePath { get; init; }
    public bool WeatherPresetExists { get; init; }

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
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(180);
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
    public bool NormalPauseActive { get; init; }
    public bool ActivePauseActive { get; init; }
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
        NormalPauseActive = sample.NormalPauseActive,
        ActivePauseActive = sample.ActivePauseActive,
        WindDirectionDeg = sample.WindDirectionDeg,
        WindVelocityKts = sample.WindVelocityKts
    };
}

public sealed record FlightLoadStartState
{
    public FlightLoadSimulatorMode SimulatorMode { get; init; }
    public string? AircraftTitle { get; init; }
    public FlightLoadObservation? Observation { get; init; }
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
    public string? PresetFile { get; init; }
    public string? PresetAbsolutePath { get; init; }
    public bool PresetExists { get; init; }
    public double? InitialWindDirectionDeg { get; init; }
    public double? InitialWindVelocityKts { get; init; }
    public double? FinalWindDirectionDeg { get; init; }
    public double? FinalWindVelocityKts { get; init; }
    public string ManualVerification { get; init; } =
        "Clouds, gusts, precipitation and turbulence require visual confirmation in MSFS.";
}

public sealed record FlightLoadResult
{
    public string Format { get; init; } = "challengelab.flightloadtest/v1";
    public Guid AttemptId { get; init; } = Guid.NewGuid();
    public DateTimeOffset RequestedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset CompletedUtc { get; init; } = DateTimeOffset.UtcNow;
    public FlightLoadOutcome Outcome { get; init; }
    public required string FlightFilePath { get; init; }
    public string? FlightFileSha256 { get; init; }
    public FltFileMetadata? Target { get; init; }
    public FlightLoadStartState? StartState { get; init; }
    public FlightLoadObservation? FinalObservation { get; init; }
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
    public string? ReportPath { get; init; }
}

public sealed record FlightLoadSafetyDecision(bool Allowed, string Reason);

public sealed record FlightLoadSampleValidation
{
    public bool IsMatch { get; init; }
    public double? HorizontalErrorM { get; init; }
    public double? AltitudeErrorFeet { get; init; }
    public double? AirspeedErrorKts { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
}
