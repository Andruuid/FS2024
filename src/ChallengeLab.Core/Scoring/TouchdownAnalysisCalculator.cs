using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public static class TouchdownAnalysisCalculator
{
    public static ImpactAnalysis AnalyzeImpact(
        IReadOnlyList<LandingTelemetrySample> samples,
        double contactTimeSeconds,
        double verticalSpeedFpm,
        string verticalSpeedSource,
        LandingSessionSettings settings,
        double? endAtSeconds = null,
        bool forceDegraded = false,
        string? forcedReason = null)
    {
        var end = Math.Min(
            contactTimeSeconds + settings.ImpactWindowSeconds,
            endAtSeconds ?? double.PositiveInfinity);
        var selected = samples
            .Where(s => s.TimeSeconds >= contactTimeSeconds - settings.ImpactPreWindowSeconds
                        && s.TimeSeconds <= end
                        && double.IsFinite(s.TimeSeconds)
                        && double.IsFinite(s.GForce))
            .OrderBy(s => s.TimeSeconds)
            .GroupBy(s => s.TimeSeconds)
            .Select(g => g.Last())
            .ToArray();
        var postCount = selected.Count(s => s.TimeSeconds >= contactTimeSeconds);
        var postRaw = selected.Where(s => s.TimeSeconds >= contactTimeSeconds).Select(s => s.GForce).ToArray();
        var rawPeak = postRaw.Length == 0 ? 1.0 : postRaw.Max();
        var pre = selected.Where(s => s.TimeSeconds < contactTimeSeconds).Select(s => s.GForce).OrderBy(x => x).ToArray();
        double? medianPre = pre.Length == 0 ? null : pre[pre.Length / 2];

        var invalidVerticalSpeed = !double.IsFinite(verticalSpeedFpm);
        var degraded = forceDegraded || invalidVerticalSpeed || postCount < settings.MinImpactSamples;
        var reason = forcedReason;
        if (invalidVerticalSpeed)
            reason = "Touchdown vertical speed was not finite.";
        if (postCount < settings.MinImpactSamples)
            reason = $"Impact G requires {settings.MinImpactSamples} valid post-contact samples; received {postCount}.";

        double robustPeak = rawPeak;
        if (!degraded)
        {
            var values = selected.Select(s => s.GForce).ToArray();
            var times = selected.Select(s => s.TimeSeconds).ToArray();
            var filtered = CompositeScoringMath.ZeroPhaseOnePoleLowPass(
                values, times, settings.ImpactFilterCutoffHz);
            var filteredPost = filtered
                .Where((_, i) => selected[i].TimeSeconds >= contactTimeSeconds)
                .ToArray();
            robustPeak = CompositeScoringMath.Quantile(filteredPost, settings.ImpactPeakQuantile);
            if (!double.IsFinite(robustPeak))
            {
                robustPeak = rawPeak;
                degraded = true;
                reason = "Filtered impact G could not be calculated.";
            }
        }

        return new ImpactAnalysis(
            Available: true,
            TelemetryDegraded: degraded,
            ContactTimeSeconds: contactTimeSeconds,
            VerticalSpeedFpm: double.IsFinite(verticalSpeedFpm) ? verticalSpeedFpm : 0,
            VerticalSpeedSource: verticalSpeedSource,
            RawPeakG: double.IsFinite(rawPeak) ? rawPeak : 1.0,
            RobustPeakG: double.IsFinite(robustPeak) ? robustPeak : 1.0,
            ValidPostContactSamples: postCount,
            MedianPreContactG: medianPre,
            DegradedReason: degraded ? reason ?? "Impact telemetry is degraded." : null);
    }

    public static FloatAnalysis AnalyzeFloat(
        IReadOnlyList<LandingTelemetrySample> samples,
        double touchdownTime,
        double flareAglFeet,
        double entryVerticalSpeedFpm,
        double minimumSustainSeconds)
    {
        var eligible = samples
            .Where(s => double.IsFinite(s.TimeSeconds)
                        && double.IsFinite(s.AglFeet)
                        && double.IsFinite(s.GroundSpeedKts)
                        && double.IsFinite(s.VerticalSpeedFpm)
                        && s.TimeSeconds <= touchdownTime
                        && s.AglFeet <= flareAglFeet)
            .OrderBy(s => s.TimeSeconds)
            .ToArray();
        var preContact = eligible.Where(s => s.TimeSeconds < touchdownTime).ToArray();
        if (preContact.Length < 2
            || preContact[^1].TimeSeconds - preContact[0].TimeSeconds < minimumSustainSeconds)
            return new FloatAnalysis(false, false, 0, 0, 0, 0, 0,
                $"Flare analysis requires at least {minimumSustainSeconds:0.##} seconds of below-flare coverage before touchdown.");

        var candidateStart = -1;
        var floatStart = -1;
        for (var i = 0; i < preContact.Length; i++)
        {
            if (preContact[i].VerticalSpeedFpm < entryVerticalSpeedFpm)
            {
                candidateStart = -1;
                continue;
            }

            if (candidateStart < 0) candidateStart = i;
            if (preContact[i].TimeSeconds - preContact[candidateStart].TimeSeconds >= minimumSustainSeconds)
            {
                floatStart = candidateStart;
                break;
            }
        }

        if (floatStart < 0)
            return new FloatAnalysis(true, false, 0, 0, 0, 0, 0, null);

        var startTime = preContact[floatStart].TimeSeconds;
        var path = eligible.Where(s => s.TimeSeconds >= startTime).ToArray();
        double distance = 0;
        double positiveSeconds = 0;
        double maxPositive = Math.Max(0, path[0].VerticalSpeedFpm);
        for (var i = 1; i < path.Length; i++)
        {
            var previous = path[i - 1];
            var current = path[i];
            var segmentEnd = Math.Min(current.TimeSeconds, touchdownTime);
            var dt = segmentEnd - previous.TimeSeconds;
            if (!double.IsFinite(dt) || dt <= 0) continue;
            var fraction = current.TimeSeconds <= previous.TimeSeconds
                ? 0
                : Math.Clamp(dt / (current.TimeSeconds - previous.TimeSeconds), 0, 1);
            var endGs = previous.GroundSpeedKts + fraction * (current.GroundSpeedKts - previous.GroundSpeedKts);
            distance += Math.Max(0, 0.5 * (previous.GroundSpeedKts + endGs) * 0.514444 * dt);
            if (previous.VerticalSpeedFpm > 0 || current.VerticalSpeedFpm > 0)
                positiveSeconds += dt;
            maxPositive = Math.Max(maxPositive, Math.Max(0, current.VerticalSpeedFpm));
            if (segmentEnd >= touchdownTime) break;
        }

        return new FloatAnalysis(
            true, true, startTime, Math.Max(0, touchdownTime - startTime),
            distance, positiveSeconds, maxPositive, null);
    }

    public static IReadOnlyList<(double Start, double Recontact)> DetectBounceIntervals(
        IReadOnlyList<LandingTelemetrySample> samples,
        double initialTouchdownTime,
        double endTime,
        double minimumAirborneSeconds)
    {
        var result = new List<(double Start, double Recontact)>();
        var hadMainContact = false;
        double? airborneStart = null;
        foreach (var sample in samples
                     .Where(s => s.TimeSeconds >= initialTouchdownTime && s.TimeSeconds <= endTime)
                     .OrderBy(s => s.TimeSeconds))
        {
            if (!sample.MainGearContactsAvailable) continue;
            var anyMain = sample.LeftMainOnGround || sample.RightMainOnGround;
            if (!hadMainContact)
            {
                if (anyMain) hadMainContact = true;
                continue;
            }
            if (!anyMain && airborneStart is null)
            {
                airborneStart = sample.TimeSeconds;
                continue;
            }
            if (anyMain && airborneStart is not null)
            {
                if (sample.TimeSeconds - airborneStart.Value >= minimumAirborneSeconds)
                    result.Add((airborneStart.Value, sample.TimeSeconds));
                airborneStart = null;
            }
        }
        return result;
    }

    public static ContactStabilityAnalysis AnalyzeContactStability(
        IReadOnlyList<LandingTelemetrySample> samples,
        double initialTouchdownTime,
        LandingSessionSettings settings,
        bool windowComplete)
    {
        var end = initialTouchdownTime + settings.BounceWindowSeconds;
        var window = samples
            .Where(s => s.TimeSeconds >= initialTouchdownTime && s.TimeSeconds <= end)
            .OrderBy(s => s.TimeSeconds)
            .ToArray();
        if (window.Length == 0 || window.Any(s => !s.MainGearContactsAvailable))
            return new ContactStabilityAnalysis(false, Array.Empty<BounceEvent>(), 0, 0,
                "Independent main-gear contact telemetry is unavailable.");
        if (!windowComplete && window[^1].TimeSeconds + 0.001 < end)
            return new ContactStabilityAnalysis(false, Array.Empty<BounceEvent>(), 0, 0,
                "The configured bounce observation window is still in progress.");

        var intervals = DetectBounceIntervals(
            window, initialTouchdownTime, end, settings.BounceMinAirborneSeconds);
        var events = new List<BounceEvent>();
        for (var i = 0; i < intervals.Count; i++)
        {
            var interval = intervals[i];
            var nextAirborne = i + 1 < intervals.Count ? intervals[i + 1].Start : (double?)null;
            var recontact = samples
                .Where(s => double.IsFinite(s.TimeSeconds))
                .MinBy(s => Math.Abs(s.TimeSeconds - interval.Recontact))!;
            var impact = AnalyzeImpact(
                samples,
                interval.Recontact,
                recontact.VerticalSpeedFpm,
                "recontact vertical speed",
                settings,
                nextAirborne);
            events.Add(new BounceEvent(
                interval.Start,
                interval.Recontact,
                interval.Recontact - interval.Start,
                impact.VerticalSpeedFpm,
                impact.RawPeakG,
                impact.RobustPeakG,
                impact.TelemetryDegraded));
        }

        return new ContactStabilityAnalysis(
            true,
            events,
            events.Count,
            events.Count == 0 ? 0 : events.Max(x => x.AirborneDurationSeconds),
            events.Any(x => x.ImpactTelemetryDegraded)
                ? "A secondary-impact G window had insufficient telemetry."
                : null);
    }
}
