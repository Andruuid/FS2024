using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public static class NoseGearImpactCalculator
{
    private const double TimeEpsilon = 1e-9;
    private const double IsolatedSpikeDifferenceG = 0.50;
    private const double NeighbourAgreementG = 0.15;

    public static NoseGearImpactAnalysis Analyze(
        IReadOnlyList<LandingTelemetrySample> samples,
        double mainTouchdownTimeSeconds,
        NoseGearImpactGateConfig config)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(config);

        var ordered = samples
            .Where(s => double.IsFinite(s.TimeSeconds))
            .OrderBy(s => s.TimeSeconds)
            .GroupBy(s => s.TimeSeconds)
            .Select(g => g.Last())
            .ToArray();
        var analysis = new NoseGearImpactAnalysis
        {
            NoseGearContactCoverageAvailable = ordered.Any(s =>
                s.TimeSeconds >= mainTouchdownTimeSeconds - TimeEpsilon
                && s.NoseGearContactAvailable),
            GForceCoverageAvailable = ordered.Any(s =>
                s.TimeSeconds >= mainTouchdownTimeSeconds - config.PreContactWindowSeconds - TimeEpsilon
                && s.GForceAvailable
                && double.IsFinite(s.GForce)),
            CompressionTelemetryCoverageAvailable = ordered.Any(s =>
                s.TimeSeconds >= mainTouchdownTimeSeconds - config.PreContactWindowSeconds - TimeEpsilon
                && s.ContactPointTelemetryAvailable
                && s.ContactPointOnGroundByIndex is not null
                && s.ContactPointCompressionByIndex is not null)
        };

        if (!analysis.NoseGearContactCoverageAvailable)
        {
            analysis.DegradedReason = "Nose-gear contact mapping is unavailable.";
            return analysis;
        }

        if (!analysis.GForceCoverageAvailable)
        {
            analysis.DegradedReason = "Aircraft-G telemetry is unavailable around nose contact.";
            return analysis;
        }

        var contactTimes = DetectContactTimes(ordered, mainTouchdownTimeSeconds,
            config.RecontactDebounceSeconds);
        if (contactTimes.Count == 0)
        {
            analysis.DegradedReason = "A verified nose-gear touchdown was not observed.";
            return analysis;
        }

        foreach (var contactTime in contactTimes)
            analysis.Events.Add(AnalyzeEvent(ordered, contactTime, config));

        analysis.CompressionFallbackUsed = analysis.Events.Any(e => e.CompressionFallbackUsed);
        analysis.WorstEvent = analysis.Events
            .OrderByDescending(e => e.Severity)
            .ThenByDescending(e => e.DeltaG)
            .ThenByDescending(e => e.RobustPeakG)
            .FirstOrDefault();

        var degraded = analysis.Events.FirstOrDefault(e => e.TelemetryDegraded);
        if (degraded is not null)
        {
            analysis.DegradedReason = degraded.DegradedReason
                                      ?? "Nose-gear impact telemetry is incomplete.";
            return analysis;
        }

        analysis.CoverageSufficient = true;
        return analysis;
    }

    private static List<double> DetectContactTimes(
        IReadOnlyList<LandingTelemetrySample> samples,
        double mainTouchdownTimeSeconds,
        double debounceSeconds)
    {
        var result = new List<double>();
        var preState = samples.LastOrDefault(s =>
            s.TimeSeconds < mainTouchdownTimeSeconds && s.NoseGearContactAvailable);
        var wasOnGround = preState?.NoseOnGround ?? false;
        double? airborneSince = wasOnGround ? null : mainTouchdownTimeSeconds;

        foreach (var sample in samples.Where(s =>
                     s.TimeSeconds >= mainTouchdownTimeSeconds - TimeEpsilon
                     && s.NoseGearContactAvailable))
        {
            if (!sample.NoseOnGround)
            {
                if (wasOnGround || airborneSince is null)
                    airborneSince = sample.TimeSeconds;
                wasOnGround = false;
                continue;
            }

            if (!wasOnGround)
            {
                var isInitial = result.Count == 0;
                var airborneLongEnough = airborneSince is { } start
                                         && sample.TimeSeconds - start + TimeEpsilon >= debounceSeconds;
                if (isInitial || airborneLongEnough)
                    result.Add(sample.TimeSeconds);
            }

            wasOnGround = true;
            airborneSince = null;
        }

        return result;
    }

    private static NoseGearImpactEvent AnalyzeEvent(
        IReadOnlyList<LandingTelemetrySample> samples,
        double contactTime,
        NoseGearImpactGateConfig config)
    {
        var selected = samples
            .Where(s => s.TimeSeconds >= contactTime - config.PreContactWindowSeconds - TimeEpsilon
                        && s.TimeSeconds <= contactTime + config.PostContactWindowSeconds + TimeEpsilon
                        && s.GForceAvailable
                        && double.IsFinite(s.GForce))
            .ToArray();
        var pre = selected.Where(s => s.TimeSeconds < contactTime - TimeEpsilon)
            .Select(s => s.GForce)
            .OrderBy(x => x)
            .ToArray();
        var postCount = selected.Count(s => s.TimeSeconds >= contactTime - TimeEpsilon);

        var result = new NoseGearImpactEvent
        {
            ContactTimeSeconds = contactTime,
            ValidPostContactSamples = postCount,
            MedianPreContactG = pre.Length == 0 ? null : Median(pre)
        };

        if (pre.Length == 0 || postCount < config.MinimumPostContactSamples)
        {
            result.TelemetryDegraded = true;
            result.DegradedReason = pre.Length == 0
                ? "No valid pre-contact G samples were available for the nose-impact baseline."
                : $"Nose impact requires {config.MinimumPostContactSamples} valid post-contact G samples; received {postCount}.";
            ApplyCompressionDiagnostics(result, samples, contactTime, config);
            return result;
        }

        var times = selected.Select(s => s.TimeSeconds).ToArray();
        var rawValues = selected.Select(s => s.GForce).ToArray();
        var rawObservedPost = rawValues.Where((_, i) =>
            selected[i].TimeSeconds >= contactTime - TimeEpsilon).ToArray();
        var cleanedValues = RejectIsolatedSpikes(rawValues);
        var filtered = CompositeScoringMath.ZeroPhaseOnePoleLowPass(
            cleanedValues, times, config.FilterCutoffHz);
        var filteredPost = filtered.Where((_, i) =>
            selected[i].TimeSeconds >= contactTime - TimeEpsilon).ToArray();
        var rawPost = cleanedValues.Where((_, i) =>
            selected[i].TimeSeconds >= contactTime - TimeEpsilon).ToArray();
        var robustPeak = CompositeScoringMath.Quantile(filteredPost, config.PeakQuantile);
        if (!double.IsFinite(robustPeak) || rawPost.Length == 0)
        {
            result.TelemetryDegraded = true;
            result.DegradedReason = "Filtered nose-impact G could not be calculated.";
            ApplyCompressionDiagnostics(result, samples, contactTime, config);
            return result;
        }

        result.RawPeakG = rawObservedPost.Length == 0 ? rawPost.Max() : rawObservedPost.Max();
        result.RobustPeakG = robustPeak;
        result.DeltaG = Math.Max(0, robustPeak - result.MedianPreContactG!.Value);
        if (result.DeltaG + TimeEpsilon >= config.SevereDeltaG
            && result.RobustPeakG + TimeEpsilon >= config.SeverePeakG)
        {
            result.Severity = NoseGearImpactSeverity.Severe;
            result.AppliedMultiplier = config.SevereMultiplier;
        }
        else if (result.DeltaG + TimeEpsilon >= config.ModerateDeltaG
                 && result.RobustPeakG + TimeEpsilon >= config.ModeratePeakG)
        {
            result.Severity = NoseGearImpactSeverity.Moderate;
            result.AppliedMultiplier = config.ModerateMultiplier;
        }

        ApplyCompressionDiagnostics(result, samples, contactTime, config);
        return result;
    }

    private static void ApplyCompressionDiagnostics(
        NoseGearImpactEvent result,
        IReadOnlyList<LandingTelemetrySample> samples,
        double contactTime,
        NoseGearImpactGateConfig config)
    {
        var pre = samples.LastOrDefault(s =>
            s.TimeSeconds < contactTime - TimeEpsilon
            && s.TimeSeconds >= contactTime - config.PreContactWindowSeconds - TimeEpsilon
            && s.ContactPointTelemetryAvailable
            && s.ContactPointOnGroundByIndex is not null
            && s.ContactPointCompressionByIndex is not null);
        var post = samples.Where(s =>
                s.TimeSeconds >= contactTime - TimeEpsilon
                && s.TimeSeconds <= contactTime + Math.Min(0.25, config.PostContactWindowSeconds) + TimeEpsilon
                && s.ContactPointTelemetryAvailable
                && s.ContactPointOnGroundByIndex is not null
                && s.ContactPointCompressionByIndex is not null)
            .ToArray();

        result.CompressionTelemetryAvailable = pre is not null || post.Length > 0;
        if (pre is null || post.Length == 0)
        {
            result.CompressionFallbackUsed = true;
            return;
        }

        var indices = pre.ContactPointOnGroundByIndex!.Keys
            .Concat(post.SelectMany(s => s.ContactPointOnGroundByIndex!.Keys))
            .Distinct()
            .OrderBy(i => i);
        double? bestPeak = null;
        double? bestRise = null;
        foreach (var index in indices)
        {
            var preOnGround = pre.ContactPointOnGroundByIndex.TryGetValue(index, out var beforeGround)
                              && beforeGround;
            var becameGrounded = post.Any(s =>
                s.ContactPointOnGroundByIndex!.TryGetValue(index, out var grounded) && grounded);
            if (preOnGround || !becameGrounded)
                continue;

            var baseline = pre.ContactPointCompressionByIndex!.TryGetValue(index, out var beforeCompression)
                && double.IsFinite(beforeCompression)
                    ? beforeCompression
                    : 0;
            var peak = post.Select(s =>
                    s.ContactPointCompressionByIndex!.TryGetValue(index, out var value)
                    && double.IsFinite(value) ? value : double.NaN)
                .Where(double.IsFinite)
                .DefaultIfEmpty(double.NaN)
                .Max();
            if (!double.IsFinite(peak))
                continue;

            var rise = Math.Max(0, peak - baseline);
            if (rise + TimeEpsilon < config.CompressionNoiseThreshold)
                continue;

            result.CorrelatedContactPointIndices.Add(index);
            if (bestRise is null || rise > bestRise)
            {
                bestRise = rise;
                bestPeak = peak;
            }
        }

        result.CompressionCorroborated = result.CorrelatedContactPointIndices.Count > 0;
        result.CompressionFallbackUsed = !result.CompressionCorroborated;
        result.PeakCompression = bestPeak;
        result.CompressionRise = bestRise;
    }

    private static double[] RejectIsolatedSpikes(IReadOnlyList<double> values)
    {
        var result = values.ToArray();
        for (var i = 1; i < values.Count - 1; i++)
        {
            var left = values[i - 1];
            var current = values[i];
            var right = values[i + 1];
            if (Math.Abs(current - left) >= IsolatedSpikeDifferenceG
                && Math.Abs(current - right) >= IsolatedSpikeDifferenceG
                && Math.Abs(left - right) <= NeighbourAgreementG)
                result[i] = (left + right) / 2.0;
        }
        return result;
    }

    private static double Median(IReadOnlyList<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }
}
