using System.Text.Json;
using System.Text.Json.Serialization;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Highscores;

public sealed class HighscoreCriterionDetail
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double Weight { get; set; }
    public double ScorePercent { get; set; }
    public double? RawValue { get; set; }
    public string? Unit { get; set; }
    public string? Note { get; set; }
    public bool Applied { get; set; } = true;
    public string? PhaseId { get; set; }
    public string? PhaseDisplayName { get; set; }
    public double ImportancePercent { get; set; }
}

public sealed class HighscoreEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTimeOffset Utc { get; set; }
    public string ChallengeId { get; set; } = "";
    public string ChallengeTitle { get; set; } = "";

    /// <summary>Legacy field from Easy/Strict era; ignored for new entries, kept for old saves.</summary>
    public string Level { get; set; } = "";

    public double ScorePercent { get; set; }
    public string Grade { get; set; } = "";

    /// <summary>Hierarchical result text (Total / phases / metrics).</summary>
    public string? Notes { get; set; }

    public double? ScoreBeforeGatesPercent { get; set; }
    public bool GearUpPenaltyApplied { get; set; }

    /// <summary>Phase totals for rebuild/display.</summary>
    public List<HighscorePhaseDetail> Phases { get; set; } = new();

    /// <summary>Primary metric snapshot (always stored for new entries).</summary>
    public double? VerticalSpeedFpm { get; set; }

    /// <summary>Full per-criterion breakdown (new entries). Older saves may be empty.</summary>
    public List<HighscoreCriterionDetail> Criteria { get; set; } = new();

    /// <summary>Hierarchical breakdown for UI (stored Notes, or rebuilt from criteria/phases).</summary>
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
                    Weight = c.Weight,
                    ScorePercent = c.ScorePercent,
                    Applied = c.Applied,
                    PhaseId = c.PhaseId,
                    PhaseDisplayName = c.PhaseDisplayName,
                    Note = c.Note
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
            var vs = ResolveVerticalSpeedFpm();
            if (vs is null) return "—";
            return $"{vs:0} fpm";
        }
    }

    public double? ResolveVerticalSpeedFpm()
    {
        if (VerticalSpeedFpm is not null) return VerticalSpeedFpm;
        var vs = Criteria.FirstOrDefault(c =>
            c.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
            c.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase) ||
            c.DisplayName.Contains("Vertical", StringComparison.OrdinalIgnoreCase));
        return vs?.RawValue;
    }

    /// <summary>Criteria ordered for the detail report: VS first, then remaining by weight desc.</summary>
    [JsonIgnore]
    public IReadOnlyList<HighscoreCriterionDetail> CriteriaForReport
    {
        get
        {
            if (Criteria.Count == 0) return Criteria;

            var list = Criteria.ToList();
            var vs = list.Where(IsVerticalSpeedCriterion).ToList();
            var rest = list.Where(c => !IsVerticalSpeedCriterion(c))
                .OrderByDescending(c => c.Applied)
                .ThenByDescending(c => c.Weight)
                .ThenBy(c => c.DisplayName)
                .ToList();
            return vs.Concat(rest).ToList();
        }
    }

    private static bool IsVerticalSpeedCriterion(HighscoreCriterionDetail c) =>
        c.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
        c.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase) ||
        (c.Unit?.Equals("fpm", StringComparison.OrdinalIgnoreCase) == true &&
         c.DisplayName.Contains("Touchdown", StringComparison.OrdinalIgnoreCase));
}

public sealed class HighscoreStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // Never persist computed UI helpers (HasDetail, CriteriaForReport, …)
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _filePath;
    private readonly object _lock = new();
    private List<HighscoreEntry> _entries = new();

    public HighscoreStore(string? filePath = null)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab");
        Directory.CreateDirectory(dir);
        _filePath = filePath ?? Path.Combine(dir, "highscores.json");
        Load();
    }

    public string FilePath => _filePath;

    public IReadOnlyList<HighscoreEntry> Entries
    {
        get
        {
            lock (_lock)
                return _entries.OrderByDescending(e => e.Utc).ToList();
        }
    }

    public void Add(ScoreResult result)
    {
        var criteria = result.Criteria.Select(c => new HighscoreCriterionDetail
        {
            Id = c.Id,
            DisplayName = c.DisplayName,
            Weight = c.Weight,
            ScorePercent = Math.Round(c.ScorePercent, 1),
            RawValue = c.RawValue,
            Unit = c.Unit,
            Note = c.Note,
            Applied = c.Applied,
            PhaseId = c.PhaseId,
            PhaseDisplayName = c.PhaseDisplayName,
            ImportancePercent = c.ImportancePercent
        }).ToList();

        var phases = result.PhaseScores.Select(p => new HighscorePhaseDetail
        {
            PhaseId = p.PhaseId,
            DisplayName = p.DisplayName,
            WeightPercent = p.WeightPercent,
            ScorePercent = p.ScorePercent,
            Used = p.Used
        }).ToList();

        var vsRaw = criteria.FirstOrDefault(c =>
                c.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
                c.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase))
            ?.RawValue;

        // Prefer firmness raw; else vertical speed from raw if unit is fpm
        vsRaw ??= criteria.FirstOrDefault(c =>
            string.Equals(c.Unit, "fpm", StringComparison.OrdinalIgnoreCase))?.RawValue;

        var entry = new HighscoreEntry
        {
            Id = Guid.NewGuid(),
            Utc = result.ScoredAtUtc,
            ChallengeId = result.ChallengeId,
            ChallengeTitle = result.ChallengeTitle,
            ScorePercent = result.ScorePercent,
            Grade = result.Grade,
            Notes = result.Summary,
            ScoreBeforeGatesPercent = result.ScoreBeforeGatesPercent,
            GearUpPenaltyApplied = result.GearUpPenaltyApplied,
            Phases = phases,
            VerticalSpeedFpm = vsRaw,
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

    /// <summary>Rewrite the file without computed properties (self-heal older saves).</summary>
    public void RewriteClean()
    {
        lock (_lock)
        {
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            _entries = JsonSerializer.Deserialize<List<HighscoreEntry>>(json, JsonOptions) ?? new();
            foreach (var e in _entries)
            {
                if (e.Id == Guid.Empty)
                    e.Id = Guid.NewGuid();
                e.Criteria ??= new List<HighscoreCriterionDetail>();

                // Self-heal: older files may have empty Criteria but still have VS
                if (e.Criteria.Count == 0 && e.VerticalSpeedFpm is not null)
                {
                    e.Criteria.Add(new HighscoreCriterionDetail
                    {
                        Id = "touchdown_vs",
                        DisplayName = "Touchdown firmness",
                        Weight = 1.6,
                        ScorePercent = 0,
                        RawValue = e.VerticalSpeedFpm,
                        Unit = "fpm",
                        Applied = true,
                        Note = "Partial recovery — only vertical speed was stored for this attempt."
                    });
                }
            }

            // Drop legacy computed fields from disk on next save
            Save();
        }
        catch
        {
            _entries = new();
        }
    }

    private void Save()
    {
        // Serialize only real data (JsonIgnore on computed props)
        var json = JsonSerializer.Serialize(_entries, JsonOptions);
        File.WriteAllText(_filePath, json);
    }
}
