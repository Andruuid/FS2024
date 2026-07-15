using System.Text.Json;
using System.Text.Json.Serialization;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Highscores;

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
    public bool Applied => EffectiveStatus == MetricStatus.Scored;
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
    public List<HighscorePhaseDetail> Phases { get; set; } = new();
    public double? VerticalSpeedFpm { get; set; }
    public List<HighscoreCriterionDetail> Criteria { get; set; } = new();

    [JsonIgnore]
    public bool IsLegacy => !string.IsNullOrWhiteSpace(Level)
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
                    Note = c.Note,
                    UnavailableReason = c.UnavailableReason
                }));
        }
    }

    [JsonIgnore]
    public bool HasDetail => Criteria is { Count: > 0 };

    [JsonIgnore]
    public string VerticalSpeedDisplay
    {
        get
        {
            var verticalSpeed = ResolveVerticalSpeedFpm();
            if (verticalSpeed is null) return "—";
            // Grid column: signed value + absolute sink rate for quick reading.
            return $"{verticalSpeed:0} ({Math.Abs(verticalSpeed.Value):0} abs)";
        }
    }

    public double? ResolveVerticalSpeedFpm()
    {
        if (VerticalSpeedFpm is not null) return VerticalSpeedFpm;
        var verticalSpeed = Criteria.FirstOrDefault(IsVerticalSpeedCriterion);
        return verticalSpeed?.RawValue;
    }

    [JsonIgnore]
    public IReadOnlyList<HighscoreCriterionDetail> CriteriaForReport
    {
        get
        {
            if (Criteria.Count == 0) return Criteria;
            var verticalSpeed = Criteria.Where(IsVerticalSpeedCriterion).ToList();
            var rest = Criteria.Where(c => !IsVerticalSpeedCriterion(c))
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

    public void Add(ScoreResult result)
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

        var verticalSpeed = criteria.FirstOrDefault(criterion =>
                criterion.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
                criterion.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase))
            ?.RawValue;

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
            Phases = phases,
            VerticalSpeedFpm = verticalSpeed,
            Criteria = criteria
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
            }
        }
        catch
        {
            _entries = new();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
