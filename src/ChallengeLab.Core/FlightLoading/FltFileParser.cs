using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ChallengeLab.Core.FlightLoading;

public static partial class FltFileParser
{
    public const double FeetPerSecondToKnots = 0.592483801295896;

    public static FltFileMetadata Parse(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A flight file path is required.", nameof(path));

        var absolutePath = Path.GetFullPath(path);
        if (!File.Exists(absolutePath))
            throw new FileNotFoundException("Flight file not found.", absolutePath);
        if (!string.Equals(Path.GetExtension(absolutePath), ".flt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The selected file is not an FLT file.");

        // Developer-mode exports use a legacy single-byte degree marker. Latin-1 preserves
        // every byte while leaving the INI keys/values (ASCII) unchanged.
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(absolutePath));
        var sections = ParseSections(text);
        var main = Section(sections, "Main");
        var sim = Section(sections, "Sim.0");
        var simVars = Section(sections, "SimVars.0");
        var weather = Section(sections, "Weather");
        var dateTime = sections.TryGetValue("DateTimeSeason", out var dateTimeSection)
            ? ParseDateTime(dateTimeSection)
            : new FltDateTimeMetadata { Status = FlightLoadTimeStatus.NotSpecified };

        var preset = Value(weather, "WeatherPresetFile");
        var presetAbsolute = ResolveRelativePath(absolutePath, preset);
        var airspeedFeetPerSecond = ParseDouble(Value(simVars, "ZVelBodyAxis_IAS"));

        return new FltFileMetadata
        {
            FilePath = absolutePath,
            Title = Value(main, "Title"),
            AircraftTitle = Value(sim, "Sim"),
            Livery = Value(sim, "Livery"),
            Latitude = ParseCoordinate(Value(simVars, "Latitude"), latitude: true),
            Longitude = ParseCoordinate(Value(simVars, "Longitude"), latitude: false),
            AltitudeFeet = ParseDouble(Value(simVars, "Altitude")),
            HeadingDegrees = NormalizeHeading(ParseDouble(Value(simVars, "Heading"))),
            AirspeedFeetPerSecond = airspeedFeetPerSecond,
            AirspeedKts = airspeedFeetPerSecond * FeetPerSecondToKnots,
            OnGround = ParseBool(Value(simVars, "SimOnGround")),
            UseWeatherFile = ParseBool(Value(weather, "UseWeatherFile")) ?? false,
            UseLiveWeather = ParseBool(Value(weather, "UseLiveWeather")) ?? false,
            WeatherPresetFile = preset,
            WeatherPresetAbsolutePath = presetAbsolute,
            WeatherPresetExists = presetAbsolute is not null && File.Exists(presetAbsolute),
            DateTime = dateTime
        };
    }

    private static FltDateTimeMetadata ParseDateTime(IReadOnlyDictionary<string, string> section)
    {
        var season = Value(section, "Season");
        var year = ParseInt(Value(section, "Year"));
        var day = ParseInt(Value(section, "Day"));
        var hours = ParseInt(Value(section, "Hours"));
        var minutes = ParseInt(Value(section, "Minutes"));
        var seconds = ParseDouble(Value(section, "Seconds"));
        var useZuluTime = ParseBool(Value(section, "UseZuluTime"));
        var valid = !string.IsNullOrWhiteSpace(season)
                    && year is > 0
                    && day is >= 1 and <= 366
                    && hours is >= 0 and <= 23
                    && minutes is >= 0 and <= 59
                    && seconds is >= 0 and < 60
                    && useZuluTime is not null;

        return new FltDateTimeMetadata
        {
            Status = valid ? FlightLoadTimeStatus.Specified : FlightLoadTimeStatus.Invalid,
            Season = season,
            Year = year,
            Day = day,
            Hours = hours,
            Minutes = minutes,
            Seconds = seconds,
            UseZuluTime = useZuluTime
        };
    }

    private static Dictionary<string, Dictionary<string, string>> ParseSections(string text)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;

        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[name] = current;
                continue;
            }

            if (current is null) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            current[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return result;
    }

    private static Dictionary<string, string> Section(
        IReadOnlyDictionary<string, Dictionary<string, string>> sections,
        string name) => sections.TryGetValue(name, out var section)
        ? section
        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private static string? Value(IReadOnlyDictionary<string, string> section, string key) =>
        section.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static double? ParseCoordinate(string? value, bool latitude)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = CoordinateRegex().Match(value);
        if (!match.Success) return null;

        var hemisphere = char.ToUpperInvariant(match.Groups[1].Value[0]);
        if (latitude && hemisphere is not ('N' or 'S')) return null;
        if (!latitude && hemisphere is not ('E' or 'W')) return null;
        if (!double.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var degrees)
            || !double.TryParse(match.Groups[3].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
            || !double.TryParse(match.Groups[4].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return null;

        var result = degrees + minutes / 60d + seconds / 3600d;
        return hemisphere is 'S' or 'W' ? -result : result;
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed)
            ? parsed
            : null;

    private static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static bool? ParseBool(string? value) =>
        bool.TryParse(value, out var parsed) ? parsed : value switch
        {
            "1" => true,
            "0" => false,
            _ => null
        };

    private static double? NormalizeHeading(double? heading)
    {
        if (heading is null) return null;
        var normalized = heading.Value % 360d;
        return normalized < 0 ? normalized + 360d : normalized;
    }

    private static string? ResolveRelativePath(string flightPath, string? referencedPath)
    {
        if (string.IsNullOrWhiteSpace(referencedPath)) return null;
        var normalized = referencedPath.Trim().Trim('"').Replace('/', Path.DirectorySeparatorChar);
        return Path.GetFullPath(Path.IsPathRooted(normalized)
            ? normalized
            : Path.Combine(Path.GetDirectoryName(flightPath)!, normalized));
    }

    [GeneratedRegex(@"^\s*([NSEW])\s*(\d+)\D+(\d+)\D+([0-9]+(?:\.[0-9]+)?)", RegexOptions.IgnoreCase)]
    private static partial Regex CoordinateRegex();
}
