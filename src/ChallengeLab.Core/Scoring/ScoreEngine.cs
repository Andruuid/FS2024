using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Scoring;

public sealed class ScoreEngine
{
    private readonly LandingEvaluationKey? _key;

    public ScoreEngine(LandingEvaluationKey? evaluationKey = null)
    {
        _key = evaluationKey;
    }

    public ScoreResult Evaluate(
        ChallengeConfig challenge,
        ScoringProfileConfig profile,
        LandingSnapshot snapshot,
        DifficultyLevel level)
    {
        // Prefer phase hierarchy when evaluation key is loaded
        if (_key?.Phases is { Count: > 0 })
            return EvaluateHierarchical(challenge, profile, snapshot, level, _key);

        return EvaluateFlat(challenge, profile, snapshot, level);
    }

    /// <summary>
    /// final = Σ (phaseScore × phaseWeight/100), then gear gate.
    /// Within each phase: Σ (metricScore × importance/100) over metrics active on this difficulty (renormalized).
    /// </summary>
    private ScoreResult EvaluateHierarchical(
        ChallengeConfig challenge,
        ScoringProfileConfig profile,
        LandingSnapshot snapshot,
        DifficultyLevel level,
        LandingEvaluationKey key)
    {
        var criteriaScores = new List<CriterionScore>();
        var phaseScores = new List<PhaseScore>();
        double finalAccum = 0;
        double phaseWeightUsed = 0;

        foreach (var phase in key.Phases)
        {
            var active = phase.Metrics.Where(m => m.AppliesTo(level)).ToList();
            if (active.Count == 0)
            {
                phaseScores.Add(new PhaseScore
                {
                    PhaseId = phase.Id,
                    DisplayName = phase.DisplayName,
                    WeightPercent = phase.WeightPercent,
                    ScorePercent = 0,
                    Used = false,
                    Note = $"No metrics for {level.ToDisplayName()} in this phase."
                });
                continue;
            }

            var importanceSum = active.Sum(m => m.ImportancePercent);
            if (importanceSum <= 0) importanceSum = active.Count;

            double phaseScore01 = 0;
            foreach (var metric in active)
            {
                var criterion = metric.ToCriterionConfig();
                var raw = MetricResolver.Resolve(metric.Metric, snapshot, challenge);
                var evaluator = EvaluatorFactory.Create(metric.Evaluator);
                double score01 = raw is null
                    ? 0.5
                    : evaluator.Evaluate(raw.Value, criterion, level);

                var share = metric.ImportancePercent / importanceSum;
                phaseScore01 += score01 * share;

                var displayRaw = raw;
                if (IsIasErrorMetric(metric.Metric))
                    displayRaw = snapshot.AirspeedAtTouchdownKts;
                else if (IsExcessSpeedMetric(metric.Metric))
                    displayRaw = snapshot.ExcessSpeedOverVappKts;

                var explanation = MetricExplanations.For(criterion, snapshot, level, score01, raw);
                explanation =
                    $"[{phase.DisplayName} · {metric.ImportancePercent:0.#}% of phase] " + explanation;

                criteriaScores.Add(new CriterionScore
                {
                    Id = metric.Id,
                    DisplayName = metric.DisplayName,
                    Weight = metric.ImportancePercent * phase.WeightPercent / 100.0,
                    Score01 = score01,
                    RawValue = displayRaw,
                    Unit = metric.Unit,
                    Note = explanation,
                    Applied = true
                });
            }

            var phasePercent = Math.Clamp(phaseScore01 * 100.0, 0, 100);
            phaseScores.Add(new PhaseScore
            {
                PhaseId = phase.Id,
                DisplayName = phase.DisplayName,
                WeightPercent = phase.WeightPercent,
                ScorePercent = Math.Round(phasePercent, 1),
                Used = true,
                Note = phase.Note
            });

            finalAccum += phasePercent * (phase.WeightPercent / 100.0);
            phaseWeightUsed += phase.WeightPercent;
        }

        // Renormalize if some phases unused (e.g. Easy has no approach metrics)
        double percentBeforeGate;
        if (phaseWeightUsed > 0 && Math.Abs(phaseWeightUsed - 100) > 0.01)
            percentBeforeGate = finalAccum * (100.0 / phaseWeightUsed);
        else
            percentBeforeGate = finalAccum;

        percentBeforeGate = Math.Clamp(percentBeforeGate, 0, 100);

        // Gear safety gate
        var gearUpHardPenalty = false;
        AppendGearGate(challenge, profile, snapshot, key, criteriaScores, ref gearUpHardPenalty);

        var percent = percentBeforeGate;
        string? gateNote = null;
        if (gearUpHardPenalty)
        {
            var mult = key.Gates?.Gear?.MultiplierOnFail
                       ?? (profile.GearUpScoreMultiplier > 0 ? profile.GearUpScoreMultiplier : 0.1);
            if (mult <= 0 || mult > 1) mult = 0.1;
            percent = percentBeforeGate * mult;
            var cutPct = (1.0 - mult) * 100.0;
            gateNote =
                $"GEAR UP: overall score cut by {cutPct:0}% " +
                $"({percentBeforeGate:0.0}% → {percent:0.0}%). Gear-down awards no credit.";
        }

        percent = Math.Round(Math.Clamp(percent, 0, 100), 1);

        var phaseSummary = string.Join(" · ",
            phaseScores.Where(p => p.Used)
                .Select(p => $"{p.DisplayName} {p.ScorePercent:0.0}% (w={p.WeightPercent:0}%)"));

        var scored = criteriaScores.Where(c => c.Applied && c.Weight > 0).ToList();
        var worst = scored.OrderBy(c => c.Score01).FirstOrDefault();
        var best = scored.OrderByDescending(c => c.Score01).FirstOrDefault();
        var summary =
            $"Phases: {phaseSummary}. " +
            (best is null
                ? ""
                : $"Strongest: {best.DisplayName} ({best.ScorePercent:0}%). Weakest: {worst!.DisplayName} ({worst.ScorePercent:0}%).");
        if (gateNote is not null)
            summary = gateNote + " " + summary;

        return new ScoreResult
        {
            ChallengeId = challenge.Id,
            ChallengeTitle = challenge.Title,
            Level = level,
            ScorePercent = percent,
            Grade = ScoreResult.GradeFromPercent(percent),
            Criteria = criteriaScores,
            ScoredAtUtc = DateTimeOffset.UtcNow,
            Summary = summary.Trim(),
            ScoreBeforeGatesPercent = Math.Round(percentBeforeGate, 1),
            GearUpPenaltyApplied = gearUpHardPenalty,
            PhaseScores = phaseScores
        };
    }

