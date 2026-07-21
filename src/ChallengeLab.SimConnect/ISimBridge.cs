using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Snapshots;

namespace ChallengeLab.SimConnect;

public enum SimConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    SimNotRunning,
    Error
}

public interface ISimBridge : IDisposable
{
    SimConnectionState State { get; }
    string? StatusMessage { get; }
    bool IsConnected { get; }

    event EventHandler<SimConnectionState>? StateChanged;
    event EventHandler<TelemetrySample>? TelemetryReceived;
    event EventHandler<string>? LogMessage;

    void Connect(IntPtr windowHandle);
    void Disconnect();
    void ReceiveMessage();

    /// <summary>
    /// Enable high-rate contact-point probes only while a controlled Challenge/Career
    /// attempt uses the nose-gear impact gate. Implementations without this optional
    /// telemetry may leave the default no-op behavior.
    /// </summary>
    void SetNoseGearImpactTelemetryEnabled(bool enabled) { }

    /// <summary>Return the simulator's worldwide airport catalog for the current connection.</summary>
    Task<IReadOnlyList<AirportFacility>> GetAirportsAsync(CancellationToken ct = default);

    /// <summary>Return physical runways and runway start positions for one airport.</summary>
    Task<AirportRunwayFacility> GetAirportRunwaysAsync(
        AirportFacility airport,
        CancellationToken ct = default);

    /// <summary>
    /// Safe mid-session scenario apply (time/weather/teleport/gear). No FlightLoad.
    /// Returns spawn verification result — caller must not arm scoring on failure.
    /// </summary>
    Task<SpawnApplyResult> LoadScenarioAsync(ChallengeConfig challenge, string flightFileAbsolutePath, IProgress<string>? progress = null, CancellationToken ct = default);
    void ConfigureAircraft(AircraftSetupConfig setup);
    void ApplyWeather(WeatherConfig weather);
    void ApplyTimeOfDay(TimeOfDayConfig? timeOfDay);
    void Teleport(SpawnConfig spawn);

    /// <summary>
    /// Resume after challenge hold: SET PAUSE OFF (+ clear active pause if stuck).
    /// HUD "Go" — pilot does not need ESC → Resume.
    /// </summary>
    void ResumeFlight();

    /// <summary>
    /// STORE tab: one-shot full flight-state capture (pose, exact velocities, gear/flaps/
    /// spoilers, trim, fuel, engines, lights, zulu time, ambient weather). Null when the
    /// sim is not delivering data (menu / load screen / disconnected).
    /// </summary>
    Task<FlightStateSnapshot?> CaptureSnapshotAsync(CancellationToken ct = default);

    /// <summary>
    /// STORE tab: restore a snapshot via the safe-apply pipeline (no FlightLoad). Entry is
    /// normalized so flying / ground / ESC pause / active pause all behave identically.
    /// Throws AircraftMismatchException when the live aircraft differs from the snapshot.
    /// </summary>
    Task<SpawnApplyResult> RestoreSnapshotAsync(
        FlightStateSnapshot snapshot,
        SnapshotRestoreOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default);

    /// <summary>
    /// ACTIONS tab: operate the iniBuilds A320neo V2 MCDU RAD NAV page. Frequency is
    /// always entered; course is optional so the manual control can change only frequency.
    /// </summary>
    Task SetMcduIlsAsync(
        decimal frequencyMhz,
        int? courseDegrees = null,
        IProgress<string>? progress = null,
        CancellationToken ct = default) =>
        Task.FromException(new NotSupportedException("MCDU ILS control is not supported by this simulator bridge."));
}
