using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.ViewModels;

public sealed class LandingReportViewModel : ViewModelBase
{
    public LandingReportViewModel(HighscoreEntry entry)
    {
        Entry = entry;
        Title = entry.ChallengeTitle;
        Subtitle = $"{entry.Utc:yyyy-MM-dd HH:mm} UTC" +
                   (entry.IsLegacy ? "  ·  LEGACY RECORD" : "");
        RunwayLengthMeters = ResolveRunwayLengthMeters(entry);
        RunwayLengthDisplay = FormatRunwayLength(RunwayLengthMeters);
        HasRunwayLength = !string.IsNullOrEmpty(RunwayLengthDisplay);
        ScorePercent = entry.ScorePercent;
        Grade = entry.Grade;
        Notes = entry.Breakdown;
        CriteriaStored = entry.Criteria?.Count ?? 0;
        ProjectedScoreHistory = entry.ProjectedScoreHistory is { } history
            ? history
            : Array.Empty<ScoreHistoryPoint>();
        HasProjectedScoreHistory = ProjectedScoreHistory.Count > 0;

        Phases = new ObservableCollection<ReportPhaseViewModel>(
            entry.Phases
                .Where(phase => phase.Used)
                .OrderBy(phase => PhaseOrder(phase.PhaseId))
                .ThenBy(phase => phase.DisplayName)
                .Select(phase => new ReportPhaseViewModel(phase)));

        VerticalSpeedRaw = entry.ResolveVerticalSpeedFpm();
        VerticalSpeedDisplay = FormatVerticalSpeedDisplay(VerticalSpeedRaw);
        var firmness = entry.Criteria?.FirstOrDefault(c => c.Id == "touchdown_impact")
                       ?? entry.Criteria?.FirstOrDefault(IsVs);
        VerticalSpeedScorePercent = entry.Diagnostics?.TouchdownImpactScore ?? firmness?.ScorePercent;
        VerticalSpeedExplanation = EnrichNote(firmness)
            ?? (VerticalSpeedRaw is null
                ? "Vertical speed at main-gear touchdown was not recorded for this attempt."
                : MetricExplanations.DefaultCatalog("touchdown_vs", "Touchdown firmness") +
                  $" Measured: {FormatVerticalSpeedDisplay(VerticalSpeedRaw)}. Historical score unavailable.");

        var airspeed = entry.Criteria?.FirstOrDefault(IsAirspeed);
        AirspeedRawKts = airspeed?.RawValue;
        AirspeedScorePercent = airspeed?.ScorePercent;
        AirspeedTargetKts = TryParseTargetKts(airspeed?.Note);
        AirspeedVappKts = TryParseVappKts(airspeed?.Note);
        AirspeedErrorKts = AirspeedRawKts is not null && AirspeedTargetKts is not null
            ? AirspeedRawKts - AirspeedTargetKts
            : TryParseErrorKts(airspeed?.Note);
        AirspeedDisplay = FormatAirspeedDisplay(AirspeedRawKts, AirspeedTargetKts, AirspeedVappKts, AirspeedErrorKts);
        AirspeedExplanation = EnrichNote(airspeed)
            ?? (AirspeedRawKts is null
                ? "Touchdown airspeed was not recorded for this attempt."
                : MetricExplanations.DefaultCatalog("airspeed", "IAS versus touchdown target") +
                  $" Measured: {AirspeedDisplay}.");

        Metrics = new ObservableCollection<ReportMetricViewModel>();
        DetailMetrics = new ObservableCollection<ReportMetricViewModel>();
        Penalties = new ObservableCollection<PenaltyViewModel>();
        var source = entry.CriteriaForReport;
        if (source.Count == 0 && entry.Criteria is { Count: > 0 }) source = entry.Criteria;
        foreach (var criterion in source)
        {
            if (criterion.EffectiveStatus == MetricStatus.GateFailed
                || criterion.EffectiveStatus == MetricStatus.Assumed
                && criterion.AppliedMultiplier is < 1)
            {
                Penalties.Add(new PenaltyViewModel(criterion));
                continue;
            }

            if (!ShouldShowAsMetric(criterion)) continue;
            var isVs = IsVs(criterion);
            var isAirspeed = IsAirspeed(criterion);
            var metric = new ReportMetricViewModel(criterion, isVs);
            Metrics.Add(metric);
            if (!isVs && !isAirspeed)
                DetailMetrics.Add(metric);
        }

        MetricCount = Metrics.Count;
        HasDetail = MetricCount > 0;
        HasPenalties = Penalties.Count > 0;
        PenaltySummary = BuildPenaltySummary(entry, Penalties.Count);
        DetailHint = HasDetail
            ? $"METRICS: {MetricCount} (stored criteria: {CriteriaStored}) — scroll for every result and explanation"
            : $"NO METRIC BREAKDOWN (stored criteria: {CriteriaStored}). This attempt predates metric storage.";
    }

