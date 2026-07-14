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
            if (phaseComplete)
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
                IsComplete = phaseComplete,
                Note = phase.Note
            });
        }

        var gearFailed = AppendGearGate(challenge, snapshot, criteria, incompleteReasons);
        var ranked = incompleteReasons.Count == 0;
        double? scoreBeforeGates = null;
        double? scorePercent = null;
        var grade = "UNRANKED";
        var gearPenaltyApplied = false;

        if (ranked)
        {
            scoreBeforeGates = Math.Round(Math.Clamp(totalBeforeGate, 0, 100), 1);
            var final = scoreBeforeGates.Value;
            if (gearFailed)
            {
                final *= _key.Gates!.Gear!.MultiplierOnFail;
                gearPenaltyApplied = true;
            }

            scorePercent = Math.Round(Math.Clamp(final, 0, 100), 1);
            grade = ScoreResult.GradeFromPercent(scorePercent.Value);
        }

        var summary = ScoreBreakdownFormatter.Format(
            scorePercent,
            grade,
            scoreBeforeGates,
            gearPenaltyApplied,
            phases,
            criteria,
            incompleteReasons);

        return new ScoreResult
        {
            ChallengeId = challenge.Id,
            ChallengeTitle = challenge.Title,
            ScorePercent = scorePercent,
            Grade = grade,
            IsRanked = ranked,
            IncompleteReasons = incompleteReasons,
            Criteria = criteria,
            ScoredAtUtc = DateTimeOffset.UtcNow,
            Summary = summary,
            ScoreBeforeGatesPercent = scoreBeforeGates,
            GearUpPenaltyApplied = gearPenaltyApplied,
            PhaseScores = phases
        };
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
}
