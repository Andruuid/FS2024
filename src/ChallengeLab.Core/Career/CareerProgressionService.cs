using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Career;

public sealed class CareerProgressionService
{
    private readonly CareerConfig _config;
    private readonly IReadOnlyDictionary<string, ChallengeConfig> _challenges;
    private readonly CareerProgressStore _store;
    private readonly IRandomIndexProvider _random;
    private readonly Func<DateTimeOffset> _utcNow;

    public CareerProgressionService(
        CareerConfig config,
        IReadOnlyList<ChallengeConfig> challenges,
        CareerProgressStore store,
        IRandomIndexProvider? random = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(challenges);
        ArgumentNullException.ThrowIfNull(store);

        var validation = CareerConfigValidationResult.Validate(config, challenges);
        if (!validation.IsValid)
            throw new InvalidOperationException("Invalid Career configuration: " + string.Join(" | ", validation.Errors));

        _config = config;
        _challenges = challenges.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        _store = store;
        _random = random ?? new SystemRandomIndexProvider();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

        State = _store.Load();
        var stateError = ValidateState(State);
        if (stateError is not null)
            State = _store.ResetInvalidState(stateError);
    }

    public CareerProgressState State { get; private set; }
    public CareerConfig Config => _config;
    public string? RecoveryMessage => _store.RecoveryMessage;
    public bool IsComplete => State.CompletedStageCount >= _config.Ranks.Count;
    public int TotalStageCount => _config.Ranks.Count;
    public double ProgressPercent => TotalStageCount == 0
        ? 0
        : State.CompletedStageCount * 100.0 / TotalStageCount;
    public CareerRankConfig? CurrentRank => IsComplete ? null : _config.Ranks[State.CompletedStageCount];

    public ChallengeConfig? AcceptedAssignment =>
        string.IsNullOrWhiteSpace(State.AcceptedAssignmentId)
            ? null
            : _challenges.GetValueOrDefault(State.AcceptedAssignmentId);

    public IReadOnlyList<CareerRankConfig> UnlockedRanks =>
        _config.Ranks.Take(Math.Min(State.CompletedStageCount, _config.Ranks.Count)).ToList();

