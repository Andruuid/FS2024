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

        foreach (var criterion in profile.Criteria)
        {
            if (!criterion.AppliesTo(level))
            {
                criteriaScores.Add(new CriterionScore
                {
                    Id = criterion.Id,
                    DisplayName = criterion.DisplayName,
                    Weight = criterion.Weight,
                    Score01 = 0,
                    Applied = false,
                    Note = "Not evaluated on this difficulty"
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
                score01 = evaluator.Evaluate(raw.Value, criterion);
                if (criterion.FailIfOutside && score01 <= 0)
                    score01 = 0;
            }

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
                Note = criterion.Note,
                Applied = true
            });
        }

        var percent = weightTotal > 0 ? (weightedSum / weightTotal) * 100.0 : 0;
        percent = Math.Round(Math.Clamp(percent, 0, 100), 1);

        var worst = criteriaScores.Where(c => c.Applied).OrderBy(c => c.Score01).FirstOrDefault();
        var best = criteriaScores.Where(c => c.Applied).OrderByDescending(c => c.Score01).FirstOrDefault();
        var summary = best is null
            ? "No criteria evaluated."
            : $"Strongest: {best.DisplayName} ({best.ScorePercent:0}%). Weakest: {worst!.DisplayName} ({worst.ScorePercent:0}%).";

        return new ScoreResult
        {
            ChallengeId = challenge.Id,
            ChallengeTitle = challenge.Title,
            Level = level,
            ScorePercent = percent,
            Grade = ScoreResult.GradeFromPercent(percent),
            Criteria = criteriaScores,
            ScoredAtUtc = DateTimeOffset.UtcNow,
            Summary = summary
        };
    }
}
