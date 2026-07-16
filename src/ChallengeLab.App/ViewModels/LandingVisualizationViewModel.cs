using System.Text.RegularExpressions;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;

namespace ChallengeLab.App.ViewModels;

public sealed class LandingVisualizationViewModel
{
    private const double FeetPerMeter = 3.280839895;

    public LandingVisualizationViewModel(HighscoreEntry entry, LandingVisualizationData data)
    {
        Entry = entry;
        Data = data;

        RunwayTitle = string.IsNullOrWhiteSpace(data.AirportIcao)
            ? $"RUNWAY {data.RunwayId}"
            : $"{data.AirportIcao.ToUpperInvariant()}  ·  RUNWAY {data.RunwayId.ToUpperInvariant()}";
        RunwayDetail = $"{data.RunwayLengthM:0} m × {data.RunwayWidthM:0} m  ·  " +
                       $"{NormalizeHeading(data.RunwayHeadingTrueDeg):000}° TRUE";

        var actualFeet = data.TouchdownDistanceFromThresholdM * FeetPerMeter;
        var idealFeet = data.IdealTouchdownDistanceFromThresholdM * FeetPerMeter;
        var errorFeet = actualFeet - idealFeet;
        PositionHeadline = $"{actualFeet:0} FT FROM THRESHOLD";
        PositionDetail = $"Target {idealFeet:0} ft  ·  {FormatEarlyLate(errorFeet)}  ·  " +
                         FormatLateral(data.TouchdownLateralOffsetM);

        var impact = FindCriterion(entry, "touchdown_impact")
                     ?? entry.Criteria.FirstOrDefault(IsVerticalSpeedCriterion);
        var bank = FindCriterion(entry, "bank");
        var airspeed = FindCriterion(entry, "airspeed", "touchdownIasErrorKts");

        VerticalSpeed = new LandingMetricTileViewModel(
            "VERTICAL SPEED",
            $"{data.TouchdownVerticalSpeedFpm:0}",
            "FPM",
            MetricResult(impact));
        PeakG = new LandingMetricTileViewModel(
            "ROBUST PEAK G",
            $"{data.TouchdownRobustPeakG:0.00}",
            "G",
            MetricResult(impact));
        Bank = new LandingMetricTileViewModel(
            "BANK ANGLE",
            $"{Math.Abs(data.TouchdownBankDeg):0.0}°",
            Direction(data.TouchdownBankDeg, "LEFT", "RIGHT", "LEVEL"),
            MetricResult(bank));

        var airspeedDelta = data.TargetTouchdownAirspeedKts > 0
            ? data.TouchdownAirspeedKts - data.TargetTouchdownAirspeedKts
            : (double?)null;
        Airspeed = new LandingMetricTileViewModel(
            "TOUCHDOWN IAS",
            $"{data.TouchdownAirspeedKts:0}",
            airspeedDelta is null
                ? "KT"
                : $"KT  ·  {(airspeedDelta >= 0 ? "+" : "")}{airspeedDelta:0} VS TARGET",
            MetricResult(airspeed));

        VerdictHeadline = $"{entry.Grade}  ·  {VerdictFor(entry.ScorePercent)}";
        VerdictSummary =
            $"Touchdown was {FormatEarlyLate(errorFeet).ToLowerInvariant()} and " +
            $"{FormatLateral(data.TouchdownLateralOffsetM).ToLowerInvariant()}. " +
            $"The main-gear impact was {data.TouchdownVerticalSpeedFpm:0} fpm at " +
            $"{data.TouchdownRobustPeakG:0.00} g, contributing to a {entry.ScorePercent:0.0}% final score.";

        var relevant = entry.Criteria
            .Where(IsCoachingCriterion)
            .ToList();
        var strength = relevant
            .Where(c => c.EffectiveStatus is MetricStatus.Scored or MetricStatus.Degraded
                        && c.ScorePercent is not null)
            .OrderByDescending(c => c.ScorePercent)
            .ThenByDescending(c => c.MaxOverallPoints)
            .FirstOrDefault();
        var failedGate = entry.Criteria
            .Where(c => c.EffectiveStatus == MetricStatus.GateFailed)
            .OrderByDescending(c => c.MaxOverallPoints)
            .FirstOrDefault();
        var improvement = failedGate ?? relevant
            .Where(c => c.EffectiveStatus is MetricStatus.Scored or MetricStatus.Degraded
                        && c.ScorePercent is < 95)
            .OrderBy(c => c.ScorePercent)
            .ThenByDescending(c => c.MaxOverallPoints)
            .FirstOrDefault();

        StrengthTitle = strength is null ? "Recorded result" : strength.DisplayName;
        StrengthBody = strength is null
            ? "The visual uses the exact touchdown values stored with this result."
            : BuildInsight(strength);

        ImprovementTitle = improvement is null ? "No major weakness detected" : improvement.DisplayName;
        ImprovementBody = improvement is null
            ? "Every available touchdown metric scored at least 95% and no landing gate failed. Repeat this consistency."
            : BuildInsight(improvement);
    }