    public ChallengeConfig AcceptAssignment()
    {
        if (IsComplete)
            throw new InvalidOperationException("Career is complete; no further assignment is available.");

        if (AcceptedAssignment is { } accepted)
            return accepted;

        var candidates = _config.AssignmentChallengeIds
            .Where(id => !string.Equals(id, State.PreviousAssignmentId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (candidates.Count == 0)
            candidates = _config.AssignmentChallengeIds.ToList();

        var index = _random.Next(candidates.Count);
        if (index < 0 || index >= candidates.Count)
            throw new InvalidOperationException($"Random assignment index {index} is outside 0..{candidates.Count - 1}.");

        var challenge = _challenges[candidates[index]];
        var previousState = CloneState(State);
        State.AcceptedAssignmentId = challenge.Id;
        State.AcceptedAtUtc = _utcNow();
        try
        {
            _store.Save(State);
        }
        catch
        {
            State = previousState;
            throw;
        }
        return challenge;
    }

    public CareerOutcome? RecordAttempt(
        LandingAttemptOrigin origin,
        string challengeId,
        bool isRanked,
        double? displayedScorePercent,
        string? unrankedReason = null)
    {
        if (origin != LandingAttemptOrigin.CareerAssignment
            || IsComplete
            || string.IsNullOrWhiteSpace(State.AcceptedAssignmentId)
            || !string.Equals(State.AcceptedAssignmentId, challengeId, StringComparison.OrdinalIgnoreCase))
            return null;

        var rank = CurrentRank!;
        var now = _utcNow();
        var hasValidScore = displayedScorePercent is { } score && double.IsFinite(score);
        var passed = isRanked
                     && hasValidScore
                     && displayedScorePercent!.Value >= _config.PassScorePercent;

        var previousState = CloneState(State);
        State.AttemptCount++;
        CareerOutcome outcome;
        if (passed)
        {
            var completedChallengeId = State.AcceptedAssignmentId;
            State.PreviousAssignmentId = completedChallengeId;
            State.AcceptedAssignmentId = null;
            State.AcceptedAtUtc = null;
            State.CompletedStageCount++;
            var complete = IsComplete;
            var promotedRank = CurrentRank;
            outcome = new CareerOutcome
            {
                Kind = complete ? CareerOutcomeKind.Complete : CareerOutcomeKind.Passed,
                ChallengeId = completedChallengeId!,
                RankId = rank.Id,
                RankTitle = rank.Title,
                ScorePercent = displayedScorePercent,
                IsRanked = true,
                AttemptedAtUtc = now,
                Message = complete
                    ? $"CAREER COMPLETE — {displayedScorePercent:0.0}%"
                    : $"PROMOTED — {promotedRank!.Title} · {displayedScorePercent:0.0}%"
            };
        }
        else
        {
            var unranked = !isRanked || !hasValidScore;
            var reason = string.IsNullOrWhiteSpace(unrankedReason)
                ? "Required telemetry was unavailable."
                : unrankedReason.Trim();
            outcome = new CareerOutcome
            {
                Kind = unranked ? CareerOutcomeKind.Unranked : CareerOutcomeKind.Retry,
                ChallengeId = challengeId,
                RankId = rank.Id,
                RankTitle = rank.Title,
                ScorePercent = displayedScorePercent,
                IsRanked = isRanked,
                AttemptedAtUtc = now,
                Message = unranked
                    ? $"UNRANKED — assignment retained. {reason}"
                    : $"RETRY — {displayedScorePercent:0.0}% / {_config.PassScorePercent:0.0}% required"
            };
        }

        State.LastResult = outcome;
        try
        {
            _store.Save(State);
        }
        catch
        {
            State = previousState;
            throw;
        }
        return outcome;
    }

    public ChallengeConfig GetRewardChallenge(int rankIndex)
    {
        if (rankIndex < 0 || rankIndex >= _config.Ranks.Count)
            throw new ArgumentOutOfRangeException(nameof(rankIndex));
        return _challenges[_config.Ranks[rankIndex].RewardChallengeId];
    }

    private string? ValidateState(CareerProgressState state)
    {
        if (state.SchemaVersion != CareerProgressState.CurrentSchemaVersion)
            return $"Unsupported schemaVersion {state.SchemaVersion}.";
        if (state.CompletedStageCount < 0 || state.CompletedStageCount > _config.Ranks.Count)
            return $"completedStageCount {state.CompletedStageCount} is outside the configured ladder.";
        if (state.AttemptCount < 0)
            return "attemptCount cannot be negative.";
        if (state.CompletedStageCount == _config.Ranks.Count
            && !string.IsNullOrWhiteSpace(state.AcceptedAssignmentId))
            return "A completed career cannot retain an accepted assignment.";
        if (!string.IsNullOrWhiteSpace(state.AcceptedAssignmentId))
        {
            if (!_config.AssignmentChallengeIds.Contains(state.AcceptedAssignmentId, StringComparer.OrdinalIgnoreCase))
                return $"Accepted assignment '{state.AcceptedAssignmentId}' is no longer in the career pool.";
            if (!_challenges.TryGetValue(state.AcceptedAssignmentId, out var assignment) || !assignment.Available)
                return $"Accepted assignment '{state.AcceptedAssignmentId}' is missing or unavailable.";
            if (state.AcceptedAtUtc is null)
                return "An accepted assignment must include acceptedAtUtc.";
        }
        else if (state.AcceptedAtUtc is not null)
        {
            return "acceptedAtUtc cannot exist without acceptedAssignmentId.";
        }

        return null;
    }

    private static CareerProgressState CloneState(CareerProgressState state) => new()
    {
        SchemaVersion = state.SchemaVersion,
        CompletedStageCount = state.CompletedStageCount,
        AcceptedAssignmentId = state.AcceptedAssignmentId,
        AcceptedAtUtc = state.AcceptedAtUtc,
        PreviousAssignmentId = state.PreviousAssignmentId,
        AttemptCount = state.AttemptCount,
        LastResult = state.LastResult is null
            ? null
            : new CareerOutcome
            {
                Kind = state.LastResult.Kind,
                ChallengeId = state.LastResult.ChallengeId,
                RankId = state.LastResult.RankId,
                RankTitle = state.LastResult.RankTitle,
                ScorePercent = state.LastResult.ScorePercent,
                IsRanked = state.LastResult.IsRanked,
                AttemptedAtUtc = state.LastResult.AttemptedAtUtc,
                Message = state.LastResult.Message
            }
    };
}
