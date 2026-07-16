namespace ChallengeLab.Core.Models;

/// <summary>
/// Versioned, self-contained runway-relative snapshot used to reproduce the landing visual
/// without simulator, trace, or current challenge configuration access.
/// </summary>
public sealed class LandingVisualizationData
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string AirportIcao { get; set; } = "";
    public string RunwayId { get; set; } = "";
    public double RunwayHeadingTrueDeg { get; set; }
    public double RunwayLengthM { get; set; }
    public double RunwayWidthM { get; set; }

    /// <summary>Positive values are past the landing threshold in the runway direction.</summary>
    public double TouchdownDistanceFromThresholdM { get; set; }

    /// <summary>The scoring profile's ideal touchdown point for this runway.</summary>
    public double IdealTouchdownDistanceFromThresholdM { get; set; }

    /// <summary>Positive is right of centerline while facing the landing direction.</summary>
    public double TouchdownLateralOffsetM { get; set; }

    public double TouchdownHeadingErrorDeg { get; set; }
    public double TouchdownBankDeg { get; set; }
    public double TouchdownPitchDeg { get; set; }
    public double TouchdownVerticalSpeedFpm { get; set; }
    public double TouchdownRawPeakG { get; set; }
    public double TouchdownRobustPeakG { get; set; }
    public double TouchdownAirspeedKts { get; set; }
    public double TargetTouchdownAirspeedKts { get; set; }
}