    private static void AppendGearGate(
        ChallengeConfig challenge,
        ScoringProfileConfig profile,
        LandingSnapshot snapshot,
        LandingEvaluationKey key,
        List<CriterionScore> criteriaScores,
        ref bool gearUpHardPenalty)
    {
        var gear = key.Gates?.Gear;
        if (gear is null)
            return;

        if (!challenge.RequireGearDown)
        {
            criteriaScores.Add(new CriterionScore
            {
                Id = gear.Id,
                DisplayName = "Gear (not required)",
                Weight = 0,
                Score01 = 1,
                RawValue = snapshot.GearDownAtTouchdown ? 1 : 0,
                Applied = false,
                Note = "Challenge allows gear-up (e.g. water/belly). Informational only."
            });
            return;
        }

        if (snapshot.GearDownAtTouchdown)
        {
            criteriaScores.Add(new CriterionScore
            {
                Id = gear.Id,
                DisplayName = "Gear (safety gate)",
                Weight = 0,
                Score01 = 1,
                RawValue = 1,
                Applied = false,
                Note = "Gear down — required baseline, no score credit."
            });
            return;
        }

        gearUpHardPenalty = true;
        var mult = gear.MultiplierOnFail > 0 && gear.MultiplierOnFail <= 1
            ? gear.MultiplierOnFail
            : 0.1;
        criteriaScores.Add(new CriterionScore
        {
            Id = gear.Id,
            DisplayName = "Gear UP — hard penalty",
            Weight = 0,
            Score01 = 0,
            RawValue = 0,
            Applied = true,
            Note =
                $"Gear up at touchdown. Overall score × {mult:0.##} (~{(1 - mult) * 100:0}% cut). " +
                (gear.PenaltyDescription ?? "")
        });
    }

