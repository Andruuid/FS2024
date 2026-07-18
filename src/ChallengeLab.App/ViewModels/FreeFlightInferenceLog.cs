using System.IO;
using System.Text.Json;

namespace ChallengeLab.App.ViewModels;

/// <summary>Persistent JSON-lines diagnostics for Free Flight target acquisition and release.</summary>
internal sealed class FreeFlightInferenceLog
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _sync = new();

    public FreeFlightInferenceLog(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "free-flight-inference.jsonl");
    }

    public string Path { get; }

    public void Append(FreeFlightInferenceLogEntry entry)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            lock (_sync)
                File.AppendAllText(Path, line);
        }
        catch
        {
            // Diagnostics must never affect HUD targeting or scoring.
        }
    }
}

internal sealed record FreeFlightInferenceLogEntry(
    DateTimeOffset Timestamp,
    string Event,
    string Reason,
    string? CandidateKey,
    string? LockedKey,
    double? ThresholdDistanceNm,
    double? HeadingErrorDeg,
    double? TrackErrorDeg,
    double? CrossTrackNm,
    double Latitude,
    double Longitude,
    double AltitudeFeet,
    double AirspeedKts,
    double GroundSpeedKts,
    double GearHandlePosition,
    bool IsConnected,
    string? AircraftTitle);
