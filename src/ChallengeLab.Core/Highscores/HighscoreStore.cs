using System.Text.Json;
using System.Text.Json.Serialization;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Highscores;

public sealed record ScoreHistoryPoint(double ElapsedSeconds, double ScorePercent);

public sealed class HighscoreCriterionDetail
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public double Weight { get; set; }

    public double? ScorePercent { get; set; }
    public double? RawValue { get; set; }
    public string? Unit { get; set; }
    public string? Note { get; set; }

    [JsonPropertyName("Applied")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyApplied { get; set; }

    public MetricStatus? Status { get; set; }
    public string? UnavailableReason { get; set; }
    public double? AppliedMultiplier { get; set; }
    public string? PhaseId { get; set; }
    public string? PhaseDisplayName { get; set; }
    public double PhaseImportancePercent { get; set; }
    public double PhaseWeightPercent { get; set; }
    public double MaxOverallPoints { get; set; }

    [JsonIgnore]
    public MetricStatus EffectiveStatus => Status ?? (LegacyApplied switch
    {
        false => MetricStatus.Informational,
        _ when ScorePercent is null => MetricStatus.Unavailable,
        _ => MetricStatus.Scored
    });

    [JsonIgnore]
    public bool Applied => EffectiveStatus is MetricStatus.Scored or MetricStatus.Degraded or MetricStatus.Assumed;
}

public sealed class HighscoreEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Utc { get; set; }
    public string ChallengeId { get; set; } = "";
    public string ChallengeTitle { get; set; } = "";

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Level { get; set; }

    public double ScorePercent { get; set; }
    public string Grade { get; set; } = "";
    public string? Notes { get; set; }
    public double? ScoreBeforeGatesPercent { get; set; }
    public bool GearUpPenaltyApplied { get; set; }
    public bool FlapsPenaltyApplied { get; set; }
    public List<HighscorePhaseDetail> Phases { get; set; } = new();
    public double? VerticalSpeedFpm { get; set; }
    public List<HighscoreCriterionDetail> Criteria { get; set; } = new();
    public string? EvaluationKeyId { get; set; }
    public int? EvaluationKeyVersion { get; set; }
    public string? ScoringProfileHash { get; set; }
    public string? RankedBucketId { get; set; }
    public LandingResultDiagnostics? Diagnostics { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public LandingVisualizationData? LandingVisualization { get; set; }

    /// <summary>Runway length used for scoring (metres). Stored so the report can show it without re-resolving facilities.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? RunwayLengthMeters { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ScoreHistoryPoint>? ProjectedScoreHistory { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? CareerStageNumber { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CareerRankId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CareerRankTitle { get; set; }

    [JsonIgnore]
    public string CareerDisplay => CareerStageNumber is null || string.IsNullOrWhiteSpace(CareerRankTitle)
        ? ""
        : $"Career {CareerStageNumber} · {CareerRankTitle}";

    [JsonIgnore]
    public bool IsLegacy => string.IsNullOrWhiteSpace(RankedBucketId)
                            || !string.IsNullOrWhiteSpace(Level)
                            || Phases.Count == 0
                            || !Criteria.Any(c => !string.IsNullOrWhiteSpace(c.PhaseId));

    [JsonIgnore]
    public string Breakdown
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Notes)
                && Notes.Contains("Total Grade", StringComparison.OrdinalIgnoreCase))
                return Notes.TrimEnd() + Environment.NewLine;

            if (Criteria.Count == 0 && Phases.Count == 0)
                return $"Total Grade {Grade}  {ScoreBreakdownFormatter.Pct(ScorePercent)}{Environment.NewLine}";

            return ScoreBreakdownFormatter.FormatFromStored(
                ScorePercent,
                Grade,
                ScoreBeforeGatesPercent,
                GearUpPenaltyApplied,
                FlapsPenaltyApplied,
                Phases,
                Criteria.Select(c => new ScoreBreakdownFormatter.StoredMetric
                {
                    Id = c.Id,
                    DisplayName = c.DisplayName,
                    ScorePercent = c.ScorePercent,
                    Status = c.EffectiveStatus,
                    PhaseId = c.PhaseId,
                    PhaseDisplayName = c.PhaseDisplayName,
                    PhaseImportancePercent = c.PhaseImportancePercent,
                    PhaseWeightPercent = c.PhaseWeightPercent,
                    MaxOverallPoints = c.MaxOverallPoints,
                    AppliedMultiplier = c.AppliedMultiplier,
                    Note = c.Note,
                    UnavailableReason = c.UnavailableReason
                }));
        }
    }

    [JsonIgnore]
    public bool HasDetail => Criteria is { Count: > 0 };

    [JsonIgnore]
    public bool HasProjectedScoreHistory => ProjectedScoreHistory is { Count: > 0 };

    [JsonIgnore]
    public string VerticalSpeedDisplay
    {
        get
        {
            var verticalSpeed = ResolveVerticalSpeedFpm();
            return verticalSpeed is null ? "—" : $"{verticalSpeed.Value:0}";
        }
    }

    public double? ResolveVerticalSpeedFpm()
    {
        if (Diagnostics is not null)
            return Diagnostics.TouchdownSinkRateFpm != 0
                ? Diagnostics.TouchdownSinkRateFpm
                : Diagnostics.TouchdownVerticalSpeedFpm;
        if (VerticalSpeedFpm is not null) return VerticalSpeedFpm;
        var verticalSpeed = Criteria.FirstOrDefault(IsVerticalSpeedCriterion);
        return verticalSpeed?.RawValue;
    }

    [JsonIgnore]
    public string EffectiveRankedBucketId =>
        string.IsNullOrWhiteSpace(RankedBucketId) ? "legacy|unknown-scoring-profile" : RankedBucketId;

    [JsonIgnore]
    public string ScoringProfileDisplay => EvaluationKeyVersion is null
        ? "Legacy"
        : $"v{EvaluationKeyVersion} · {ScoringProfileHash ?? "unknown"}";

    [JsonIgnore]
    public IReadOnlyList<HighscoreCriterionDetail> CriteriaForReport
    {
        get
        {
            if (Criteria.Count == 0) return Criteria;
            var verticalSpeed = Criteria.Where(c => c.Id == "touchdown_impact").ToList();
            if (verticalSpeed.Count == 0)
                verticalSpeed = Criteria.Where(IsVerticalSpeedCriterion).ToList();
            var rest = Criteria.Where(c => c.Id != "touchdown_impact" && !IsVerticalSpeedCriterion(c))
                .OrderBy(c => c.EffectiveStatus == MetricStatus.Unavailable)
                .ThenByDescending(c => c.MaxOverallPoints > 0 ? c.MaxOverallPoints : c.Weight)
                .ThenBy(c => c.DisplayName)
                .ToList();
            return verticalSpeed.Concat(rest).ToList();
        }
    }

    private static bool IsVerticalSpeedCriterion(HighscoreCriterionDetail criterion) =>
        criterion.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
        criterion.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase) ||
        (criterion.Unit?.Equals("fpm", StringComparison.OrdinalIgnoreCase) == true &&
         criterion.DisplayName.Contains("Touchdown", StringComparison.OrdinalIgnoreCase));
}

