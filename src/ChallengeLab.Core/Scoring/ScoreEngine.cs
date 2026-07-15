using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Scoring;

public sealed class ScoreEngine
{
    private readonly LandingEvaluationKey _key;

    public ScoreEngine(LandingEvaluationKey evaluationKey)
    {
        var errors = EvaluationKeyValidator.Validate(evaluationKey);
        if (errors.Count > 0)
            throw new ArgumentException(
                "Evaluation key is invalid: " + string.Join(" | ", errors),
                nameof(evaluationKey));
        _key = evaluationKey;
    }

    public ScoreResult Evaluate(ChallengeConfig challenge, LandingSnapshot snapshot)
        => EvaluateCore(challenge, snapshot, preview: false);

    /// <summary>
    /// Live projection: every metric that is not yet measurable scores 100%.
    /// Available metrics use real evaluators. Gear penalty only applies after touchdown.
    /// Never save this as a highscore — use <see cref="Evaluate"/> at settle.
    /// </summary>
    public ScoreResult EvaluatePreview(ChallengeConfig challenge, LandingSnapshot snapshot)
        => EvaluateCore(challenge, snapshot, preview: true);

    private ScoreResult EvaluateCore(ChallengeConfig challenge, LandingSnapshot snapshot, bool preview)
    {
        var criteria = new List<CriterionScore>();
        var phases = new List<PhaseScore>();
        var incompleteReasons = new List<string>();
        double totalBeforeGate = 0;

        foreach (var phase in _key.Phases)
        {
            var phaseComplete = true;
            double phaseScore01 = 0;

            foreach (var metric in phase.Metrics)
            {
                var observation = MetricResolver.Resolve(metric.Metric, snapshot, challenge);
                var maxOverallPoints = metric.ImportancePercent * phase.WeightPercent / 100.0;
                if (!observation.IsAvailable)
                {
                    if (preview)
                    {
                        // Assume remaining flight will be perfect for this metric.
                        phaseScore01 += 1.0 * (metric.ImportancePercent / 100.0);
                        criteria.Add(new CriterionScore
                        {
                            Id = metric.Id,
                            DisplayName = metric.DisplayName,
                            Score01 = 1.0,
                            RawValue = null,
                            Unit = metric.Unit,
                            Status = MetricStatus.Informational,
                            UnavailableReason = observation.UnavailableReason,
                            Note =
                                $"[PREVIEW · assumed 100%] {MetricExplanations.DefaultCatalog(metric.Id, metric.DisplayName)} " +
                                $"Not measured yet: {observation.UnavailableReason ?? "pending"}",
                            PhaseId = phase.Id,
                            PhaseDisplayName = phase.DisplayName,
                            PhaseImportancePercent = metric.ImportancePercent,
                            PhaseWeightPercent = phase.WeightPercent,
                            MaxOverallPoints = maxOverallPoints
                        });
                        continue;
                    }

                    phaseComplete = false;
                    var reason = observation.UnavailableReason ?? "Required telemetry is unavailable.";
                    incompleteReasons.Add($"{phase.DisplayName} / {metric.DisplayName}: {reason}");
                    criteria.Add(new CriterionScore
                    {
                        Id = metric.Id,
                        DisplayName = metric.DisplayName,
                        Score01 = null,
                        RawValue = null,
                        Unit = metric.Unit,
                        Status = MetricStatus.Unavailable,
                        UnavailableReason = reason,
                        Note = $"{MetricExplanations.DefaultCatalog(metric.Id, metric.DisplayName)} Telemetry unavailable: {reason}",
                        PhaseId = phase.Id,
                        PhaseDisplayName = phase.DisplayName,
                        PhaseImportancePercent = metric.ImportancePercent,
                        PhaseWeightPercent = phase.WeightPercent,
                        MaxOverallPoints = maxOverallPoints
                    });
                    continue;
                }

                var raw = observation.Value!.Value;
                var evaluator = EvaluatorFactory.Create(metric.Evaluator);
                var score01 = evaluator.Evaluate(raw, metric);
                phaseScore01 += score01 * (metric.ImportancePercent / 100.0);

                double? displayRaw = raw;
                if (metric.Metric.Equals("touchdownIasErrorKts", StringComparison.OrdinalIgnoreCase))
                    displayRaw = snapshot.AirspeedAtTouchdownKts;

                var explanation = MetricExplanations.For(metric, snapshot, score01, raw);
                if (preview)
                    explanation = "[PREVIEW · measured] " + explanation;
                explanation =
                    $"[{phase.DisplayName} · {metric.ImportancePercent:0.#}% of phase · " +
                    $"{maxOverallPoints:0.##} max overall points] {explanation}";

                criteria.Add(new CriterionScore
                {
                    Id = metric.Id,
                    DisplayName = metric.DisplayName,
                    Score01 = score01,
                    RawValue = displayRaw,
                    Unit = metric.Unit,
                    Note = explanation,
                    Status = MetricStatus.Scored,
                    PhaseId = phase.Id,
                    PhaseDisplayName = phase.DisplayName,
                    PhaseImportancePercent = metric.ImportancePercent,
                    PhaseWeightPercent = phase.WeightPercent,
                    MaxOverallPoints = maxOverallPoints
                });
            }

            double? phasePercent = null;
            if (phaseComplete || preview)
            {
                phasePercent = Math.Round(Math.Clamp(phaseScore01 * 100.0, 0, 100), 1);
                totalBeforeGate += phaseScore01 * phase.WeightPercent;
            }

            phases.Add(new PhaseScore
            {
                PhaseId = phase.Id,
                DisplayName = phase.DisplayName,
                WeightPercent = phase.WeightPercent,
                ScorePercent = phasePercent,
                IsComplete = phaseComplete || preview,
                Note = phase.Note
            });
        }

        var gearFailed = preview
            ? AppendGearGatePreview(challenge, snapshot, criteria)
            : AppendGearGate(challenge, snapshot, criteria, incompleteReasons);
        var flapsFailed = preview
            ? AppendFlapsGatePreview(snapshot, criteria)
            : AppendFlapsGate(snapshot, criteria, incompleteReasons);
        var ranked = preview || incompleteReasons.Count == 0;
        double? scoreBeforeGates = null;
        double? scorePercent = null;
        var grade = "UNRANKED";
        var gearPenaltyApplied = false;
        var flapsPenaltyApplied = false;

        if (ranked)
        {
            scoreBeforeGates = Math.Round(Math.Clamp(totalBeforeGate, 0, 100), 1);
            var final = scoreBeforeGates.Value;
            if (gearFailed)
            {
                final *= _key.Gates!.Gear!.MultiplierOnFail;
                gearPenaltyApplied = true;
            }

            if (flapsFailed)
            {
                final *= _key.Gates!.Flaps!.MultiplierOnFail;
                flapsPenaltyApplied = true;
            }

            scorePercent = Math.Round(Math.Clamp(final, 0, 100), 1);
            grade = ScoreResult.GradeFromPercent(scorePercent.Value);
        }

        var summary = ScoreBreakdownFormatter.Format(
            scorePercent,
            grade,
            scoreBeforeGates,
            gearPenaltyApplied,
            flapsPenaltyApplied,
            phases,
            criteria,
            incompleteReasons);

        return new ScoreResult
        {
            ChallengeId = challenge.Id,
            ChallengeTitle = challenge.Title,
            ScorePercent = scorePercent,
            Grade = grade,
            IsRanked = ranked && !preview,
            IsPreview = preview,
            IncompleteReasons = incompleteReasons,
            Criteria = criteria,
            ScoredAtUtc = DateTimeOffset.UtcNow,
            Summary = summary,
            ScoreBeforeGatesPercent = scoreBeforeGates,
            GearUpPenaltyApplied = gearPenaltyApplied,
            FlapsPenaltyApplied = flapsPenaltyApplied,
            PhaseScores = phases
        };
    }

