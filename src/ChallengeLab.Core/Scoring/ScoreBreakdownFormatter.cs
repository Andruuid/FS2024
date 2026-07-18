using System.Text;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public static class ScoreBreakdownFormatter
{
    public static string Format(ScoreResult result) => Format(
        result.ScorePercent,
        result.Grade,
        result.ScoreBeforeGatesPercent,
        result.GearUpPenaltyApplied,
        result.FlapsPenaltyApplied,
        result.PhaseScores,
        result.Criteria,
        result.IncompleteReasons);

    public static string Format(
        double? scorePercent,
        string grade,
        double? scoreBeforeGatesPercent,
        bool gearUpPenaltyApplied,
        bool flapsPenaltyApplied,
        IReadOnlyList<PhaseScore> phaseScores,
        IReadOnlyList<CriterionScore> criteria,
        IReadOnlyList<string>? incompleteReasons = null)
    {
        var sb = new StringBuilder();
        var contactPenaltyApplied = criteria.Any(c =>
            c.Id.Equals("contact_stability", StringComparison.OrdinalIgnoreCase)
            && c.Status == MetricStatus.GateFailed);
        var stallWarningPenaltyApplied = criteria.Any(c =>
            c.Id.Equals("stall_warning", StringComparison.OrdinalIgnoreCase)
            && c.Status == MetricStatus.GateFailed);
        var overspeedWarningPenaltyApplied = criteria.Any(c =>
            c.Id.Equals("overspeed_warning", StringComparison.OrdinalIgnoreCase)
            && c.Status == MetricStatus.GateFailed);
        var operationalPenalties = criteria.Where(c =>
                c.Status == MetricStatus.GateFailed
                && c.Id is "spoiler_deployment" or "manual_braking" or "nose_gear_impact" or "automation" or "pause_usage" or "simulation_rate" or "cockpit_view" or "rollout_distance" or "reverse_thrust")
            .ToList();
        var assumedAdjustments = criteria.Where(c =>
            c.Status == MetricStatus.Assumed && c.AppliedMultiplier is < 1).ToList();
        if (scorePercent is not null)
            sb.Append("Total Grade ").Append(grade).Append("  ").Append(Pct(scorePercent));
        else
            sb.Append("Total UNRANKED — incomplete telemetry");
        sb.AppendLine();

        if (contactPenaltyApplied || stallWarningPenaltyApplied || overspeedWarningPenaltyApplied
            || gearUpPenaltyApplied || flapsPenaltyApplied || operationalPenalties.Count > 0
            || assumedAdjustments.Count > 0)
        {
            sb.Append("(pre-penalty metric total ").Append(Pct(scoreBeforeGatesPercent));
            if (contactPenaltyApplied) sb.Append(" · bounce penalty");
            if (stallWarningPenaltyApplied) sb.Append(" · stall-warning penalty");
            if (overspeedWarningPenaltyApplied) sb.Append(" · overspeed-warning penalty");
            if (gearUpPenaltyApplied) sb.Append(" · gear-up penalty");
            if (flapsPenaltyApplied) sb.Append(" · flaps penalty");
            foreach (var penalty in operationalPenalties)
                sb.Append(" · ").Append(ShortName(penalty.Id, penalty.DisplayName)).Append(" penalty");
            foreach (var assumed in assumedAdjustments)
                sb.Append(" · ").Append(ShortName(assumed.Id, assumed.DisplayName))
                    .Append(" assumed x").Append(assumed.AppliedMultiplier!.Value.ToString("0.###"));
            sb.Append(')');
            sb.AppendLine();
        }

        if (incompleteReasons is { Count: > 0 })
        {
            sb.AppendLine("Missing required data:");
            foreach (var reason in incompleteReasons.Distinct(StringComparer.OrdinalIgnoreCase))
                sb.Append("- ").AppendLine(reason);
        }

        foreach (var phase in OrderPhases(phaseScores))
        {
            sb.AppendLine();
            sb.Append(phase.DisplayName).Append(' ').Append(Pct(phase.ScorePercent))
                .Append(" (").Append(Pct(phase.WeightPercent)).Append(" overall)");
            sb.AppendLine();

            foreach (var metric in criteria.Where(c =>
                         string.Equals(c.PhaseId, phase.PhaseId, StringComparison.OrdinalIgnoreCase)))
                AppendMetricLine(sb, metric.Id, metric.DisplayName, metric.ScorePercent);
        }

        if (contactPenaltyApplied || stallWarningPenaltyApplied || overspeedWarningPenaltyApplied
            || gearUpPenaltyApplied || flapsPenaltyApplied
            || criteria.Any(c => c.Status == MetricStatus.GateFailed)
            || assumedAdjustments.Count > 0)
        {
            sb.AppendLine();
            if (contactPenaltyApplied)
                sb.AppendLine("BOUNCE (penalty gate)");
            if (stallWarningPenaltyApplied)
                sb.AppendLine("STALL WARNING (aircraft-warning penalty gate)");
            if (overspeedWarningPenaltyApplied)
                sb.AppendLine("OVERSPEED WARNING (aircraft-warning penalty gate)");
            if (gearUpPenaltyApplied)
                sb.AppendLine("GEAR UP (hard penalty)");
            if (flapsPenaltyApplied)
                sb.AppendLine("FLAPS NOT SET (penalty)");
            foreach (var penalty in operationalPenalties)
                sb.AppendLine($"{penalty.DisplayName.ToUpperInvariant()} (penalty)");
            foreach (var assumed in assumedAdjustments)
                sb.AppendLine($"{assumed.DisplayName.ToUpperInvariant()} (assumed x{assumed.AppliedMultiplier:0.###})");
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string FormatFromStored(
        double scorePercent,
        string grade,
        double? scoreBeforeGates,
        bool gearUpPenalty,
        IReadOnlyList<HighscorePhaseDetail>? phases,
        IEnumerable<StoredMetric> metrics)
        => FormatFromStored(scorePercent, grade, scoreBeforeGates, gearUpPenalty,
            flapsPenalty: false, phases, metrics);

    public static string FormatFromStored(
        double scorePercent,
        string grade,
        double? scoreBeforeGates,
        bool gearUpPenalty,
        bool flapsPenalty,
        IReadOnlyList<HighscorePhaseDetail>? phases,
        IEnumerable<StoredMetric> metrics)
    {
        var storedMetrics = metrics.ToList();
        var phaseList = phases ?? Array.Empty<HighscorePhaseDetail>();
        var hasHierarchy = phaseList.Count > 0
                           && storedMetrics.Any(m => !string.IsNullOrWhiteSpace(m.PhaseId));
        if (!hasHierarchy)
            return FormatLegacy(scorePercent, grade, storedMetrics);

        var phaseScores = phaseList.Select(p => new PhaseScore
        {
            PhaseId = p.PhaseId,
            DisplayName = p.DisplayName,
            WeightPercent = p.WeightPercent,
            ScorePercent = p.ScorePercent,
            IsComplete = p.Used && p.ScorePercent is not null
        }).ToList();

        var criteria = storedMetrics.Select(m => new CriterionScore
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Score01 = m.ScorePercent / 100.0,
            Status = m.Status,
            PhaseId = m.PhaseId,
            PhaseDisplayName = m.PhaseDisplayName,
            PhaseImportancePercent = m.PhaseImportancePercent,
            PhaseWeightPercent = m.PhaseWeightPercent,
            MaxOverallPoints = m.MaxOverallPoints,
            AppliedMultiplier = m.AppliedMultiplier,
            Note = m.Note,
            UnavailableReason = m.UnavailableReason
        }).ToList();

        return Format(
            scorePercent,
            grade,
            scoreBeforeGates ?? scorePercent,
            gearUpPenalty,
            flapsPenalty,
            phaseScores,
            criteria);
    }

    private static string FormatLegacy(double scorePercent, string grade, IReadOnlyList<StoredMetric> metrics)
    {
        var sb = new StringBuilder();
        sb.Append("Total Grade ").Append(grade).Append("  ").Append(Pct(scorePercent));
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Legacy metrics — phase breakdown unavailable");
        foreach (var metric in metrics)
            AppendMetricLine(sb, metric.Id, metric.DisplayName, metric.ScorePercent);
        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static IEnumerable<PhaseScore> OrderPhases(IEnumerable<PhaseScore> phases) =>
        phases.OrderBy(p => PhaseOrder(p.PhaseId)).ThenBy(p => p.DisplayName);

    private static int PhaseOrder(string id) => id.ToLowerInvariant() switch
    {
        "touchdown" => 0,
        "approach" => 1,
        "rollout" => 2,
        _ => 3
    };

    private static void AppendMetricLine(StringBuilder sb, string id, string displayName, double? scorePercent)
    {
        sb.Append('-').Append(ShortName(id, displayName)).Append(' ').Append(Pct(scorePercent));
        sb.AppendLine();
    }

    public static string ShortName(string id, string displayName) => id.ToLowerInvariant() switch
    {
        "touchdown_vs" => "vSpeed",
        "touchdown_impact" => "impact",
        "flare_efficiency" => "flare/float",
        "contact_stability" => "contact",
        "airspeed" => "airspeed",
        "centerline" => "centerline",
        "excess_speed" => "excessSpeed",
        "bank" => "bank",
        "crab_angle" => "crab angle",
        "alignment" => "alignment",
        "flaps" => "flaps",
        "stall_warning" => "stall warning",
        "overspeed_warning" => "overspeed warning",
        "spoiler_deployment" => "spoilers",
        "manual_braking" => "braking",
        "nose_gear_impact" => "nose impact",
        "automation" => "automation",
        "pause_usage" => "pause",
        "simulation_rate" => "sim rate",
        "cockpit_view" => "cockpit view",
        "approach_path" => "3° path accuracy",
        "approach_glideslope" => "avg glideslope",
        "approach_vertical_steady" => "vert steady",
        "approach_lateral_steady" => "lat steady",
        "approach_bank_stability" => "bank steady",
        "post_td_alignment" => "heading",
        "rollout_path" => "centerline",
        "rollout_weave" => "weave",
        "max_centerline" => "maxCenterline",
        "gear" => "gear",
        _ when !string.IsNullOrWhiteSpace(displayName) => displayName,
        _ => id
    };

    public static string Pct(double? percent)
    {
        if (percent is null) return "N/A";
        var rounded = Math.Round(percent.Value, 1);
        if (Math.Abs(rounded - Math.Round(rounded)) < 0.05)
            return $"{Math.Round(rounded):0}%";
        return $"{rounded:0.0}%";
    }

    public sealed class StoredMetric
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public double? ScorePercent { get; init; }
        public MetricStatus Status { get; init; } = MetricStatus.Scored;
        public string? PhaseId { get; init; }
        public string? PhaseDisplayName { get; init; }
        public double PhaseImportancePercent { get; init; }
        public double PhaseWeightPercent { get; init; }
        public double MaxOverallPoints { get; init; }
        public double? AppliedMultiplier { get; init; }
        public string? Note { get; init; }
        public string? UnavailableReason { get; init; }
    }
}

public sealed class HighscorePhaseDetail
{
    public string PhaseId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double WeightPercent { get; set; }
    public double? ScorePercent { get; set; }
    public bool Used { get; set; } = true;
}