public sealed class HighscoreStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<HighscoreEntry> _entries = new();

    public HighscoreStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "highscores.json");
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        Load();
    }

    public string FilePath => _filePath;

    public IReadOnlyList<HighscoreEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.OrderByDescending(entry => entry.Utc).ToList();
        }
    }

    public void Add(
        ScoreResult result,
        int? careerStageNumber = null,
        string? careerRankId = null,
        string? careerRankTitle = null,
        IReadOnlyList<ScoreHistoryPoint>? projectedScoreHistory = null)
    {
        if (!result.IsRanked || result.ScorePercent is null)
            throw new InvalidOperationException("Unranked results cannot be saved as highscores.");

        var criteria = result.Criteria.Select(criterion => new HighscoreCriterionDetail
        {
            Id = criterion.Id,
            DisplayName = criterion.DisplayName,
            ScorePercent = criterion.ScorePercent is null ? null : Math.Round(criterion.ScorePercent.Value, 1),
            RawValue = criterion.RawValue,
            Unit = criterion.Unit,
            Note = criterion.Note,
            Status = criterion.Status,
            UnavailableReason = criterion.UnavailableReason,
            AppliedMultiplier = criterion.AppliedMultiplier,
            PhaseId = criterion.PhaseId,
            PhaseDisplayName = criterion.PhaseDisplayName,
            PhaseImportancePercent = criterion.PhaseImportancePercent,
            PhaseWeightPercent = criterion.PhaseWeightPercent,
            MaxOverallPoints = criterion.MaxOverallPoints
        }).ToList();

        var phases = result.PhaseScores.Select(phase => new HighscorePhaseDetail
        {
            PhaseId = phase.PhaseId,
            DisplayName = phase.DisplayName,
            WeightPercent = phase.WeightPercent,
            ScorePercent = phase.ScorePercent,
            Used = phase.IsComplete
        }).ToList();

        var verticalSpeed = result.Diagnostics.TouchdownSinkRateFpm != 0
            ? result.Diagnostics.TouchdownSinkRateFpm
            : result.Diagnostics.TouchdownVerticalSpeedFpm;
        if (Math.Abs(verticalSpeed) < 0.001)
            verticalSpeed = criteria.FirstOrDefault(criterion =>
                criterion.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
                criterion.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase))
            ?.RawValue ?? 0;

        var entry = new HighscoreEntry
        {
            Id = Guid.NewGuid(),
            Utc = result.ScoredAtUtc,
            ChallengeId = result.ChallengeId,
            ChallengeTitle = result.ChallengeTitle,
            ScorePercent = result.ScorePercent.Value,
            Grade = result.Grade,
            Notes = result.Summary,
            ScoreBeforeGatesPercent = result.ScoreBeforeGatesPercent,
            GearUpPenaltyApplied = result.GearUpPenaltyApplied,
            FlapsPenaltyApplied = result.FlapsPenaltyApplied,
            Phases = phases,
            VerticalSpeedFpm = verticalSpeed,
            Criteria = criteria,
            EvaluationKeyId = result.EvaluationKeyId,
            EvaluationKeyVersion = result.EvaluationKeyVersion,
            ScoringProfileHash = result.ScoringProfileHash,
            RankedBucketId = result.RankedBucketId,
            Diagnostics = result.Diagnostics,
            LandingVisualization = result.LandingVisualization,
            RunwayLengthMeters = ResolveRunwayLengthMeters(result),
            ProjectedScoreHistory = NormalizeScoreHistory(projectedScoreHistory),
            CareerStageNumber = careerStageNumber,
            CareerRankId = careerRankId,
            CareerRankTitle = careerRankTitle
        };

        lock (_lock)
        {
            _entries.Add(entry);
            Save();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Save();
        }
    }

    public void RewriteClean()
    {
        lock (_lock) Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            _entries = JsonSerializer.Deserialize<List<HighscoreEntry>>(json, JsonOptions) ?? new();
            foreach (var entry in _entries)
            {
                if (entry.Id == Guid.Empty) entry.Id = Guid.NewGuid();
                entry.Criteria ??= new List<HighscoreCriterionDetail>();
                entry.Phases ??= new List<HighscorePhaseDetail>();
                entry.ProjectedScoreHistory = NormalizeScoreHistory(entry.ProjectedScoreHistory);

                if (entry.Criteria.Count == 0 && entry.VerticalSpeedFpm is not null)
                {
                    entry.Criteria.Add(new HighscoreCriterionDetail
                    {
                        Id = "touchdown_vs",
                        DisplayName = "Touchdown firmness",
                        ScorePercent = null,
                        RawValue = entry.VerticalSpeedFpm,
                        Unit = "fpm",
                        Status = MetricStatus.Unavailable,
                        UnavailableReason = "This historical attempt did not store the metric score.",
                        Note = "Historical recovery: vertical speed was stored, but its score and phase breakdown are unavailable."
                    });
                }

                // Prefer explicit field; recover from gate diagnostics when older saves only latched length there.
                entry.RunwayLengthMeters ??= ResolveRunwayLengthMetersFromVisualization(entry.LandingVisualization)
                                               ?? ResolveRunwayLengthMetersFromDiagnostics(entry.Diagnostics);
            }
        }
        catch
        {
            _entries = new();
        }
    }

    private static double? ResolveRunwayLengthMeters(ScoreResult result)
        => ResolveRunwayLengthMetersFromVisualization(result.LandingVisualization)
           ?? ResolveRunwayLengthMetersFromDiagnostics(result.Diagnostics);

    private static double? ResolveRunwayLengthMetersFromVisualization(LandingVisualizationData? visualization)
    {
        var length = visualization?.RunwayLengthM;
        return length is > 0 && double.IsFinite(length.Value) ? length : null;
    }

    private static double? ResolveRunwayLengthMetersFromDiagnostics(LandingResultDiagnostics? diagnostics)
    {
        var length = diagnostics?.OperationalGates?.RunwayLengthMeters;
        return length is > 0 && double.IsFinite(length.Value) ? length : null;
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }

    private static List<ScoreHistoryPoint>? NormalizeScoreHistory(
        IReadOnlyList<ScoreHistoryPoint>? points)
    {
        if (points is null || points.Count == 0) return null;

        var ordered = points
            .Where(point => double.IsFinite(point.ElapsedSeconds) && double.IsFinite(point.ScorePercent))
            .Select(point => new ScoreHistoryPoint(
                Math.Round(Math.Max(0, point.ElapsedSeconds), 2),
                Math.Round(Math.Clamp(point.ScorePercent, 0, 100), 1)))
            .OrderBy(point => point.ElapsedSeconds)
            .ToList();
        if (ordered.Count == 0) return null;

        var sampled = new List<ScoreHistoryPoint>(Math.Min(ordered.Count, 601));
        foreach (var point in ordered)
        {
            if (sampled.Count == 0 || point.ElapsedSeconds - sampled[^1].ElapsedSeconds >= 1.0)
                sampled.Add(point);
        }

        var final = ordered[^1];
        if (Math.Abs(sampled[^1].ElapsedSeconds - final.ElapsedSeconds) < .001)
            sampled[^1] = final;
        else
            sampled.Add(final);

        const int maximumPoints = 600;
        if (sampled.Count <= maximumPoints) return sampled;

        var thinned = new List<ScoreHistoryPoint>(maximumPoints);
        for (var index = 0; index < maximumPoints; index++)
        {
            var sourceIndex = (int)Math.Round(
                index * (sampled.Count - 1.0) / (maximumPoints - 1.0),
                MidpointRounding.AwayFromZero);
            thinned.Add(sampled[sourceIndex]);
        }

        thinned[^1] = final;
        return thinned;
    }
}
