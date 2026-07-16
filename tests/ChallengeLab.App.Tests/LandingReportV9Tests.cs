using System.IO;
using ChallengeLab.App.ViewModels;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Tests;

public sealed class LandingReportV9Tests
{
    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ChallengeLab.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
    }

    [Fact]
    public void Report_LabelsProfileAndBuildsDistinctCompositeMetricCards()
    {
        HighscoreCriterionDetail Metric(string id, string name, double score, string note) => new()
        {
            Id = id,
            DisplayName = name,
            ScorePercent = score,
            RawValue = score,
            Unit = "%",
            Note = note,
            Status = MetricStatus.Scored,
            PhaseId = "touchdown",
            PhaseDisplayName = "Touchdown",
            PhaseImportancePercent = id == "touchdown_impact" ? 55 : 10,
            PhaseWeightPercent = 70,
            MaxOverallPoints = id == "touchdown_impact" ? 38.5 : 7
        };

        var entry = new HighscoreEntry
        {
            Utc = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
            ChallengeId = "test",
            ChallengeTitle = "V9 landing",
            ScorePercent = 91,
            Grade = "A",
            EvaluationKeyId = "landing-evaluation-key",
            EvaluationKeyVersion = 9,
            ScoringProfileHash = "abc123",
            RankedBucketId = "test|landing-evaluation-key|v9|abc123",
            Diagnostics = new LandingResultDiagnostics
            {
                TouchdownVerticalSpeedFpm = -218,
                TouchdownVerticalSpeedSubscore = 96,
                TouchdownRobustPeakG = 1.34,
                TouchdownPeakGSubscore = 90,
                TouchdownImpactScore = 92,
                FloatDetected = true,
                FloatDistanceM = 245,
                FloatSeconds = 3.2,
                BounceCount = 1,
                MaximumBounceAirborneSeconds = .24
            },
            Phases =
            {
                new HighscorePhaseDetail
                {
                    PhaseId = "touchdown", DisplayName = "Touchdown",
                    WeightPercent = 70, ScorePercent = 90, Used = true
                }
            },
            Criteria =
            {
                Metric("touchdown_impact", "Touchdown impact", 92,
                    "Vertical speed -218 fpm — 96%; robust peak 1.34 g — 90%."),
                Metric("flare_efficiency", "Flare and float efficiency", 61,
                    "Float distance 245 m; duration 3.2 s."),
                Metric("contact_stability", "Contact stability", 65,
                    "1 valid bounce; maximum airborne 0.24 s."),
                Metric("airspeed", "IAS versus touchdown target", 90,
                    "Measured 140 kt; target 138 kt; VAPP 143.")
            }
        };

        var report = new LandingReportViewModel(entry);

        Assert.Contains("v9", report.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LEGACY", report.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(4, report.Metrics.Count);
        Assert.Equal(4, report.Metrics.Select(m => m.Id).Distinct().Count());
        Assert.Single(report.Metrics, m => m.Id == "touchdown_impact");
        Assert.Equal(92, report.VerticalSpeedScorePercent);
        Assert.Contains("1.34", report.VerticalSpeedHeadline, StringComparison.Ordinal);
        Assert.Contains("Float distance", report.Metrics.Single(m => m.Id == "flare_efficiency").Note);
        Assert.Contains("bounce", report.Metrics.Single(m => m.Id == "contact_stability").Note,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_DegradedMetricRemainsVisibleAndExplainable()
    {
        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Degraded",
            EvaluationKeyVersion = 9,
            ScoringProfileHash = "hash",
            RankedBucketId = "bucket",
            Criteria =
            {
                new HighscoreCriterionDetail
                {
                    Id = "touchdown_impact",
                    DisplayName = "Touchdown impact",
                    ScorePercent = 70,
                    RawValue = 70,
                    Unit = "%",
                    Status = MetricStatus.Degraded,
                    Note = "Vertical-speed-only fallback; G coverage was insufficient."
                }
            }
        };
        var report = new LandingReportViewModel(entry);
        var metric = Assert.Single(report.Metrics);
        Assert.Equal(MetricStatus.Degraded, metric.Status);
        Assert.Equal("DEGRADED", metric.Verdict);
        Assert.True(metric.IsScored);
        Assert.Contains("fallback", metric.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Report_ShowsAllOperationalGateCardsWithFailureDetails()
    {
        var ids = new[]
        {
            "spoiler_deployment", "manual_braking", "nose_gear_impact", "automation", "pause_usage", "simulation_rate", "rollout_distance"
        };
        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Operational gates",
            EvaluationKeyVersion = 17,
            ScoringProfileHash = "gates",
            RankedBucketId = "test|landing-evaluation-key|v17|gates",
            Criteria = ids.Select(id => new HighscoreCriterionDetail
            {
                Id = id,
                DisplayName = id.Replace('_', ' '),
                Status = MetricStatus.GateFailed,
                RawValue = 1,
                Note = $"{id} failed; ranked overall score multiplied."
            }).ToList()
        };

        var report = new LandingReportViewModel(entry);

        Assert.Equal(7, report.Metrics.Count);
        Assert.All(ids, id => Assert.Single(report.Metrics, metric => metric.Id == id));
        Assert.All(report.Metrics, metric => Assert.Equal("FAILED GATE", metric.Verdict));
        Assert.All(report.Metrics, metric => Assert.Contains("multiplied", metric.Note));
    }

    [Fact]
    public void ReportXaml_KeepsReadOnlyProgressBindingsOneWayAndCodePaintedCards()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "ChallengeLab.App", "Views", "MainWindow.xaml"));
        var codeBehind = File.ReadAllText(Path.Combine(root, "src", "ChallengeLab.App", "Views", "MainWindow.xaml.cs"));
        Assert.Contains("LandingReport.VerticalSpeedScorePercent, Mode=OneWay", xaml, StringComparison.Ordinal);
        Assert.Contains("LandingReport.AirspeedScorePercent, Mode=OneWay", xaml, StringComparison.Ordinal);
        Assert.Contains("BarValue, Mode=OneWay", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MetricsHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MetricsHost.Children.Add(CreateMetricCard", codeBehind, StringComparison.Ordinal);
    }
}
