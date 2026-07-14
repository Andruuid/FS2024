using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public readonly record struct MetricObservation(double? Value, string? UnavailableReason = null)
{
    public bool IsAvailable => Value is not null && string.IsNullOrWhiteSpace(UnavailableReason);

    public static MetricObservation Available(double value) => new(value);
    public static MetricObservation Unavailable(string reason) => new(null, reason);
}

public static class MetricResolver
{
    private static readonly HashSet<string> KnownMetricNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "touchdownVerticalSpeedFpm",
        "centerlineDeviationM",
        "alignmentDeg",
        "touchdownIasErrorKts",
        "excessSpeedOverVappKts",
        "flapsIndex",
        "bankAtTouchdownDeg",
        "approachPathRms",
        "groundTrackErrorMeanDeg",
        "postTouchdownAlignmentMeanDeg",
        "rolloutLateralMeanM",
        "rolloutLateralPeakM",
        "rolloutWeaveIndex"
    };

    public static IReadOnlyCollection<string> KnownMetrics => KnownMetricNames;

    public static bool IsKnownMetric(string metric) => KnownMetricNames.Contains(metric.Trim());

    public static MetricObservation Resolve(string metric, LandingSnapshot snap, ChallengeConfig challenge)
    {
        if (!IsKnownMetric(metric))
            throw new ArgumentException($"Unknown scoring metric '{metric}'.", nameof(metric));

        if (IsTouchdownMetric(metric) && snap.Touchdown is null)
            return MetricObservation.Unavailable("Touchdown telemetry was not captured.");

        return metric.ToLowerInvariant() switch
        {
            "touchdownverticalspeedfpm" => MetricObservation.Available(snap.VerticalSpeedAtTouchdownFpm),
            "centerlinedeviationm" => MetricObservation.Available(Math.Abs(snap.TouchdownLateralOffsetM)),
            "alignmentdeg" => MetricObservation.Available(Math.Abs(snap.TouchdownHeadingErrorDeg)),
            "touchdowniaserrorkts" => MetricObservation.Available(snap.TouchdownIasErrorKts),
            "excessspeedovervappkts" => MetricObservation.Available(snap.ExcessSpeedOverVappKts),
            "flapsindex" => MetricObservation.Available(snap.FlapsIndexAtTouchdown),
            "bankattouchdowndeg" => MetricObservation.Available(Math.Abs(snap.BankAtTouchdownDeg)),
            "approachpathrms" when snap.ApproachPathSampleCount >= 2 =>
                MetricObservation.Available(snap.ApproachPathRms),
            "approachpathrms" =>
                MetricObservation.Unavailable("Approach path requires at least two airborne samples."),
            "groundtrackerrormeandeg" when snap.GroundTrackBeforeSegmentCount >= 1
                                                   && snap.GroundTrackAfterSegmentCount >= 1 =>
                MetricObservation.Available(snap.GroundTrackErrorMeanDeg),
            "groundtrackerrormeandeg" =>
                MetricObservation.Unavailable("Ground track requires valid movement segments before and after touchdown."),
            "posttouchdownalignmentmeandeg" when snap.PostTouchdownAlignmentSampleCount >= 2 =>
                MetricObservation.Available(snap.PostTouchdownAlignmentMeanDeg),
            "posttouchdownalignmentmeandeg" =>
                MetricObservation.Unavailable("Rollout heading alignment requires at least two samples after the delay."),
            "rolloutlateralmeanm" or "rolloutlateralpeakm" or "rolloutweaveindex"
                when snap.RolloutPathSegmentCount >= 2 && snap.RolloutDistanceM >= 1 =>
                MetricObservation.Available(metric.ToLowerInvariant() switch
                {
                    "rolloutlateralmeanm" => snap.RolloutLateralMeanM,
                    "rolloutlateralpeakm" => snap.RolloutLateralPeakM,
                    _ => snap.RolloutWeaveIndex
                }),
            "rolloutlateralmeanm" or "rolloutlateralpeakm" or "rolloutweaveindex" =>
                MetricObservation.Unavailable("Rollout path requires at least two movement segments and one metre traveled."),
            _ => throw new InvalidOperationException($"Metric '{metric}' was validated but has no resolver.")
        };
    }

    public static string? FormatRawDisplay(string metric, LandingSnapshot snap, double? raw, string? unit)
    {
        var m = metric.ToLowerInvariant();
        if (m == "touchdowniaserrorkts")
        {
            var sign = snap.TouchdownIasErrorKts >= 0 ? "+" : "";
            return $"{snap.AirspeedAtTouchdownKts:0.0} kt  (target {snap.TargetTouchdownIasKts:0.0}, VAPP {snap.VappKts:0.0}, err {sign}{snap.TouchdownIasErrorKts:0.0})";
        }

        if (m == "excessspeedovervappkts")
            return $"+{snap.ExcessSpeedOverVappKts:0.0} kt over VAPP {snap.VappKts:0.0}  (IAS {snap.AirspeedAtTouchdownKts:0.0})";

        if (raw is null) return null;
        return unit is null ? $"{raw:0.##}" : $"{raw:0.##} {unit}";
    }

    private static bool IsTouchdownMetric(string metric) => metric.ToLowerInvariant() is
        "touchdownverticalspeedfpm" or
        "centerlinedeviationm" or
        "alignmentdeg" or
        "touchdowniaserrorkts" or
        "excessspeedovervappkts" or
        "flapsindex" or
        "bankattouchdowndeg";
}
