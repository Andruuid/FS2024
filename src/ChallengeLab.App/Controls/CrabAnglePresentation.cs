using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Controls;

/// <summary>Live true-crab presentation derived from aircraft heading minus ground track.</summary>
internal static class CrabAnglePresentation
{
    internal const double DisplayThresholdDegrees = 0.5;
    internal const double MinimumGroundSpeedKts = 5;

    public static double? FromSample(TelemetrySample sample)
    {
        if (!double.IsFinite(sample.HeadingTrueDeg)
            || !double.IsFinite(sample.GroundSpeedKts)
            || sample.GroundSpeedKts < MinimumGroundSpeedKts
            || sample.GroundTrackTrueDeg is not { } groundTrack
            || !double.IsFinite(groundTrack))
        {
            return null;
        }

        var crabAngle = LandingMonitorCalculator.NormalizeSignedDegrees(
            sample.HeadingTrueDeg - groundTrack);
        return Math.Abs(crabAngle) > DisplayThresholdDegrees
            ? crabAngle
            : null;
    }

    public static string Format(double crabAngleDegrees)
    {
        var side = crabAngleDegrees >= 0 ? "R" : "L";
        return $"CRAB {side} {Math.Abs(crabAngleDegrees):0.0}°";
    }
}
