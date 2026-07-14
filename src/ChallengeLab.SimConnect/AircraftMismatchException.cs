namespace ChallengeLab.SimConnect;

/// <summary>
/// Current sim aircraft is not one of the challenge titles.
/// Safe path: do not FlightLoad / force a mid-session aircraft swap (CTD risk).
/// </summary>
public sealed class AircraftMismatchException : Exception
{
    public string ActualTitle { get; }
    public IReadOnlyList<string> ExpectedTitles { get; }

    public AircraftMismatchException(string actualTitle, IReadOnlyList<string> expectedTitles)
        : base(BuildMessage(actualTitle, expectedTitles))
    {
        ActualTitle = actualTitle;
        ExpectedTitles = expectedTitles;
    }

    private static string BuildMessage(string actual, IReadOnlyList<string> expected)
    {
        var list = string.Join(", ", expected);
        return
            "Challenge Lab will not force an aircraft change mid-flight " +
            "(that path can crash MSFS 2024).\n\n" +
            $"Current aircraft: {actual}\n" +
            $"Challenge needs: {list}\n\n" +
            "You do NOT need to restart Flight Simulator.\n\n" +
            "Do this:\n" +
            "1. World Map → select A330-200 (RR) (or one of the titles above)\n" +
            "2. Start free flight (any airport / time is fine)\n" +
            "3. Challenge Lab → Connect → Start Challenge\n\n" +
            "Then we set spawn position, noon local time, weather, and gear safely.";
    }
}
