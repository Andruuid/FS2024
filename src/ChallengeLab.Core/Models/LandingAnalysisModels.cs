using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Models;

/// <summary>Small logical sample used exclusively by touchdown-event analysis.</summary>
public sealed record LandingTelemetrySample(
    double TimeSeconds,
    double AglFeet,
    double GroundSpeedKts,
    double VerticalSpeedFpm,
    double GForce,
    bool LeftMainOnGround,
    bool RightMainOnGround,
    bool NoseOnGround,
    bool MainGearContactsAvailable,
    bool NoseGearContactAvailable = true,
    bool GForceAvailable = true,
    IReadOnlyDictionary<int, bool>? ContactPointOnGroundByIndex = null,
    IReadOnlyDictionary<int, double>? ContactPointCompressionByIndex = null,
    bool ContactPointTelemetryAvailable = false);

public sealed record ImpactAnalysis(
    bool Available,
    bool TelemetryDegraded,
    double ContactTimeSeconds,
    double VerticalSpeedFpm,
    string VerticalSpeedSource,
    double RawPeakG,
    double RobustPeakG,
    int ValidPostContactSamples,
    double? MedianPreContactG,
    string? DegradedReason,
    double? TouchdownNormalVelocityFpm = null);

public sealed record FloatAnalysis(
    bool CoverageSufficient,
    bool Detected,
    double StartTimeSeconds,
    double DurationSeconds,
    double DistanceMetres,
    double PositiveVerticalSpeedSeconds,
    double MaximumPositiveVerticalSpeedFpm,
    string? DegradedReason);

public sealed record BounceEvent(
    double AirborneStartTimeSeconds,
    double RecontactTimeSeconds,
    double AirborneDurationSeconds,
    double VerticalSpeedAtRecontactFpm,
    double RawPeakGAtRecontact,
    double RobustPeakGAtRecontact,
    bool ImpactTelemetryDegraded);

public sealed record ContactStabilityAnalysis(
    bool CoverageSufficient,
    IReadOnlyList<BounceEvent> Bounces,
    int BounceCount,
    double MaximumAirborneDurationSeconds,
    string? DegradedReason);

/// <summary>
/// Fuselage heading alignment at main-gear touchdown and throughout the fixed
/// three-second directional-control window that follows it.
/// </summary>
public sealed record CrabAngleAnalysis(
    bool CoverageSufficient,
    double TouchdownErrorDeg,
    double IntegratedDeviationDegSeconds,
    double CoverageSeconds,
    double MeanAbsoluteDeviationDeg,
    double PeakDeviationDeg,
    int SampleCount,
    string? DegradedReason);

/// <summary>
/// Independent fuselage-heading and ground-track alignment with the runway at
/// main-gear touchdown and throughout the fixed three-second control window.
/// </summary>
public sealed record RunwayAlignmentAnalysis(
    bool CoverageSufficient,
    double TouchdownHeadingErrorDeg,
    double TouchdownTrackErrorDeg,
    double TouchdownTrueCrabAngleDeg,
    double IntegratedHeadingDeviationDegSeconds,
    double IntegratedTrackDeviationDegSeconds,
    double CoverageSeconds,
    double MeanAbsoluteHeadingDeviationDeg,
    double MeanAbsoluteTrackDeviationDeg,
    double PeakHeadingDeviationDeg,
    double PeakTrackDeviationDeg,
    int SampleCount,
    string GroundTrackSource,
    string? DegradedReason);

/// <summary>Raw values and component scores needed to explain or persist a landing result.</summary>
public sealed class LandingResultDiagnostics
{
    public LandingGateObservations OperationalGates { get; set; } = new();
    public FreeFlightCapabilityContext? FreeFlightCapabilities { get; set; }

    public double TouchdownVerticalSpeedFpm { get; set; }
    public double TouchdownSinkRateFpm { get; set; }
    public double? TouchdownNormalVelocityFpm { get; set; }
    public double TouchdownVerticalSpeedSubscore { get; set; }
    public string TouchdownVerticalSpeedSource { get; set; } = "";
    public double TouchdownRawPeakG { get; set; }
    public double TouchdownRobustPeakG { get; set; }
    public double TouchdownPeakGSubscore { get; set; }
    public double TouchdownImpactScore { get; set; }
    public bool ImpactTelemetryDegraded { get; set; }

    public bool FloatDetected { get; set; }
    public double FloatStartTimeSeconds { get; set; }
    public double FloatSeconds { get; set; }
    public double FloatDistanceM { get; set; }
    public double PositiveVerticalSpeedSeconds { get; set; }
    public double MaximumPositiveVerticalSpeedFpm { get; set; }
    public double FloatDistanceSubscore { get; set; }
    public double FloatTimeSubscore { get; set; }
    public double PositiveVerticalSpeedSubscore { get; set; }
    public double FlareEfficiencyScore { get; set; }
    public bool FlareTelemetryDegraded { get; set; }

    public int BounceCount { get; set; }
    public double BounceCountSubscore { get; set; } = 100;
    public double MaximumBounceAirborneSeconds { get; set; }
    public double MaximumBounceAirborneDurationSubscore { get; set; } = 100;
    public double? WorstSecondaryTouchdownVerticalSpeedFpm { get; set; }
    public double? WorstSecondaryRawPeakG { get; set; }
    public double? WorstSecondaryRobustPeakG { get; set; }
    public double WorstSecondaryImpactScore { get; set; } = 100;
    public double ContactStabilityScore { get; set; } = 100;
    public bool ContactTelemetryDegraded { get; set; }
    public List<ScoredBounceDiagnostic> Bounces { get; set; } = new();

    public double CrabAngleTouchdownDeg { get; set; }
    public double CrabAngleTouchdownSubscore { get; set; }
    public double CrabAngleThreeSecondIntegralDegSeconds { get; set; }
    public double CrabAngleThreeSecondSubscore { get; set; }
    public double CrabAngleScore { get; set; }
    public bool CrabAngleTelemetryDegraded { get; set; }

    public double RunwayAlignmentHeadingTouchdownDeg { get; set; }
    public double RunwayAlignmentTrackTouchdownDeg { get; set; }
    public double TouchdownTrueCrabAngleDeg { get; set; }
    public double? TouchdownGroundTrackTrueDeg { get; set; }
    public string TouchdownGroundTrackSource { get; set; } = "";
    public double RunwayAlignmentHeadingTouchdownSubscore { get; set; }
    public double RunwayAlignmentTrackTouchdownSubscore { get; set; }
    public double RunwayAlignmentHeadingThreeSecondIntegralDegSeconds { get; set; }
    public double RunwayAlignmentTrackThreeSecondIntegralDegSeconds { get; set; }
    public double RunwayAlignmentHeadingThreeSecondSubscore { get; set; }
    public double RunwayAlignmentTrackThreeSecondSubscore { get; set; }
    public double RunwayAlignmentScore { get; set; }
    public bool RunwayAlignmentTelemetryDegraded { get; set; }
}

public sealed class ScoredBounceDiagnostic
{
    public double AirborneStartTimeSeconds { get; set; }
    public double RecontactTimeSeconds { get; set; }
    public double AirborneDurationSeconds { get; set; }
    public double VerticalSpeedAtRecontactFpm { get; set; }
    public double RobustPeakGAtRecontact { get; set; }
    public double SecondaryImpactScore { get; set; }
}
