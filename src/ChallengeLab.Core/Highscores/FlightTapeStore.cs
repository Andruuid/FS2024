using System.Text.Json;
using System.Text.Json.Serialization;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Highscores;

/// <summary>
/// Full-rate flight tapes for offline re-scoring.
/// Files live under %LocalAppData%\ChallengeLab\flights\.
/// </summary>
public sealed class FlightTapeStore
{
    public const string FormatId = "challengelab.flighttape/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _directory;

    public FlightTapeStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "flights");
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    /// <summary>Write a full-rate tape after a settled landing.</summary>
    public string Save(
        ChallengeConfig challenge,
        IReadOnlyList<TelemetrySample> samples,
        ScoreResult result,
        string? attemptOrigin = null)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(result);

        var id = Guid.NewGuid();
        var stamp = result.ScoredAtUtc.ToString("yyyyMMdd_HHmmss");
        var safeChallenge = string.Join("_",
            (result.ChallengeId ?? challenge.Id ?? "landing").Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(_directory, $"{stamp}_{safeChallenge}_{id:N}.json");

        var doc = new FlightTapeDocument
        {
            Format = FormatId,
            Id = id,
            Utc = result.ScoredAtUtc,
            AttemptOrigin = attemptOrigin ?? "",
            ChallengeId = result.ChallengeId ?? challenge.Id ?? "",
            ChallengeTitle = result.ChallengeTitle ?? challenge.Title ?? "",
            EvaluationKeyId = result.EvaluationKeyId,
            EvaluationKeyVersion = result.EvaluationKeyVersion,
            ScoringProfileHash = result.ScoringProfileHash,
            OriginalScorePercent = result.ScorePercent,
            OriginalGrade = result.Grade,
            OriginalIsRanked = result.IsRanked,
            FreeFlightCapabilities = challenge.FreeFlightCapabilities,
            OriginalCriteria = result.Criteria.Select(criterion => new FlightTapeCriterion
            {
                Id = criterion.Id,
                DisplayName = criterion.DisplayName,
                Status = criterion.Status,
                ScorePercent = criterion.ScorePercent,
                AppliedMultiplier = criterion.AppliedMultiplier,
                Note = criterion.Note,
                UnavailableReason = criterion.UnavailableReason
            }).ToList(),
            SampleCount = samples.Count,
            Challenge = CloneChallenge(challenge),
            Samples = samples.ToList()
        };

        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
        return path;
    }

    public FlightTapeDocument Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("Flight tape not found.", path);

        var json = File.ReadAllText(path);
        var doc = JsonSerializer.Deserialize<FlightTapeDocument>(json, JsonOptions)
                  ?? throw new InvalidOperationException("Flight tape is empty or invalid.");

        if (doc.Challenge is null)
            throw new InvalidOperationException("Flight tape is missing embedded challenge config.");
        if (doc.Samples is null)
            doc.Samples = new List<TelemetrySample>();
        doc.OriginalCriteria ??= new List<FlightTapeCriterion>();
        doc.Challenge.FreeFlightCapabilities ??= doc.FreeFlightCapabilities;

        doc.SourcePath = path;
        if (doc.SampleCount <= 0)
            doc.SampleCount = doc.Samples.Count;
        return doc;
    }

    public IReadOnlyList<FlightTapeListItem> List()
    {
        if (!Directory.Exists(_directory))
            return Array.Empty<FlightTapeListItem>();

        var items = new List<FlightTapeListItem>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json")
                     .OrderByDescending(File.GetLastWriteTimeUtc))
        {
            try
            {
                var doc = Load(path);
                items.Add(FlightTapeListItem.From(doc, path));
            }
            catch
            {
                // Skip unreadable files so one bad tape does not break the list.
            }
        }

        return items;
    }

    private static ChallengeConfig CloneChallenge(ChallengeConfig challenge)
    {
        var json = JsonSerializer.Serialize(challenge, JsonOptions);
        return JsonSerializer.Deserialize<ChallengeConfig>(json, JsonOptions)
               ?? throw new InvalidOperationException("Could not clone challenge for flight tape.");
    }
}

