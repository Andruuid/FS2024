namespace ChallengeLab.Core.FlightLoading;

public static class FlightLoadSafetyPolicy
{
    public static FlightLoadSafetyDecision Evaluate(
        FltFileMetadata target,
        FlightLoadSimulatorMode simulatorMode,
        string? currentAircraftTitle)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(target.AircraftTitle))
            return new(false, "The FLT does not identify its target aircraft.");

        if (simulatorMode == FlightLoadSimulatorMode.MainMenu)
            return new(true, "Main menu detected; no active aircraft will be replaced.");
        if (simulatorMode != FlightLoadSimulatorMode.ActiveFlight)
            return new(false, "The simulator state could not be established safely.");
        if (string.IsNullOrWhiteSpace(currentAircraftTitle))
            return new(false, "The current aircraft TITLE is unavailable; the load was not sent.");
        if (!AircraftTitlesMatch(currentAircraftTitle, target.AircraftTitle))
            return new(false,
                $"Current aircraft '{currentAircraftTitle}' does not match FLT aircraft '{target.AircraftTitle}'. " +
                "Cross-aircraft FlightLoad is blocked because it can crash MSFS 2024.");

        return new(true, $"Current aircraft matches '{target.AircraftTitle}'.");
    }

    public static bool AircraftTitlesMatch(string actual, string expected)
    {
        var normalizedActual = Normalize(actual);
        var normalizedExpected = Normalize(expected);
        return normalizedActual.Length > 0 && normalizedExpected.Length > 0
               && (normalizedActual.Equals(normalizedExpected, StringComparison.Ordinal)
                   || normalizedActual.Contains(normalizedExpected, StringComparison.Ordinal)
                   || normalizedExpected.Contains(normalizedActual, StringComparison.Ordinal));
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
