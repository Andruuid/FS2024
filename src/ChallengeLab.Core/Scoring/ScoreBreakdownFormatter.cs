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
        result.PhaseScores,
        result.Criteria,
        result.IncompleteReasons);

    public static string Format(
        double? scorePercent,
        string grade,
        double? scoreBeforeGatesPercent,
        bool gearUpPenaltyApplied,
        IReadOnlyList<PhaseScore> phaseScores,
        IReadOnlyList<CriterionScore> criteria,
        IReadOnlyList<string>? incompleteReasons = null)
    {
        var sb = new StringBuilder();
        if (scorePercent is not null)
            sb.Append("Total Grade ").Append(grade).Append("  ").Append(Pct(scorePercent));
        else
            sb.Append("Total UNRANKED — incomplete telemetry");
        sb.AppendLine();

        if (gearUpPenaltyApplied)
        {
            sb.Append("(pre-gate ").Append(Pct(scoreBeforeGatesPercent))
                .Append(" · gear-up penalty applied)");
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

        if (gearUpPenaltyApplied || criteria.Any(c => c.Status == MetricStatus.GateFailed))
        {
            sb.AppendLine();
            sb.AppendLine("GEAR UP (hard penalty)");
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
            Note = m.Note,
            UnavailableReason = m.UnavailableReason
        }).ToList();

        return Format(
            scorePercent,
            grade,
            scoreBeforeGates ?? scorePercent,
            gearUpPenalty,
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
        "airspeed" => "airspeed",
        "centerline" => "centerline",
        "ground_track" => "groundTrack",
        "excess_speed" => "excessSpeed",
        "bank" => "bank",
        "alignment" => "alignment",
        "flaps" => "flaps",
        "approach_path" => "3° path accuracy",
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
