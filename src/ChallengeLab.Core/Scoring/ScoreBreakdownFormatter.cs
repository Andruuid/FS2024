using System.Text;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Hierarchical landing result text:
/// <code>
/// Total Grade S  86.4%
///
/// Touchdown 80%
/// -vSpeed 90%
/// -airspeed 82.3%
///
/// Approach 76.4%
/// -steadiness 70%
///
/// Rollout 88%
/// -centerline 86%
/// </code>
/// </summary>
public static class ScoreBreakdownFormatter
{
    public static string Format(ScoreResult result) => Format(
        result.ScorePercent,
        result.Grade,
        result.ScoreBeforeGatesPercent,
        result.GearUpPenaltyApplied,
        result.PhaseScores,
        result.Criteria);

    public static string Format(
        double scorePercent,
        string grade,
        double scoreBeforeGatesPercent,
        bool gearUpPenaltyApplied,
        IReadOnlyList<PhaseScore> phaseScores,
        IReadOnlyList<CriterionScore> criteriaAll)
    {
        var sb = new StringBuilder();
        sb.Append("Total Grade ").Append(grade).Append("  ").Append(Pct(scorePercent));
        sb.AppendLine();

        if (gearUpPenaltyApplied)
        {
            sb.Append("(pre-gate ").Append(Pct(scoreBeforeGatesPercent))
                .Append(" · gear-up penalty applied)");
            sb.AppendLine();
        }

        var criteria = criteriaAll
            .Where(c => c.Applied && c.Weight > 0
                        && !c.Id.Equals("gear", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (phaseScores is { Count: > 0 } && phaseScores.Any(p => p.Used))
        {
            foreach (var phase in phaseScores.Where(p => p.Used))
            {
                sb.AppendLine();
                sb.Append(phase.DisplayName).Append(' ').Append(Pct(phase.ScorePercent));
                sb.AppendLine();

                var phaseMetrics = criteria
                    .Where(c => string.Equals(c.PhaseId, phase.PhaseId, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (phaseMetrics.Count == 0)
                {
                    phaseMetrics = criteria
                        .Where(c => string.Equals(c.PhaseDisplayName, phase.DisplayName, StringComparison.OrdinalIgnoreCase)
                                    || (c.Note is not null
                                        && c.Note.Contains($"[{phase.DisplayName}", StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }

                foreach (var m in phaseMetrics)
                    AppendMetricLine(sb, m.Id, m.DisplayName, m.ScorePercent);
            }
        }
        else
        {
            sb.AppendLine();
            sb.AppendLine("Metrics");
            foreach (var m in criteria)
                AppendMetricLine(sb, m.Id, m.DisplayName, m.ScorePercent);
        }

        if (gearUpPenaltyApplied)
        {
            sb.AppendLine();
            sb.AppendLine("GEAR UP (hard penalty)");
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>Rebuild from stored highscore fields.</summary>
    public static string FormatFromStored(
        double scorePercent,
        string grade,
        double? scoreBeforeGates,
        bool gearUpPenalty,
        IReadOnlyList<HighscorePhaseDetail>? phases,
        IEnumerable<StoredMetric> metrics)
    {
        var phaseScores = (phases ?? Array.Empty<HighscorePhaseDetail>())
            .Select(p => new PhaseScore
            {
                PhaseId = p.PhaseId,
                DisplayName = p.DisplayName,
                WeightPercent = p.WeightPercent,
                ScorePercent = p.ScorePercent,
                Used = p.Used
            })
            .ToList();

        var criteria = metrics.Select(m => new CriterionScore
        {
            Id = m.Id,
            DisplayName = m.DisplayName,
            Weight = m.Weight,
            Score01 = m.ScorePercent / 100.0,
            Applied = m.Applied,
            PhaseId = m.PhaseId,
            PhaseDisplayName = m.PhaseDisplayName,
            Note = m.Note
        }).ToList();

        return Format(
            scorePercent,
            grade,
            scoreBeforeGates ?? scorePercent,
            gearUpPenalty,
            phaseScores,
            criteria);
    }

    private static void AppendMetricLine(StringBuilder sb, string id, string displayName, double scorePercent)
    {
        sb.Append('-').Append(ShortName(id, displayName)).Append(' ').Append(Pct(scorePercent));
        sb.AppendLine();
    }

    /// <summary>Compact labels for the hierarchy.</summary>
    public static string ShortName(string id, string displayName) => id switch
    {
        "touchdown_vs" => "vSpeed",
        "airspeed" => "airspeed",
        "centerline" => "centerline",
        "ground_track" => "groundTrack",
        "excess_speed" => "excessSpeed",
        "bank" => "bank",
        "alignment" => "alignment",
        "flaps" => "flaps",
        "approach_path" => "steadiness",
        "post_td_alignment" => "heading",
        "rollout_path" => "centerline",
        "rollout_weave" => "weave",
        "max_centerline" => "maxCenterline",
        "gear" => "gear",
        _ when !string.IsNullOrWhiteSpace(displayName) => displayName,
        _ => id
    };

    public static string Pct(double percent)
    {
        var rounded = Math.Round(percent, 1);
        if (Math.Abs(rounded - Math.Round(rounded)) < 0.05)
            return $"{Math.Round(rounded):0}%";
        return $"{rounded:0.0}%";
    }

    public sealed class StoredMetric
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public double Weight { get; init; }
        public double ScorePercent { get; init; }
        public bool Applied { get; init; } = true;
        public string? PhaseId { get; init; }
        public string? PhaseDisplayName { get; init; }
        public string? Note { get; init; }
    }
}

public sealed class HighscorePhaseDetail
{
    public string PhaseId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double WeightPercent { get; set; }
    public double ScorePercent { get; set; }
    public bool Used { get; set; } = true;
}
