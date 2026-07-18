using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public readonly record struct FreeFlightEvaluationStartState(
    double InterceptDistanceNm,
    double TriggerDistanceNm,
    double CurrentApproachDistanceNm,
    double ClosingSpeedKts,
    double? SecondsUntilStart,
    bool IsPastPlannedStart,
    bool IsReady);

/// <summary>Pure geometry for the Free Flight evaluation start gate.</summary>
public static class FreeFlightEvaluationStartCalculator
{
    public static FreeFlightEvaluationStartState Calculate(
        TelemetrySample sample,
        RunwayEndFacility runway,
        FreeFlightEvaluationStartPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(runway);
        ArgumentNullException.ThrowIfNull(policy);

        var angle = RunwayPathGeometry.SanitizeGlideslopeDeg(runway.GlideslopeDeg);
        var feetPerNm = RunwayPathGeometry.FeetPerNauticalMileForAngle(angle);
        var interceptDistanceNm = Math.Max(
            0,
            policy.HeightAboveRunwayFeet / feetPerNm
            - RunwayPathGeometry.GlideslopeAimPointOffsetNm);

        var headingError = NormalizeSigned(sample.HeadingTrueDeg - runway.HeadingTrueDeg);
        var closingSpeedKts = double.IsFinite(sample.GroundSpeedKts)
            ? Math.Max(0, sample.GroundSpeedKts * Math.Cos(headingError * Math.PI / 180.0))
            : 0;
        var leadDistanceNm = closingSpeedKts * Math.Max(0, policy.LeadSeconds) / 3_600.0;
        var triggerDistanceNm = interceptDistanceNm + leadDistanceNm;

        if (!RunwayPathGeometry.TryGetState(sample, runway.ToRunwayConfig(), out var path))
        {
            return new FreeFlightEvaluationStartState(
                interceptDistanceNm, triggerDistanceNm, double.NaN, closingSpeedKts,
                null, false, false);
        }

        var currentDistanceNm = path.ApproachDistanceNm;
        var pastStart = currentDistanceNm <= triggerDistanceNm;
        double? secondsUntilStart = closingSpeedKts <= 0
            ? null
            : currentDistanceNm > triggerDistanceNm
                ? (currentDistanceNm - triggerDistanceNm) / closingSpeedKts * 3_600.0
                : 0;
        var ready = !sample.SimOnGround
                    && closingSpeedKts > 0
                    && currentDistanceNm > 0
                    && pastStart;

        return new FreeFlightEvaluationStartState(
            interceptDistanceNm,
            triggerDistanceNm,
            currentDistanceNm,
            closingSpeedKts,
            secondsUntilStart,
            pastStart,
            ready);
    }

    private static double NormalizeSigned(double degrees)
    {
        while (degrees > 180) degrees -= 360;
        while (degrees < -180) degrees += 360;
        return degrees;
    }
}
