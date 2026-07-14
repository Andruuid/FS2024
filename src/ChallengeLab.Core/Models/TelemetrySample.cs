namespace ChallengeLab.Core.Models;

/// <summary>Normalized flight telemetry sample used by scoring (units documented per property).</summary>
public sealed class TelemetrySample
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public double Latitude { get; init; }
    public double Longitude { get; init; }
    public double AltitudeFeet { get; init; }
    public double AglFeet { get; init; }

    public double HeadingTrueDeg { get; init; }

    /// <summary>
    /// Ground track (direction of CG motion over the ground), true degrees.
    /// Prefer this over heading for path-over-ground scoring (crab is wind-dependent).
    /// </summary>
    public double GroundTrackTrueDeg { get; init; }

    public double PitchDeg { get; init; }
    public double BankDeg { get; init; }

    /// <summary>Indicated airspeed (knots).</summary>
    public double AirspeedKts { get; init; }

    /// <summary>Ground speed (knots).</summary>
    public double GroundSpeedKts { get; init; }

    /// <summary>Vertical speed (feet per minute). Negative = descending.</summary>
    public double VerticalSpeedFpm { get; init; }

    public double GForce { get; init; }
    public bool SimOnGround { get; init; }

    public double GearHandlePosition { get; init; }
    public int FlapsHandleIndex { get; init; }

    public double WindDirectionDeg { get; init; }
    public double WindVelocityKts { get; init; }

    public double RadioHeightFeet { get; init; }

    /// <summary>Stall speed landing config (DESIGN SPEED VS0), if available.</summary>
    public double DesignSpeedVs0Kts { get; init; }

    /// <summary>Total weight (lbs), if available.</summary>
    public double? TotalWeightLbs { get; init; }

    /// <summary>Ground speed in km/h (derived convenience).</summary>
    public double GroundSpeedKmh => GroundSpeedKts * 1.852;
}
