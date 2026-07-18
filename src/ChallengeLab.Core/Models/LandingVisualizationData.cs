namespace ChallengeLab.Core.Models;

/// <summary>
/// Versioned, self-contained runway-relative snapshot used to reproduce the landing visual
/// without simulator, trace, or current challenge configuration access.
/// </summary>
public sealed class LandingVisualizationData
{
    public const int CurrentVersion = 4;

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

    /// <summary>Near boundary of the v3 ideal touchdown band.</summary>
    public double? IdealTouchdownNearDistanceFromThresholdM { get; set; }

    /// <summary>Far boundary of the v3 ideal touchdown band.</summary>
    public double? IdealTouchdownFarDistanceFromThresholdM { get; set; }

    /// <summary>Beginning of the approached runway's aiming-point blocks, when known.</summary>
    public double? AimingMarkerStartDistanceFromThresholdM { get; set; }

    /// <summary>Nominal longitudinal length of each aiming-point block, when known.</summary>
    public double? AimingMarkerNominalLengthM { get; set; }

    /// <summary>Nominal center of the aiming-point blocks, when known.</summary>
    public double? AimingMarkerCenterDistanceFromThresholdM { get; set; }

    public string AimingMarkerSource { get; set; } = "";
    public string AimingMarkerConfidence { get; set; } = "";

    /// <summary>Positive is right of centerline while facing the landing direction.</summary>
    public double TouchdownLateralOffsetM { get; set; }

    public double TouchdownHeadingErrorDeg { get; set; }
    public double? TouchdownGroundTrackTrueDeg { get; set; }
    public string TouchdownGroundTrackSource { get; set; } = "";
    public double TouchdownTrackErrorDeg { get; set; }
    public double TouchdownTrueCrabAngleDeg { get; set; }
    public double TouchdownBankDeg { get; set; }
    public double TouchdownPitchDeg { get; set; }
    public double TouchdownSinkRateFpm { get; set; }
    public double? TouchdownNormalVelocityFpm { get; set; }
    /// <summary>Legacy alias; v4 writers store the pilot-facing sink rate here too.</summary>
    public double TouchdownVerticalSpeedFpm { get; set; }
    public double TouchdownRawPeakG { get; set; }
    public double TouchdownRobustPeakG { get; set; }
    public double TouchdownAirspeedKts { get; set; }
    public double TargetTouchdownAirspeedKts { get; set; }
}
