using System.Text.Json;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Highscores;

public sealed class HighscoreEntry
{
    public DateTimeOffset Utc { get; set; }
    public string ChallengeId { get; set; } = "";
    public string ChallengeTitle { get; set; } = "";
    public string Level { get; set; } = "";
    public double ScorePercent { get; set; }
    public string Grade { get; set; } = "";
    public string? Notes { get; set; }
}

public sealed class HighscoreStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
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
        var entry = new HighscoreEntry
        {
            Utc = result.ScoredAtUtc,
            ChallengeId = result.ChallengeId,
            ChallengeTitle = result.ChallengeTitle,
            Level = result.Level.ToDisplayName(),
            ScorePercent = result.ScorePercent,
            Grade = result.Grade,
            Notes = result.Summary
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

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var json = File.ReadAllText(_filePath);
            _entries = JsonSerializer.Deserialize<List<HighscoreEntry>>(json) ?? new();
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
