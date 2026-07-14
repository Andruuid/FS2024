using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

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
    /// Safe mid-session scenario apply (time/weather/teleport/gear). No FlightLoad.
    /// Returns spawn verification result — caller must not arm scoring on failure.
    /// </summary>
    Task<SpawnApplyResult> LoadScenarioAsync(ChallengeConfig challenge, string flightFileAbsolutePath, IProgress<string>? progress = null, CancellationToken ct = default);
    void ConfigureAircraft(AircraftSetupConfig setup);
    void ApplyWeather(WeatherConfig weather);
    void ApplyTimeOfDay(TimeOfDayConfig? timeOfDay);
    void Teleport(SpawnConfig spawn);
}
