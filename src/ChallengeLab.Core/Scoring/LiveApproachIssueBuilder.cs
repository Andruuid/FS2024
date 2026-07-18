using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Short live HUD tags explaining why a preview score is not 100%
/// (too high / too low / too fast / weaving / …).
/// </summary>
public static class LiveApproachIssueBuilder
{
    /// <summary>Show instantaneous path/speed tags above this magnitude.</summary>
    private const double AltErrorFeetWarn = 80;
    private const double AltErrorFeetStrong = 200;
    private const double LateralMetersWarn = 15;
    private const double LateralMetersStrong = 35;
    private const double BankDegWarn = 5;
    private const double SteepDescentFpm = -1200;
    private const double ClimbOnFinalFpm = 200;
    private const double FastVsVappKts = 8;
    private const double SlowVsTargetKts = 10;
    private const double ConfigWarnWithinNm = 3.0;

    /// <summary>Include measured scored criteria below this (0–1).</summary>
    private const double LowMetricScore01 = 0.85;

    public readonly record struct Issue(string Label, double Severity, string? Detail = null);

    /// <summary>
    /// Build ordered issues for the live HUD. Empty when nothing notable or outside window.
    /// </summary>
    public static IReadOnlyList<Issue> Build(
        ChallengeConfig challenge,
        LandingSnapshot snapshot,
        ScoreResult? preview,
        TelemetrySample? sample,
        double? vappKts = null,
        double? targetTouchdownIasKts = null,
        double approachMaxDistNm = 4.5)
    {
        var issues = new List<Issue>();

        // Instantaneous flying state (most useful "why" for the pilot right now).
        if (sample is not null
            && !sample.SimOnGround
            && RunwayPathGeometry.TryGetState(sample, challenge.Runway, out var path)
            && path.ApproachDistanceNm > 0.05
            && path.ApproachDistanceNm <= approachMaxDistNm + 0.5)
        {
            AddLivePathIssues(issues, path, sample, vappKts, targetTouchdownIasKts);
            AddLiveConfigIssues(issues, sample, path.ApproachDistanceNm, challenge);
        }

        // Measured preview criteria that are already hurting the score.
        if (preview is not null)
            AddScoredMetricIssues(issues, preview, snapshot);

        // Deduplicate by label (keep highest severity), sort worst-first, cap for HUD.
        return issues
            .GroupBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.Severity).First())
            .OrderByDescending(i => i.Severity)
            .Take(5)
            .ToList();
    }

    /// <summary>Single HUD line, e.g. "too high (+280 ft) · too fast · weaving".</summary>
    public static string FormatLine(IReadOnlyList<Issue> issues)
    {
        if (issues.Count == 0)
            return "";

        return string.Join(" · ", issues.Select(i =>
            string.IsNullOrWhiteSpace(i.Detail) ? i.Label : $"{i.Label} ({i.Detail})"));
    }

    private static void AddLivePathIssues(
        List<Issue> issues,
        RunwayPathGeometry.PathState path,
        TelemetrySample sample,
        double? vappKts,
        double? targetTouchdownIasKts)
    {
        var altErr = path.AltitudeErrorFeet;
        if (altErr >= AltErrorFeetWarn)
        {
            var severity = Math.Min(1.0, Math.Abs(altErr) / 500.0);
            var detail = $"+{altErr:0} ft";
            issues.Add(new Issue(
                altErr >= AltErrorFeetStrong ? "too high" : "slightly high",
                severity + 0.35,
                detail));
        }
        else if (altErr <= -AltErrorFeetWarn)
        {
            var severity = Math.Min(1.0, Math.Abs(altErr) / 500.0);
            var detail = $"{altErr:0} ft";
            issues.Add(new Issue(
                altErr <= -AltErrorFeetStrong ? "too low" : "slightly low",
                severity + 0.4,
                detail));
        }

        var lat = path.LateralMeters;
        if (Math.Abs(lat) >= LateralMetersWarn)
        {
            var side = lat >= 0 ? "right of CL" : "left of CL";
            var severity = Math.Min(1.0, Math.Abs(lat) / 60.0);
            issues.Add(new Issue(
                Math.Abs(lat) >= LateralMetersStrong ? side : "off centerline",
                severity + 0.2,
                $"{Math.Abs(lat):0} m"));
        }

        if (vappKts is > 50 and < 250)
        {
            var vsVapp = sample.AirspeedKts - vappKts.Value;
            if (vsVapp >= FastVsVappKts)
            {
                issues.Add(new Issue(
                    vsVapp >= 20 ? "too fast" : "fast",
                    Math.Min(1.0, vsVapp / 30.0) + 0.25,
                    $"+{vsVapp:0} kt vs VAPP"));
            }
            else if (targetTouchdownIasKts is > 50
                     && sample.AirspeedKts <= targetTouchdownIasKts.Value - SlowVsTargetKts)
            {
                var delta = sample.AirspeedKts - targetTouchdownIasKts.Value;
                issues.Add(new Issue(
                    delta <= -20 ? "too slow" : "slow",
                    Math.Min(1.0, Math.Abs(delta) / 30.0) + 0.3,
                    $"{delta:0} kt vs target"));
            }
        }

        if (sample.VerticalSpeedFpm <= SteepDescentFpm)
        {
            issues.Add(new Issue(
                "steep descent",
                Math.Min(1.0, Math.Abs(sample.VerticalSpeedFpm) / 2500.0) + 0.2,
                $"{sample.VerticalSpeedFpm:0} fpm"));
        }
        else if (sample.VerticalSpeedFpm >= ClimbOnFinalFpm && path.ApproachDistanceNm < 3.5)
        {
            issues.Add(new Issue(
                "climbing on final",
                0.45,
                $"+{sample.VerticalSpeedFpm:0} fpm"));
        }

        var bank = Math.Abs(sample.BankDeg);
        if (bank >= BankDegWarn)
        {
            issues.Add(new Issue(
                bank >= 12 ? "steep bank" : "bank high",
                Math.Min(1.0, bank / 20.0) + 0.15,
                $"{sample.BankDeg:0.0}°"));
        }
    }

    private static void AddLiveConfigIssues(
        List<Issue> issues,
        TelemetrySample sample,
        double distanceNm,
        ChallengeConfig challenge)
    {
        // Only nag once short final — spawn is intentionally gear-up / flaps clean.
        if (distanceNm > ConfigWarnWithinNm)
            return;

        if (challenge.RequireGearDown && sample.GearHandlePosition < 0.5)
        {
            issues.Add(new Issue("gear up", 0.55 + Math.Max(0, (ConfigWarnWithinNm - distanceNm) / 10.0)));
        }

        // Landing flaps typically index ≥ 2 on A330 family.
        if (sample.FlapsHandleIndex < 2)
        {
            issues.Add(new Issue(
                sample.FlapsHandleIndex <= 0 ? "flaps clean" : "flaps low",
                0.4 + Math.Max(0, (ConfigWarnWithinNm - distanceNm) / 12.0),
                $"idx {sample.FlapsHandleIndex}"));
        }
    }

    private static void AddScoredMetricIssues(
        List<Issue> issues,
        ScoreResult preview,
        LandingSnapshot snapshot)
    {
        foreach (var c in preview.Criteria)
        {
            if (c.Status is not (MetricStatus.Scored or MetricStatus.Degraded)
                || c.Score01 is null || c.Score01 >= LowMetricScore01)
                continue;

            // Skip pure path MAE if we already have live high/low — still map other metrics.
            var mapped = MapCriterion(c, snapshot);
            if (mapped is null)
                continue;

            // Lower score → higher severity; weight by phase importance lightly.
            var miss = 1.0 - c.Score01.Value;
            var severity = miss + Math.Min(0.25, c.MaxOverallPoints / 40.0);
            issues.Add(mapped.Value with { Severity = severity });
        }

        if (preview.GearUpPenaltyApplied)
            issues.Add(new Issue("gear-up penalty", 1.2));
        if (preview.FlapsPenaltyApplied)
            issues.Add(new Issue("flaps penalty", 1.0));
        if (preview.Criteria.Any(c =>
                c.Id == "contact_stability" && c.Status == MetricStatus.GateFailed))
            issues.Add(new Issue("bounce penalty", 1.0));
        if (preview.Criteria.Any(c =>
                c.Id == "stall_warning" && c.Status == MetricStatus.GateFailed))
            issues.Add(new Issue("stall-warning penalty", 1.2));
        if (preview.Criteria.Any(c =>
                c.Id == "overspeed_warning" && c.Status == MetricStatus.GateFailed))
            issues.Add(new Issue("overspeed-warning penalty", 1.2));
        foreach (var criterion in preview.Criteria.Where(c =>
                     c.Status == MetricStatus.GateFailed
                     && c.Id is "spoiler_deployment" or "manual_braking" or "nose_gear_impact" or "automation" or "pause_usage" or "simulation_rate" or "cockpit_view"))
            issues.Add(new Issue(
                ScoreBreakdownFormatter.ShortName(criterion.Id, criterion.DisplayName) + " penalty",
                1.1));
    }

    private static Issue? MapCriterion(CriterionScore c, LandingSnapshot snapshot)
    {
        // Prefer pilot-friendly tags over metric display names.
        return c.Id switch
        {
            "approach_glideslope" => new Issue(
                "path off target",
                0,
                snapshot.ApproachGlideslopeMeanBelowDeg > 0 || snapshot.ApproachGlideslopeMeanAboveDeg > 0
                    ? $"low {snapshot.ApproachGlideslopeMeanBelowDeg:0.0}° / high {snapshot.ApproachGlideslopeMeanAboveDeg:0.0}°"
                    : null),
            "approach_vertical_steady" => new Issue("unsteady vertical", 0),
            "approach_lateral_steady" => new Issue("weaving", 0),
            "approach_bank_stability" => new Issue("rocking bank", 0),
            "touchdown_impact" => MapTouchdownImpact(snapshot),
            "flare_efficiency" => new Issue("long float / balloon", 0),
            "contact_stability" => new Issue("bounce", 0),
            "airspeed" => MapIasError(snapshot),
            "excess_speed" => snapshot.ExcessSpeedOverVappKts >= 5
                ? new Issue("excess energy", 0, $"+{snapshot.ExcessSpeedOverVappKts:0} kt")
                : null,
            "centerline" => new Issue("off centerline @ TD", 0),
            "bank" => new Issue("bank @ TD", 0),
            "crab_angle" => new Issue("crab angle", 0),
            "alignment" => new Issue("heading misaligned", 0),
            "post_td_alignment" => new Issue("late de-crab", 0),
            "rollout_centerline" or "rollout_lateral_mean" => new Issue("rollout offset", 0),
            "rollout_weave" => new Issue("rollout weave", 0),
            "rollout_peak" or "rollout_lateral_peak" => new Issue("rollout peak offset", 0),
            "rollout_heading" => new Issue("rollout heading", 0),
            _ => c.Score01 is < 0.5
                ? new Issue(ShortenName(c.DisplayName), 0)
                : null
        };
    }

    private static Issue MapTouchdownImpact(LandingSnapshot snap)
    {
        var vs = snap.VerticalSpeedAtTouchdownFpm;
        var g = snap.InitialImpact?.RobustPeakG ?? 0;
        if (g >= 1.7 || vs <= -600)
            return new Issue("hard impact", 0, $"{vs:0} fpm · {g:0.00} g");
        return new Issue("impact off target", 0, $"{vs:0} fpm · {g:0.00} g");
    }

    private static Issue? MapIasError(LandingSnapshot snap)
    {
        var err = snap.TouchdownIasErrorKts;
        if (err >= 8)
            return new Issue("fast @ TD", 0, $"+{err:0} kt");
        if (err <= -10)
            return new Issue("slow @ TD", 0, $"{err:0} kt");
        return new Issue("IAS off target", 0, $"{err:+0;-0} kt");
    }

    private static string ShortenName(string displayName)
    {
        var s = displayName.Trim();
        if (s.Length <= 22)
            return s.ToLowerInvariant();
        return s[..19].Trim().ToLowerInvariant() + "…";
    }
}
