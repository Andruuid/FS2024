using System.Text.Json;
using System.Text.Json.Serialization;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Highscores;

/// <summary>
/// Persists per-landing telemetry (downsampled) for offline scoring analysis.
/// Files live under %LocalAppData%\ChallengeLab\traces\.
/// </summary>
public sealed class LandingTraceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _directory;

    public LandingTraceStore(string? directory = null)
    {
        _directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "traces");
        Directory.CreateDirectory(_directory);
    }

    public string DirectoryPath => _directory;

    /// <summary>
    /// Write a landing trace. Samples are downsampled to ~<paramref name="samplesPerSecond"/> Hz
    /// (touchdown sample is always kept).
    /// </summary>
    public string Save(
        ScoreResult result,
        LandingSnapshot snapshot,
        Guid? highscoreId = null,
        double samplesPerSecond = 5.0)
    {
        var id = highscoreId ?? Guid.NewGuid();
        var stamp = result.ScoredAtUtc.ToString("yyyyMMdd_HHmmss");
        var safeChallenge = string.Join("_",
            (result.ChallengeId ?? "landing").Split(Path.GetInvalidFileNameChars()));
        var path = Path.Combine(_directory, $"{stamp}_{safeChallenge}_{id:N}.json");

        var interval = samplesPerSecond <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(1.0 / samplesPerSecond);

        var tdTime = snapshot.Touchdown?.Timestamp;

        var doc = new LandingTraceDocument
        {
            Id = id,
            Utc = result.ScoredAtUtc,
            ChallengeId = result.ChallengeId ?? "",
            ChallengeTitle = result.ChallengeTitle ?? "",
            ScorePercent = result.ScorePercent,
            Grade = result.Grade,
            IsRanked = result.IsRanked,
            EvaluationKeyId = result.EvaluationKeyId,
            EvaluationKeyVersion = result.EvaluationKeyVersion,
            ScoringProfileHash = result.ScoringProfileHash,
            RankedBucketId = result.RankedBucketId,
            Summary = result.Summary,
            Diagnostics = result.Diagnostics,
            Metrics = result.Criteria.Select(c => new LandingTraceMetric
            {
                Id = c.Id,
                DisplayName = c.DisplayName,
                PhaseId = c.PhaseId,
                ScorePercent = c.ScorePercent is null ? null : Math.Round(c.ScorePercent.Value, 1),
                RawValue = c.RawValue,
                Unit = c.Unit,
                Status = c.Status.ToString()
            }).ToList(),
            Snapshot = new LandingTraceSnapshot
            {
                TouchdownLateralOffsetM = snapshot.TouchdownLateralOffsetM,
                VerticalSpeedAtTouchdownFpm = snapshot.VerticalSpeedAtTouchdownFpm,
                AirspeedAtTouchdownKts = snapshot.AirspeedAtTouchdownKts,
                ApproachPathRms = snapshot.ApproachPathRms,
                ApproachPathSampleCount = snapshot.ApproachPathSampleCount,
                ApproachGlideslopeMeanAbsFt = snapshot.ApproachGlideslopeMeanAbsFt,
                ApproachVerticalVariationFtPerSec = snapshot.ApproachVerticalVariationFtPerSec,
                ApproachLateralWeaveIndex = snapshot.ApproachLateralWeaveIndex,
                ApproachLateralDistanceM = snapshot.ApproachLateralDistanceM,
                ApproachMetricDurationSec = snapshot.ApproachMetricDurationSec,
                RolloutLateralMeanM = snapshot.RolloutLateralMeanM,
                RolloutLateralPeakM = snapshot.RolloutLateralPeakM,
                RolloutWeaveIndex = snapshot.RolloutWeaveIndex,
                RolloutDistanceM = snapshot.RolloutDistanceM,
                GroundTrackErrorMeanDeg = snapshot.GroundTrackErrorMeanDeg,
                PostTouchdownAlignmentMeanDeg = snapshot.PostTouchdownAlignmentMeanDeg,
                VappKts = snapshot.VappKts,
                TargetTouchdownIasKts = snapshot.TargetTouchdownIasKts
            },
            ApproachSamples = Downsample(snapshot.ApproachSamples, interval, tdTime),
            RolloutSamples = Downsample(snapshot.RolloutSamples, interval, tdTime),
            SampleRateHz = samplesPerSecond
        };

        File.WriteAllText(path, JsonSerializer.Serialize(doc, JsonOptions));
        return path;
    }

    private static List<LandingTraceSample> Downsample(
        IReadOnlyList<TelemetrySample> samples,
        TimeSpan minInterval,
        DateTimeOffset? alwaysKeep)
    {
        if (samples.Count == 0) return new List<LandingTraceSample>();

        var ordered = samples.OrderBy(s => s.Timestamp).ToList();
        var result = new List<LandingTraceSample>(Math.Min(ordered.Count, 4096));
        DateTimeOffset? lastKept = null;

        foreach (var s in ordered)
        {
            var force = alwaysKeep is not null
                        && Math.Abs((s.Timestamp - alwaysKeep.Value).TotalMilliseconds) < 5;
            if (!force && lastKept is not null && minInterval > TimeSpan.Zero
                && s.Timestamp - lastKept.Value < minInterval)
                continue;

            result.Add(LandingTraceSample.From(s));
            lastKept = s.Timestamp;
        }

        // Cap very long approaches (~20 min @ 5 Hz ≈ 6000 pts) to keep files manageable.
        const int maxPoints = 6000;
        if (result.Count <= maxPoints) return result;

        var step = (double)result.Count / maxPoints;
        var thinned = new List<LandingTraceSample>(maxPoints);
        for (var i = 0; i < maxPoints; i++)
            thinned.Add(result[(int)(i * step)]);
        return thinned;
    }
}