    public HighscoreEntry Entry { get; }
    public LandingVisualizationData Data { get; }
    public string RunwayTitle { get; }
    public string RunwayDetail { get; }
    public string PositionHeadline { get; }
    public string PositionDetail { get; }
    public LandingMetricTileViewModel VerticalSpeed { get; }
    public LandingMetricTileViewModel PeakG { get; }
    public LandingMetricTileViewModel Bank { get; }
    public LandingMetricTileViewModel Airspeed { get; }
    public string VerdictHeadline { get; }
    public string VerdictSummary { get; }
    public string StrengthTitle { get; }
    public string StrengthBody { get; }
    public string ImprovementTitle { get; }
    public string ImprovementBody { get; }

    private static HighscoreCriterionDetail? FindCriterion(
        HighscoreEntry entry,
        params string[] ids) => entry.Criteria.FirstOrDefault(c =>
        ids.Contains(c.Id, StringComparer.OrdinalIgnoreCase));

    private static bool IsVerticalSpeedCriterion(HighscoreCriterionDetail criterion) =>
        criterion.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
        criterion.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase);

    private static bool IsCoachingCriterion(HighscoreCriterionDetail criterion)
    {
        if (criterion.EffectiveStatus is MetricStatus.Informational
            or MetricStatus.NotApplicable
            or MetricStatus.Assumed
            or MetricStatus.Unavailable)
            return false;

        if (criterion.EffectiveStatus == MetricStatus.GateFailed)
            return true;

        return string.Equals(criterion.PhaseId, "touchdown", StringComparison.OrdinalIgnoreCase)
               || criterion.Id is "touchdown_impact" or "touchdown_point" or "flare_efficiency"
                   or "contact_stability" or "centerline" or "alignment" or "airspeed" or "bank";
    }

    private static string MetricResult(HighscoreCriterionDetail? criterion)
    {
        if (criterion?.ScorePercent is not { } score)
            return "RECORDED";
        return $"{score:0}%  ·  {VerdictFor(score).ToUpperInvariant()}";
    }

    private static string BuildInsight(HighscoreCriterionDetail criterion)
    {
        var prefix = criterion.EffectiveStatus == MetricStatus.GateFailed
            ? "FAILED GATE"
            : criterion.ScorePercent is { } score
                ? $"{score:0}%"
                : "RECORDED";
        var note = CleanNote(criterion.Note);
        if (string.IsNullOrWhiteSpace(note))
        {
            note = criterion.RawValue is { } raw
                ? $"Measured {raw:0.##}{(string.IsNullOrWhiteSpace(criterion.Unit) ? "" : " " + criterion.Unit)}."
                : "See the detailed report for the recorded scoring evidence.";
        }

        return $"{prefix}  ·  {note}";
    }

    private static string CleanNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return "";
        var clean = Regex.Replace(note.Trim(), @"^\[[^\]]+\]\s*", "");
        var sentences = Regex.Split(clean, @"(?<=[.!?])\s+")
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .Take(2);
        return string.Join(" ", sentences);
    }

    private static string FormatEarlyLate(double errorFeet)
    {
        if (Math.Abs(errorFeet) < .5) return "ON TARGET";
        return $"{Math.Abs(errorFeet):0} FT {(errorFeet < 0 ? "EARLY" : "LATE")}";
    }

    private static string FormatLateral(double offsetMeters)
    {
        if (Math.Abs(offsetMeters) < .05) return "ON CENTERLINE";
        return $"{Math.Abs(offsetMeters):0.0} M {(offsetMeters < 0 ? "LEFT" : "RIGHT")} OF CENTERLINE";
    }

    private static string Direction(double value, string negative, string positive, string neutral)
    {
        if (Math.Abs(value) < .05) return neutral;
        return value < 0 ? negative : positive;
    }

    private static string VerdictFor(double score) => score switch
    {
        >= 95 => "Exceptional landing",
        >= 85 => "Excellent landing",
        >= 70 => "Strong landing",
        >= 55 => "Acceptable landing",
        >= 40 => "Difficult landing",
        _ => "Landing needs work"
    };

    private static double NormalizeHeading(double heading)
    {
        heading %= 360;
        return heading < 0 ? heading + 360 : heading;
    }
}

public sealed class LandingMetricTileViewModel
{
    public LandingMetricTileViewModel(string label, string value, string unitDetail, string result)
    {
        Label = label;
        Value = value;
        UnitDetail = unitDetail;
        Result = result;
    }

    public string Label { get; }
    public string Value { get; }
    public string UnitDetail { get; }
    public string Result { get; }
}