    /// <summary>Preview gear: no TD yet → no penalty; after TD use real gear state.</summary>
    private bool AppendGearGatePreview(
        ChallengeConfig challenge,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria)
    {
        if (snapshot.Touchdown is null)
        {
            criteria.Add(new CriterionScore
            {
                Id = "gear",
                DisplayName = "Gear (safety gate)",
                Status = MetricStatus.Informational,
                Note = "[PREVIEW · assumed OK] Gear not yet assessed (no touchdown)."
            });
            return false;
        }

        // After TD, same gate semantics as final (informational / fail).
        var incomplete = new List<string>();
        return AppendGearGate(challenge, snapshot, criteria, incomplete);
    }

    private bool AppendGearGate(
        ChallengeConfig challenge,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons)
    {
        if (snapshot.Touchdown is null)
        {
            const string reason = "Touchdown telemetry was not captured.";
            incompleteReasons.Add("Gear gate: " + reason);
            criteria.Add(new CriterionScore
            {
                Id = "gear",
                DisplayName = "Gear (safety gate)",
                Status = MetricStatus.Unavailable,
                UnavailableReason = reason,
                Note = reason
            });
            return false;
        }

        if (!challenge.RequireGearDown)
        {
            criteria.Add(new CriterionScore
            {
                Id = "gear",
                DisplayName = "Gear (not required)",
                RawValue = snapshot.GearDownAtTouchdown ? 1 : 0,
                Status = MetricStatus.Informational,
                Note = "Challenge allows gear-up landings. Gear position is informational and awards no points."
            });
            return false;
        }

        if (snapshot.GearDownAtTouchdown)
        {
            criteria.Add(new CriterionScore
            {
                Id = "gear",
                DisplayName = "Gear (safety gate)",
                RawValue = 1,
                Status = MetricStatus.Informational,
                Note = "Gear down is the required baseline and awards no score credit."
            });
            return false;
        }

        var multiplier = _key.Gates!.Gear!.MultiplierOnFail;
        criteria.Add(new CriterionScore
        {
            Id = "gear",
            DisplayName = "Gear UP — hard penalty",
            RawValue = 0,
            Status = MetricStatus.GateFailed,
            Note =
                $"Gear up at touchdown. Ranked overall score × {multiplier:0.##} " +
                $"(~{(1 - multiplier) * 100:0}% cut). {_key.Gates.Gear.PenaltyDescription}"
        });
        return true;
    }

