using System.Globalization;

namespace ChallengeLab.Core.Snapshots;

/// <summary>
/// Builds the default snapshot name: "{custom} LSZH Zurich (CH)" — custom prefix optional,
/// airport label when resolvable, otherwise compact coordinates. The capture timestamp is
/// carried by the filename stamp and the list row, not duplicated inside the name.
/// </summary>
public static class SnapshotNameBuilder
{
    public static string BuildDefaultName(
        string? customName,
        SnapshotAirportInfo? airport,
        double latitude,
        double longitude)
    {
        var custom = (customName ?? "").Trim();
        var location = airport is not null && !string.IsNullOrWhiteSpace(airport.Icao)
            ? airport.Label
            : FormatCoordinates(latitude, longitude);

        if (custom.Length == 0) return location;
        return $"{custom} {location}";
    }

    public static string FormatCoordinates(double latitude, double longitude)
    {
        var latHemisphere = latitude >= 0 ? "N" : "S";
        var lonHemisphere = longitude >= 0 ? "E" : "W";
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Abs(latitude):0.00}{latHemisphere} {Math.Abs(longitude):0.00}{lonHemisphere}");
    }
}