public sealed class LandingTraceDocument
{
    public Guid Id { get; set; }
    public DateTimeOffset Utc { get; set; }
    public string ChallengeId { get; set; } = "";
    public string ChallengeTitle { get; set; } = "";
    public double? ScorePercent { get; set; }
    public string Grade { get; set; } = "";
    public bool IsRanked { get; set; }
    public string EvaluationKeyId { get; set; } = "";
    public int EvaluationKeyVersion { get; set; }
    public string ScoringProfileHash { get; set; } = "";
    public string RankedBucketId { get; set; } = "";
    public string? Summary { get; set; }
    public LandingResultDiagnostics Diagnostics { get; set; } = new();
    public double SampleRateHz { get; set; }
    public LandingTraceSnapshot Snapshot { get; set; } = new();
    public List<LandingTraceMetric> Metrics { get; set; } = new();
    public List<LandingTraceSample> ApproachSamples { get; set; } = new();
    public List<LandingTraceSample> RolloutSamples { get; set; } = new();
}

public sealed class LandingTraceSnapshot
{
    public double TouchdownLateralOffsetM { get; set; }
    public double VerticalSpeedAtTouchdownFpm { get; set; }
    public double AirspeedAtTouchdownKts { get; set; }
    public double ApproachPathRms { get; set; }
    public int ApproachPathSampleCount { get; set; }
    public double ApproachGlideslopeMeanAbsFt { get; set; }
    public double ApproachVerticalVariationFtPerSec { get; set; }
    public double ApproachLateralWeaveIndex { get; set; }
    public double ApproachLateralDistanceM { get; set; }
    public double ApproachMetricDurationSec { get; set; }
    public double RolloutLateralMeanM { get; set; }
    public double RolloutLateralPeakM { get; set; }
    public double RolloutWeaveIndex { get; set; }
    public double RolloutDistanceM { get; set; }
    public double GroundTrackErrorMeanDeg { get; set; }
    public double PostTouchdownAlignmentMeanDeg { get; set; }
    public double VappKts { get; set; }
    public double TargetTouchdownIasKts { get; set; }
}

public sealed class LandingTraceMetric
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? PhaseId { get; set; }
    public double? ScorePercent { get; set; }
    public double? RawValue { get; set; }
    public string? Unit { get; set; }
    public string? Status { get; set; }
}

public sealed class LandingTraceSample
{
    public DateTimeOffset T { get; set; }
    public double? SimT { get; set; }
    public double Lat { get; set; }
    public double Lon { get; set; }
    public double AltFt { get; set; }
    public double AglFt { get; set; }
    public double Hdg { get; set; }
    public double Track { get; set; }
    public double Pitch { get; set; }
    public double Bank { get; set; }
    public double Ias { get; set; }
    public double Gs { get; set; }
    public double Vs { get; set; }
    public double G { get; set; }
    public bool OnGnd { get; set; }
    public bool? LeftMainOnGnd { get; set; }
    public bool? RightMainOnGnd { get; set; }
    public Dictionary<int, bool>? GearContacts { get; set; }
    public int Flaps { get; set; }
    public double Gear { get; set; }

    public static LandingTraceSample From(TelemetrySample s) => new()
    {
        T = s.Timestamp,
        SimT = double.IsFinite(s.SimulationTimeSeconds) ? s.SimulationTimeSeconds : null,
        Lat = s.Latitude,
        Lon = s.Longitude,
        AltFt = s.AltitudeFeet,
        AglFt = s.RadioHeightFeet > 0 ? s.RadioHeightFeet : s.AglFeet,
        Hdg = s.HeadingTrueDeg,
        Track = s.GroundTrackTrueDeg,
        Pitch = s.PitchDeg,
        Bank = s.BankDeg,
        Ias = s.AirspeedKts,
        Gs = s.GroundSpeedKts,
        Vs = s.VerticalSpeedFpm,
        G = s.GForce,
        OnGnd = s.SimOnGround,
        LeftMainOnGnd = s.GearOnGroundByIndex?.TryGetValue(1, out var left) == true ? left : null,
        RightMainOnGnd = s.GearOnGroundByIndex?.TryGetValue(2, out var right) == true ? right : null,
        GearContacts = s.GearOnGroundByIndex?.ToDictionary(pair => pair.Key, pair => pair.Value),
        Flaps = s.FlapsHandleIndex,
        Gear = s.GearHandlePosition
    };
}
