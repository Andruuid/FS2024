namespace ChallengeLab.SimConnect;

/// <summary>
/// Result of a mid-session safe spawn apply (teleport + velocity, no FlightLoad).
/// </summary>
public sealed class SpawnApplyResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public double? AltErrorFeet { get; init; }
    public double? HorizontalErrorM { get; init; }
    public bool ReportedOnGround { get; init; }
    public double? AirspeedKts { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public double? AltitudeFeet { get; init; }

    public static SpawnApplyResult Ok(string message, double? altErr, double? horizM, double? ias,
        double lat, double lon, double alt, bool onGround) => new()
    {
        Success = true,
        Message = message,
        AltErrorFeet = altErr,
        HorizontalErrorM = horizM,
        AirspeedKts = ias,
        Latitude = lat,
        Longitude = lon,
        AltitudeFeet = alt,
        ReportedOnGround = onGround
    };

    public static SpawnApplyResult Fail(string message, double? altErr = null, double? horizM = null,
        double? ias = null, bool onGround = false, double? lat = null, double? lon = null, double? alt = null) => new()
    {
        Success = false,
        Message = message,
        AltErrorFeet = altErr,
        HorizontalErrorM = horizM,
        AirspeedKts = ias,
        ReportedOnGround = onGround,
        Latitude = lat,
        Longitude = lon,
        AltitudeFeet = alt
    };
}