    private ScoreResult EvaluateFlat(
        ChallengeConfig challenge,
        ScoringProfileConfig profile,
        LandingSnapshot snapshot,
        DifficultyLevel level)
    {
        // Legacy flat weighted average if no evaluation key
        var criteriaScores = new List<CriterionScore>();
        double weightedSum = 0;
        double weightTotal = 0;
        var gearUpHardPenalty = false;

        foreach (var criterion in profile.Criteria)
        {
            if (IsGearCriterion(criterion))
            {
                if (!challenge.RequireGearDown)
                {
                    criteriaScores.Add(new CriterionScore
                    {
                        Id = criterion.Id,
                        DisplayName = "Gear (not required)",
                        Weight = 0,
                        Score01 = 1,
                        Applied = false,
                        Note = "Gear not required for this challenge."
                    });
                }
                else if (snapshot.GearDownAtTouchdown)
                {
                    criteriaScores.Add(new CriterionScore
                    {
                        Id = criterion.Id,
                        DisplayName = "Gear (safety gate)",
                        Weight = 0,
                        Score01 = 1,
                        Applied = false,
                        Note = "Gear down — no score credit."
                    });
                }
                else
                {
                    gearUpHardPenalty = true;
                    criteriaScores.Add(new CriterionScore
                    {
                        Id = criterion.Id,
                        DisplayName = "Gear UP — hard penalty",
                        Weight = 0,
                        Score01 = 0,
                        Applied = true,
                        Note = "Gear up: overall score × 0.1."
                    });
                }

                continue;
            }

            if (!criterion.AppliesTo(level))
            {
                criteriaScores.Add(new CriterionScore
                {
                    Id = criterion.Id,
                    DisplayName = criterion.DisplayName,
                    Weight = criterion.Weight,
                    Score01 = 0,
                    Applied = false,
                    Note = MetricExplanations.For(criterion, snapshot, level, 0, null)
                });
                continue;
            }

            var raw = MetricResolver.Resolve(criterion.Metric, snapshot, challenge);
            var evaluator = EvaluatorFactory.Create(criterion.Evaluator);
            var score01 = raw is null ? 0.5 : evaluator.Evaluate(raw.Value, criterion, level);
            weightedSum += score01 * criterion.Weight;
            weightTotal += criterion.Weight;
            criteriaScores.Add(new CriterionScore
            {
                Id = criterion.Id,
                DisplayName = criterion.DisplayName,
                Weight = criterion.Weight,
                Score01 = score01,
                RawValue = raw,
                Unit = criterion.Unit,
                Note = MetricExplanations.For(criterion, snapshot, level, score01, raw),
                Applied = true
            });
        }

        var percentBefore = weightTotal > 0 ? weightedSum / weightTotal * 100 : 0;
        var percent = gearUpHardPenalty
            ? percentBefore * (profile.GearUpScoreMultiplier > 0 ? profile.GearUpScoreMultiplier : 0.1)
            : percentBefore;
        percent = Math.Round(Math.Clamp(percent, 0, 100), 1);

        return new ScoreResult
        {
            ChallengeId = challenge.Id,
            ChallengeTitle = challenge.Title,
            Level = level,
            ScorePercent = percent,
            Grade = ScoreResult.GradeFromPercent(percent),
            Criteria = criteriaScores,
            ScoredAtUtc = DateTimeOffset.UtcNow,
            Summary = "Flat profile (no evaluation key loaded).",
            ScoreBeforeGatesPercent = Math.Round(percentBefore, 1),
            GearUpPenaltyApplied = gearUpHardPenalty
        };
    }

    private static bool IsGearCriterion(CriterionConfig criterion) =>
        criterion.Id.Equals("gear", StringComparison.OrdinalIgnoreCase) ||
        criterion.Metric.Equals("gearDown", StringComparison.OrdinalIgnoreCase);

    private static bool IsIasErrorMetric(string metric) =>
        metric.ToLowerInvariant() is "touchdowniaserrorkts" or "ias_error_kts" or "touchdown_ias_error";

    private static bool IsExcessSpeedMetric(string metric) =>
        metric.ToLowerInvariant() is "excessspeedovervappkts" or "excess_over_vapp" or "excess_ias_kts";
}