    /// <summary>Preview flaps: no TD yet → assume OK; after TD use real flaps index.</summary>
    private bool AppendFlapsGatePreview(LandingSnapshot snapshot, List<CriterionScore> criteria)
    {
        if (_key.Gates?.Flaps is null)
            return false;

        if (snapshot.Touchdown is null)
        {
            criteria.Add(new CriterionScore
            {
                Id = "flaps",
                DisplayName = "Flaps (safety gate)",
                Status = MetricStatus.Informational,
                Note = "[PREVIEW · assumed OK] Flaps not yet assessed (no touchdown)."
            });
            return false;
        }

        var incomplete = new List<string>();
        return AppendFlapsGate(snapshot, criteria, incomplete);
    }

    /// <summary>
    /// Flaps gate like gear: correct landing flaps award no points;
    /// flaps not set (out of min/max index) multiplies overall score.
    /// </summary>
    private bool AppendFlapsGate(
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons)
    {
        var cfg = _key.Gates?.Flaps;
        if (cfg is null)
            return false;

        if (snapshot.Touchdown is null)
        {
            const string reason = "Touchdown telemetry was not captured.";
            incompleteReasons.Add("Flaps gate: " + reason);
            criteria.Add(new CriterionScore
            {
                Id = "flaps",
                DisplayName = "Flaps (safety gate)",
                Status = MetricStatus.Unavailable,
                UnavailableReason = reason,
                Note = reason
            });
            return false;
        }

        var index = snapshot.FlapsIndexAtTouchdown;
        var min = cfg.MinIndex;
        var max = cfg.MaxIndex;
        if (index >= min && index <= max)
        {
            criteria.Add(new CriterionScore
            {
                Id = "flaps",
                DisplayName = "Flaps (safety gate)",
                RawValue = index,
                Unit = "index",
                Status = MetricStatus.Informational,
                Note =
                    $"Flaps index {index} in required landing band [{min:0}…{max:0}]. " +
                    "Baseline only — awards no score credit."
            });
            return false;
        }

        var multiplier = cfg.MultiplierOnFail;
        criteria.Add(new CriterionScore
        {
            Id = "flaps",
            DisplayName = "Flaps not set — penalty",
            RawValue = index,
            Unit = "index",
            Status = MetricStatus.GateFailed,
            Note =
                $"Flaps index {index} outside landing band [{min:0}…{max:0}]. " +
                $"Ranked overall score × {multiplier:0.##} " +
                $"(~{(1 - multiplier) * 100:0}% cut). {cfg.PenaltyDescription}"
        });
        return true;
    }
}