public sealed class FlightTapeDocument
{
    public string Format { get; set; } = FlightTapeStore.FormatId;
    public Guid Id { get; set; }
    public DateTimeOffset Utc { get; set; }
    public string AttemptOrigin { get; set; } = "";
    public string ChallengeId { get; set; } = "";
    public string ChallengeTitle { get; set; } = "";
    public string EvaluationKeyId { get; set; } = "";
    public int EvaluationKeyVersion { get; set; }
    public string ScoringProfileHash { get; set; } = "";
    public double? OriginalScorePercent { get; set; }
    public string OriginalGrade { get; set; } = "";
    public bool OriginalIsRanked { get; set; }
    public FreeFlightCapabilityContext? FreeFlightCapabilities { get; set; }
    public List<FlightTapeCriterion> OriginalCriteria { get; set; } = new();
    public int SampleCount { get; set; }
    public ChallengeConfig? Challenge { get; set; }
    public List<TelemetrySample> Samples { get; set; } = new();

    [JsonIgnore]
    public string? SourcePath { get; set; }
}

public sealed class FlightTapeCriterion
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public MetricStatus Status { get; set; }
    public double? ScorePercent { get; set; }
    public double? AppliedMultiplier { get; set; }
    public string? Note { get; set; }
    public string? UnavailableReason { get; set; }
}

public sealed class FlightTapeListItem
{
    public Guid Id { get; init; }
    public DateTimeOffset Utc { get; init; }
    public string ChallengeId { get; init; } = "";
    public string ChallengeTitle { get; init; } = "";
    public int SampleCount { get; init; }
    public double? OriginalScorePercent { get; init; }
    public string OriginalGrade { get; init; } = "";
    public string Path { get; init; } = "";
    public string DisplayName { get; init; } = "";

    public static FlightTapeListItem From(FlightTapeDocument doc, string path)
    {
        var score = doc.OriginalScorePercent is null
            ? "—"
            : $"{doc.OriginalScorePercent:0.0}% {doc.OriginalGrade}";
        return new FlightTapeListItem
        {
            Id = doc.Id,
            Utc = doc.Utc,
            ChallengeId = doc.ChallengeId,
            ChallengeTitle = doc.ChallengeTitle,
            SampleCount = doc.SampleCount > 0 ? doc.SampleCount : doc.Samples.Count,
            OriginalScorePercent = doc.OriginalScorePercent,
            OriginalGrade = doc.OriginalGrade ?? "",
            Path = path,
            DisplayName =
                $"{doc.Utc:yyyy-MM-dd HH:mm}  ·  {doc.ChallengeTitle}  ·  {score}  ·  " +
                $"{(doc.SampleCount > 0 ? doc.SampleCount : doc.Samples.Count)} samples"
        };
    }
}

/// <summary>Buffers full-rate telemetry while a scoring session is armed.</summary>
public sealed class FlightTapeRecorder
{
    private readonly List<TelemetrySample> _samples = new();
    private ChallengeConfig? _challenge;
    private string _attemptOrigin = "";
    private bool _active;

    public bool IsActive => _active;
    public int SampleCount => _samples.Count;
    public ChallengeConfig? Challenge => _challenge;
    public string AttemptOrigin => _attemptOrigin;
    public IReadOnlyList<TelemetrySample> Samples => _samples;

    public void Start(ChallengeConfig challenge, string attemptOrigin = "")
    {
        ArgumentNullException.ThrowIfNull(challenge);
        _samples.Clear();
        _challenge = challenge;
        _attemptOrigin = attemptOrigin ?? "";
        _active = true;
    }

    public void Add(TelemetrySample sample)
    {
        if (!_active || sample is null) return;
        _samples.Add(sample);
    }

    public void Cancel()
    {
        _active = false;
        _samples.Clear();
        _challenge = null;
        _attemptOrigin = "";
    }

    /// <summary>
    /// Finish recording and return a snapshot of samples for save.
    /// Clears the buffer. Returns null if nothing was recorded.
    /// </summary>
    public (ChallengeConfig Challenge, string AttemptOrigin, List<TelemetrySample> Samples)? Finish()
    {
        if (!_active || _challenge is null || _samples.Count == 0)
        {
            Cancel();
            return null;
        }

        var result = (_challenge, _attemptOrigin, _samples.ToList());
        Cancel();
        return result;
    }
}