    public HighscoreEntry Entry { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public double? RunwayLengthMeters { get; }
    public string RunwayLengthDisplay { get; }
    public bool HasRunwayLength { get; }
    public double ScorePercent { get; }
    public string Grade { get; }
    public string Notes { get; }
    public IReadOnlyList<ScoreHistoryPoint> ProjectedScoreHistory { get; }
    public bool HasProjectedScoreHistory { get; }
    public string ProjectedScoreHistoryUnavailableText =>
        "Projected score history was not stored for this landing.";
    public bool HasDetail { get; }
    public int MetricCount { get; }
    public int CriteriaStored { get; }
    public string DetailHint { get; }
    public double? VerticalSpeedRaw { get; }
    public string VerticalSpeedDisplay { get; }
    public double? VerticalSpeedScorePercent { get; }
    public string VerticalSpeedExplanation { get; }
    public string VerticalSpeedHeadline => VerticalSpeedRaw is null
        ? "Vertical speed at touchdown: not recorded"
        : Entry.Diagnostics is { } d
            ? $"Impact: {VerticalSpeedRaw:0} fpm  ·  robust peak {d.TouchdownRobustPeakG:0.00} g"
            : $"Vertical speed: {VerticalSpeedRaw:0} fpm  ·  absolute sink {Math.Abs(VerticalSpeedRaw.Value):0} fpm";
    public string VerticalSpeedScoreLabel => VerticalSpeedScorePercent is null
        ? "Score: N/A"
        : $"Impact score: {VerticalSpeedScorePercent:0}%";

    public double? AirspeedRawKts { get; }
    public double? AirspeedTargetKts { get; }
    public double? AirspeedVappKts { get; }
    public double? AirspeedErrorKts { get; }
    public double? AirspeedScorePercent { get; }
    public string AirspeedDisplay { get; }
    public string AirspeedExplanation { get; }
    public string AirspeedHeadline => AirspeedRawKts is null
        ? "Landing speed: not recorded"
        : AirspeedTargetKts is null
            ? $"Landing speed: {AirspeedRawKts:0.0} kt IAS"
            : $"Landing speed: {AirspeedRawKts:0.0} kt  vs  optimal {AirspeedTargetKts:0.0} kt" +
              (AirspeedErrorKts is null ? "" : $"  ({Signed(AirspeedErrorKts.Value)} kt)");
    public string AirspeedScoreLabel => AirspeedScorePercent is null
        ? "Score: N/A"
        : $"Metric score: {AirspeedScorePercent:0}%";

    public ObservableCollection<ReportPhaseViewModel> Phases { get; }
    public ObservableCollection<PenaltyViewModel> Penalties { get; }
    public ObservableCollection<ReportMetricViewModel> Metrics { get; }
    public ObservableCollection<ReportMetricViewModel> DetailMetrics { get; }
    public bool HasPenalties { get; }
    public string PenaltySummary { get; }

    internal static string FormatVerticalSpeedDisplay(double? fpm)
    {
        if (fpm is null) return "—";
        var v = fpm.Value;
        return $"{v:0} fpm  (abs {Math.Abs(v):0} fpm sink)";
    }

    internal static string FormatAirspeedDisplay(
        double? ias,
        double? target,
        double? vapp,
        double? error)
    {
        if (ias is null) return "—";
        if (target is null)
            return $"{ias:0.0} kt IAS";
        var delta = error ?? (ias - target);
        var vappPart = vapp is null ? "" : $" · VAPP {vapp:0.0}";
        return $"{ias:0.0} kt IAS vs optimal {target:0.0} kt ({Signed(delta.Value)} kt{vappPart})";
    }

    private static string Signed(double v) => v >= 0 ? $"+{v:0.0}" : $"{v:0.0}";

    private static double? TryParseTargetKts(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        // "target 138.0 kt" or "optimal target 138.0 kt"
        var m = Regex.Match(note, @"(?:optimal\s+)?target\s+(-?\d+(?:\.\d+)?)\s*kt",
            RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    private static double? TryParseVappKts(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var m = Regex.Match(note, @"VAPP\s+(-?\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    private static double? TryParseErrorKts(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var m = Regex.Match(note, @"(?:error|delta)\s*([+\-]?\d+(?:\.\d+)?)\s*kt",
            RegexOptions.IgnoreCase);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v : null;
    }

    private static string? EnrichNote(HighscoreCriterionDetail? criterion)
    {
        if (criterion is null) return null;
        var stored = criterion.Note?.Trim() ?? "";
        var catalog = MetricExplanations.DefaultCatalog(criterion.Id, criterion.DisplayName);
        if (string.IsNullOrWhiteSpace(stored)) return BuildFallbackNote(criterion, catalog);
        if (stored.Length > 80 || stored.Contains("Measured:", StringComparison.OrdinalIgnoreCase)) return stored;
        return BuildFallbackNote(criterion, catalog + " " + stored);
    }

    private static string BuildFallbackNote(HighscoreCriterionDetail criterion, string catalog)
    {
        var measured = criterion.RawValue is null
            ? ""
            : criterion.Unit is null
                ? $" Measured: {criterion.RawValue:0.##}."
                : $" Measured: {criterion.RawValue:0.##} {criterion.Unit}.";
        var score = criterion.ScorePercent is null ? " Score unavailable." : $" Score: {criterion.ScorePercent:0}%.";
        return (catalog + measured + score).Trim();
    }

    private static bool IsVs(HighscoreCriterionDetail criterion) =>
        criterion.Id is "touchdown_impact" or "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
        criterion.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase);

    private static bool IsAirspeed(HighscoreCriterionDetail criterion) =>
        criterion.Id is "airspeed" or "touchdownIasErrorKts" ||
        criterion.DisplayName.Contains("IAS", StringComparison.OrdinalIgnoreCase) ||
        criterion.DisplayName.Contains("airspeed", StringComparison.OrdinalIgnoreCase) ||
        criterion.DisplayName.Contains("touchdown target", StringComparison.OrdinalIgnoreCase);

    private static bool ShouldShowAsMetric(HighscoreCriterionDetail criterion)
    {
        if ((string.Equals(criterion.Id, "gear", StringComparison.OrdinalIgnoreCase)
             || string.Equals(criterion.Id, "flaps", StringComparison.OrdinalIgnoreCase))
            && criterion.EffectiveStatus == MetricStatus.Informational)
            return false;
        return criterion.EffectiveStatus != MetricStatus.Informational;
    }

    private static string BuildPenaltySummary(HighscoreEntry entry, int count)
    {
        var label = $"{count} penalt{(count == 1 ? "y" : "ies")} applied";
        if (entry.ScoreBeforeGatesPercent is not { } before) return label;
        var loss = Math.Max(0, before - entry.ScorePercent);
        return $"{label}  ·  {before:0.0}% before penalties → {entry.ScorePercent:0.0}% final" +
               (loss > .05 ? $"  ·  −{loss:0.0} points" : "");
    }

    private static int PhaseOrder(string? id) => id?.ToLowerInvariant() switch
    {
        "touchdown" => 0,
        "approach" => 1,
        "rollout" => 2,
        _ => 3
    };

    private static double? ResolveRunwayLengthMeters(HighscoreEntry entry)
    {
        if (entry.RunwayLengthMeters is { } stored && stored > 0 && double.IsFinite(stored))
            return stored;
        var fromGates = entry.Diagnostics?.OperationalGates?.RunwayLengthMeters;
        if (fromGates is { } gates && gates > 0 && double.IsFinite(gates))
            return gates;
        return null;
    }

    /// <summary>e.g. "3,353 m · 11,001 ft" for the report header badge.</summary>
    internal static string FormatRunwayLength(double? lengthMeters)
    {
        if (lengthMeters is not { } meters || meters <= 0 || !double.IsFinite(meters))
            return "";
        var feet = meters / RunwayPathGeometry.MetersPerFoot;
        return string.Create(CultureInfo.InvariantCulture,
            $"{meters:#,##0} m · {feet:#,##0} ft");
    }
}

public sealed class ReportPhaseViewModel
{
    public ReportPhaseViewModel(HighscorePhaseDetail phase)
    {
        DisplayName = phase.DisplayName;
        ScorePercent = phase.ScorePercent;
        ScoreDisplay = phase.ScorePercent is null ? "N/A" : $"{phase.ScorePercent:0.0}%";
        WeightDisplay = $"{phase.WeightPercent:0.#}% overall";
    }

    public string DisplayName { get; }
    public double? ScorePercent { get; }
    public string ScoreDisplay { get; }
    public string WeightDisplay { get; }
}

public sealed class PenaltyViewModel
{
    public PenaltyViewModel(HighscoreCriterionDetail criterion)
    {
        Id = criterion.Id;
        DisplayName = criterion.DisplayName;
        var metric = new ReportMetricViewModel(criterion, false);
        RawDisplay = metric.RawDisplay;
        Note = metric.Note;
        ScopeDisplay = string.IsNullOrWhiteSpace(criterion.PhaseDisplayName)
            ? "Overall score penalty"
            : $"{criterion.PhaseDisplayName} penalty";
        MultiplierDisplay = criterion.AppliedMultiplier is { } multiplier
            ? $"x {multiplier:0.###}"
            : "";
    }

    public string Id { get; }
    public string DisplayName { get; }
    public string RawDisplay { get; }
    public string Note { get; }
    public string ScopeDisplay { get; }
    public string MultiplierDisplay { get; }
}

public sealed class ReportMetricViewModel
{
    public ReportMetricViewModel(HighscoreCriterionDetail criterion, bool isPrimary)
    {
        Id = criterion.Id;
        Status = criterion.EffectiveStatus;
        DisplayName = criterion.DisplayName + (Status switch
        {
            MetricStatus.Informational => " (informational)",
            MetricStatus.Unavailable => " (score unavailable)",
            MetricStatus.Assumed => " (assumed fallback)",
            MetricStatus.NotApplicable => " (not applicable)",
            MetricStatus.Degraded => " (degraded telemetry)",
            _ => ""
        });
        ScorePercent = criterion.ScorePercent;
        ScoreDisplay = ScorePercent is null ? "N/A" : $"{ScorePercent:0}%";
        IsPrimary = isPrimary;
        RawDisplay = FormatRawDisplay(criterion);
        InfluenceDisplay = criterion.MaxOverallPoints > 0
            ? $"{criterion.PhaseImportancePercent:0.##}% of {criterion.PhaseDisplayName} · {criterion.MaxOverallPoints:0.##} max overall points"
            : criterion.Weight > 0
                ? $"Legacy weight {criterion.Weight:0.##}"
                : Status switch
                {
                    MetricStatus.Informational => "Informational · no score credit",
                    MetricStatus.GateFailed => "Safety gate",
                    MetricStatus.Unavailable => "Score unavailable",
                    MetricStatus.Assumed => criterion.AppliedMultiplier is { } assumedMultiplier
                        ? $"Assumed telemetry adjustment x {assumedMultiplier:0.###}"
                        : "Assumed 50% metric credit",
                    MetricStatus.NotApplicable => "Not applicable - neutral",
                    MetricStatus.Degraded => "Calculated proxy retained",
                    _ => ""
                };

        var catalog = MetricExplanations.DefaultCatalog(criterion.Id, criterion.DisplayName);
        var stored = criterion.Note?.Trim() ?? "";
        Note = string.IsNullOrWhiteSpace(stored)
            ? catalog + (criterion.RawValue is null ? "" : $" Measured: {RawDisplay}.") +
              (ScorePercent is null ? " Score unavailable." : $" Score: {ScorePercent:0}%.")
            : stored;
        Verdict = Status switch
        {
            MetricStatus.Unavailable => "N/A",
            MetricStatus.Informational => "INFO",
            MetricStatus.GateFailed => "FAILED GATE",
            MetricStatus.Assumed => "ASSUMED",
            MetricStatus.NotApplicable => "N/A",
            MetricStatus.Degraded => "DEGRADED",
            _ => ScorePercent switch
            {
                >= 95 => "Excellent",
                >= 85 => "Very good",
                >= 70 => "Good",
                >= 55 => "Acceptable",
                >= 40 => "Weak",
                > 0 => "Poor",
                _ => "Failed"
            }
        };
    }

    public string Id { get; }
    public string DisplayName { get; }
    public double? ScorePercent { get; }
    public string ScoreDisplay { get; }
    public string RawDisplay { get; }
    public string Note { get; }
    public string Verdict { get; }
    public string InfluenceDisplay { get; }
    public bool IsPrimary { get; }
    public MetricStatus Status { get; }
    public bool IsScored => Status is MetricStatus.Scored or MetricStatus.Degraded
                            || Status == MetricStatus.Assumed && ScorePercent is not null;
    public double BarValue => ScorePercent ?? 0;

    private static string FormatRawDisplay(HighscoreCriterionDetail criterion)
    {
        if (criterion.RawValue is null) return "—";

        var id = criterion.Id ?? "";
        if (id == "touchdown_point")
        {
            var target = TryParseFromNote(criterion.Note, @"perfect\s+point\s+(-?\d+(?:\.\d+)?)\s*ft");
            var errorMatch = Regex.Match(
                criterion.Note ?? "",
                @"error\s+([+\-]?\d+(?:\.\d+)?)\s*ft\s+\((early|late|on\s+target)\)",
                RegexOptions.IgnoreCase);
            var error = errorMatch.Success ? errorMatch.Groups[1].Value : null;
            var direction = errorMatch.Success ? errorMatch.Groups[2].Value.ToLowerInvariant() : null;
            return target is null || error is null || direction is null
                ? $"{criterion.RawValue:0.0} ft from threshold"
                : $"{criterion.RawValue:0.0} ft from threshold · target {target:0.0} ft · error {error} ft ({direction})";
        }

        if (id is "touchdown_vs" or "touchdownVerticalSpeedFpm"
            || criterion.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase)
            || (criterion.Unit?.Equals("fpm", StringComparison.OrdinalIgnoreCase) == true
                && criterion.DisplayName.Contains("Vertical", StringComparison.OrdinalIgnoreCase)))
        {
            return LandingReportViewModel.FormatVerticalSpeedDisplay(criterion.RawValue);
        }

        if (id is "airspeed" or "touchdownIasErrorKts"
            || criterion.DisplayName.Contains("IAS", StringComparison.OrdinalIgnoreCase)
            || criterion.DisplayName.Contains("airspeed", StringComparison.OrdinalIgnoreCase)
            || criterion.DisplayName.Contains("touchdown target", StringComparison.OrdinalIgnoreCase))
        {
            var target = TryParseFromNote(criterion.Note, @"(?:optimal\s+)?target\s+(-?\d+(?:\.\d+)?)\s*kt");
            var vapp = TryParseFromNote(criterion.Note, @"VAPP\s+(-?\d+(?:\.\d+)?)");
            var error = TryParseFromNote(criterion.Note, @"(?:error|delta)\s*([+\-]?\d+(?:\.\d+)?)\s*kt");
            return LandingReportViewModel.FormatAirspeedDisplay(criterion.RawValue, target, vapp, error);
        }

        return criterion.Unit is null
            ? $"{criterion.RawValue:0.##}"
            : $"{criterion.RawValue:0.##} {criterion.Unit}";
    }

    private static double? TryParseFromNote(string? note, string pattern)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        var m = Regex.Match(note, pattern, RegexOptions.IgnoreCase);
        return m.Success
               && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }
}
