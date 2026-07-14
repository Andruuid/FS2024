using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Scoring;

public sealed class ScoreEngine
{
    public ScoreResult Evaluate(
        ChallengeConfig challenge,
        ScoringProfileConfig profile,
        LandingSnapshot snapshot,
        DifficultyLevel level)
    {
        var criteriaScores = new List<CriterionScore>();
        double weightedSum = 0;
        double weightTotal = 0;
        var gearUpHardPenalty = false;

        foreach (var criterion in profile.Criteria)
        {
            // --- Gear: safety gate, not free points ---
            if (IsGearCriterion(criterion))
            {
                HandleGearGate(challenge, profile, snapshot, level, criterion, criteriaScores, ref gearUpHardPenalty);
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
                    Unit = criterion.Unit,
                    Note = MetricExplanations.For(criterion, snapshot, level, 0, null)
                });
                continue;
            }

            var raw = MetricResolver.Resolve(criterion.Metric, snapshot, challenge);
            var evaluator = EvaluatorFactory.Create(criterion.Evaluator);
            double score01;
            if (raw is null)
            {
                score01 = 0.5; // unknown metric — neutral rather than fail hard
            }
            else
            {
                score01 = evaluator.Evaluate(raw.Value, criterion, level);
                if (criterion.FailIfOutside && score01 <= 0)
                    score01 = 0;
            }

            weightedSum += score01 * criterion.Weight;
            weightTotal += criterion.Weight;

            var displayRaw = raw;
            if (IsIasErrorMetric(criterion.Metric))
                displayRaw = snapshot.AirspeedAtTouchdownKts;
            else if (IsExcessSpeedMetric(criterion.Metric))
                displayRaw = snapshot.ExcessSpeedOverVappKts;

            var explanation = MetricExplanations.For(criterion, snapshot, level, score01, raw);

            criteriaScores.Add(new CriterionScore
            {
                Id = criterion.Id,
                DisplayName = criterion.DisplayName,
                Weight = criterion.Weight,
                Score01 = score01,
                RawValue = displayRaw,
                Unit = criterion.Unit,
                Note = explanation,
                Applied = true
            });
        }

        var percentBeforeGate = weightTotal > 0 ? (weightedSum / weightTotal) * 100.0 : 0;
        percentBeforeGate = Math.Clamp(percentBeforeGate, 0, 100);

        var percent = percentBeforeGate;
        string? gateNote = null;
        if (gearUpHardPenalty)
        {
            var mult = profile.GearUpScoreMultiplier;
            if (mult <= 0 || mult > 1) mult = 0.1;
            percent = percentBeforeGate * mult;
            var cutPct = (1.0 - mult) * 100.0;
            gateNote =
                $"GEAR UP: overall score cut by {cutPct:0}% " +
                $"({percentBeforeGate:0.0}% → {percent:0.0}%). " +
                "Gear-down is a required baseline on this challenge — it never awards bonus points.";
        }

        percent = Math.Round(Math.Clamp(percent, 0, 100), 1);

        // Strongest/weakest ignore gates that don't participate in the average
        var scored = criteriaScores.Where(c => c.Applied && c.Weight > 0).ToList();
        var worst = scored.OrderBy(c => c.Score01).FirstOrDefault();
        var best = scored.OrderByDescending(c => c.Score01).FirstOrDefault();
        var summary = best is null
            ? (gateNote ?? "No criteria evaluated.")
            : $"Strongest: {best.DisplayName} ({best.ScorePercent:0}%). Weakest: {worst!.DisplayName} ({worst.ScorePercent:0}%).";
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
            Summary = summary,
            ScoreBeforeGatesPercent = Math.Round(percentBeforeGate, 1),
            GearUpPenaltyApplied = gearUpHardPenalty
        };
    }

    private static void HandleGearGate(
        ChallengeConfig challenge,
        ScoringProfileConfig profile,
        LandingSnapshot snapshot,
        DifficultyLevel level,
        CriterionConfig criterion,
        List<CriterionScore> criteriaScores,
        ref bool gearUpHardPenalty)
    {
        // Challenge does not require gear (belly / water / intentional gear-up)
        if (!challenge.RequireGearDown)
        {
            criteriaScores.Add(new CriterionScore
            {
                Id = criterion.Id,
                DisplayName = "Gear (not required)",
                Weight = 0,
                Score01 = 1,
                RawValue = snapshot.GearDownAtTouchdown ? 1 : 0,
                Applied = false,
                Note =
                    "This challenge allows gear-up (e.g. water / belly landing). " +
                    "Gear state is informational only and does not affect the score. " +
                    (snapshot.GearDownAtTouchdown ? "Measured: gear down." : "Measured: gear up.")
            });
            return;
        }

        if (snapshot.GearDownAtTouchdown)
        {
            // Pass: no credit, not in weighted average, not "strongest"
            criteriaScores.Add(new CriterionScore
            {
                Id = criterion.Id,
                DisplayName = "Gear (safety gate)",
                Weight = 0,
                Score01 = 1,
                RawValue = 1,
                Applied = false,
                Note =
                    "Gear down at touchdown — required baseline for this challenge. " +
                    "No score credit is given for something this basic; only a gear-up failure is penalized."
            });
            return;
        }

        // Fail: hard overall penalty
        gearUpHardPenalty = true;
        var mult = profile.GearUpScoreMultiplier;
        if (mult <= 0 || mult > 1) mult = 0.1;
        var cutPct = (1.0 - mult) * 100.0;

        criteriaScores.Add(new CriterionScore
        {
            Id = criterion.Id,
            DisplayName = "Gear UP — hard penalty",
            Weight = 0,
            Score01 = 0,
            RawValue = 0,
            Applied = true, // show prominently in report
            Note =
                $"Gear was up at touchdown. Instant overall score reduction of ~{cutPct:0}% " +
                $"(final score × {mult:0.##}). " +
                "Gear-down never adds points when correct — it only punishes a wheels-up landing. " +
                "Challenges that intentionally land gear-up set requireGearDown=false."
        });
    }

    private static bool IsGearCriterion(CriterionConfig criterion) =>
        criterion.Id.Equals("gear", StringComparison.OrdinalIgnoreCase) ||
        criterion.Metric.Equals("gearDown", StringComparison.OrdinalIgnoreCase) ||
        criterion.Metric.Equals("geardown", StringComparison.OrdinalIgnoreCase);

    private static bool IsIasErrorMetric(string metric) =>
        metric.ToLowerInvariant() is "touchdowniaserrorkts" or "ias_error_kts" or "touchdown_ias_error";

    private static bool IsExcessSpeedMetric(string metric) =>
        metric.ToLowerInvariant() is "excessspeedovervappkts" or "excess_over_vapp" or "excess_ias_kts";
}
