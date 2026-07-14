namespace ChallengeLab.Core.Config;

/// <summary>Phase-weighted landing evaluation key (Approach / Touchdown / Rollout).
/// Loaded from config JSON at app startup — edit the file to finetune without code changes.</summary>
public sealed class LandingEvaluationKey
{
    public string Id { get; set; } = "";
    public int Version { get; set; } = 1;
    public string Description { get; set; } = "";
    public string Formula { get; set; } = "";
    public EvaluationSettle? Settle { get; set; }
    public EvaluationTiming? Timing { get; set; }
    public List<EvaluationPhase> Phases { get; set; } = new();
    public EvaluationGates? Gates { get; set; }

    /// <summary>Apply settle/window timings onto a scoring profile used by LandingSession.</summary>
    public void ApplyToProfile(ScoringProfileConfig profile)
    {
        if (Settle is not null)
        {
            if (Settle.GroundSpeedKts > 0)
                profile.SettledGroundSpeedKts = Settle.GroundSpeedKts;
            if (Settle.HoldSeconds > 0)
                profile.SettledHoldSeconds = Settle.HoldSeconds;
        }

        if (Timing is not null)
        {
            if (Timing.GroundTrackWindowBeforeSeconds > 0)
                profile.GroundTrackWindowBeforeSeconds = Timing.GroundTrackWindowBeforeSeconds;
            if (Timing.GroundTrackWindowAfterSeconds > 0)
                profile.GroundTrackWindowAfterSeconds = Timing.GroundTrackWindowAfterSeconds;
            if (Timing.PostTouchdownAlignmentDelaySeconds > 0)
                profile.PostTouchdownAlignmentDelaySeconds = Timing.PostTouchdownAlignmentDelaySeconds;
            if (Timing.FlareAglFeet > 0)
                profile.FlareAglFeet = Timing.FlareAglFeet;
        }

        if (Gates?.Gear is { MultiplierOnFail: > 0 and <= 1 } gear)
            profile.GearUpScoreMultiplier = gear.MultiplierOnFail;
    }
}

public sealed class EvaluationSettle
{
    public double GroundSpeedKts { get; set; } = 50;
    public double HoldSeconds { get; set; } = 1.0;
}

public sealed class EvaluationTiming
{
    public double GroundTrackWindowBeforeSeconds { get; set; } = 3;
    public double GroundTrackWindowAfterSeconds { get; set; } = 3;
    public double PostTouchdownAlignmentDelaySeconds { get; set; } = 2;
    public double FlareAglFeet { get; set; } = 50;
}

public sealed class EvaluationPhase
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double WeightPercent { get; set; }
    public string? Note { get; set; }
    public List<EvaluationMetric> Metrics { get; set; } = new();
}

public sealed class EvaluationMetric
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double ImportancePercent { get; set; }
    public string? Unit { get; set; }
    public string Evaluator { get; set; } = "target";
    public string Metric { get; set; } = "";
    public string? Note { get; set; }
    public string SampleAt { get; set; } = "touchdown";
    public Dictionary<string, double> Params { get; set; } = new();
    public List<ScorePoint>? Points { get; set; }

    public CriterionConfig ToCriterionConfig() => new()
    {
        Id = Id,
        DisplayName = DisplayName,
        Weight = ImportancePercent,
        Metric = Metric,
        Evaluator = Evaluator,
        SampleAt = SampleAt,
        Unit = Unit,
        Note = Note,
        Params = Params,
        Points = Points
    };
}

public sealed class EvaluationGates
{
    public GearGateConfig? Gear { get; set; }
}

public sealed class GearGateConfig
{
    public string Id { get; set; } = "gear";
    public string Type { get; set; } = "overallMultiplierIfFailed";
    public string RequireFlag { get; set; } = "requireGearDown";
    public bool FailWhenGearUp { get; set; } = true;
    public double MultiplierOnFail { get; set; } = 0.1;
    public string? PenaltyDescription { get; set; }
}
