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
    public void Report_BuildsStructuredPhasesAndDistinctCompositeMetricCards()
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

        Assert.DoesNotContain("v9", report.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LEGACY", report.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.Single(report.Phases);
        Assert.Equal("90.0%", report.Phases[0].ScoreDisplay);
        Assert.Equal(4, report.Metrics.Count);
        Assert.Equal(2, report.DetailMetrics.Count);
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
    public void Report_FormatsTouchdownPointWithActualTargetAndEarlyLateError()
    {
        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Touchdown point",
            EvaluationKeyVersion = 19,
            ScoringProfileHash = "touchdown-point",
            RankedBucketId = "test|landing-evaluation-key|v19|touchdown-point",
            Criteria =
            {
                new HighscoreCriterionDetail
                {
                    Id = "touchdown_point",
                    DisplayName = "Touchdown point",
                    ScorePercent = 90,
                    RawValue = 1_125,
                    Unit = "ft",
                    Status = MetricStatus.Scored,
                    Note = "Measured: touchdown 1125.0 ft from threshold; perfect point 1200.0 ft; error -75.0 ft (early).",
                    PhaseId = "touchdown",
                    PhaseDisplayName = "Touchdown",
                    PhaseImportancePercent = 20,
                    PhaseWeightPercent = 70,
                    MaxOverallPoints = 14
                }
            }
        };

        var metric = Assert.Single(new LandingReportViewModel(entry).Metrics);

        Assert.Equal("1125.0 ft from threshold · target 1200.0 ft · error -75.0 ft (early)", metric.RawDisplay);
        Assert.Equal("20% of Touchdown · 14 max overall points", metric.InfluenceDisplay);
        Assert.Contains("perfect point 1200.0 ft", metric.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_SeparatesAllOperationalPenaltyCardsFromMetrics()
    {
        var ids = new[]
        {
            "spoiler_deployment", "manual_braking", "nose_gear_impact", "automation", "pause_usage", "simulation_rate",
            "cockpit_view", "rollout_distance", "reverse_thrust"
        };
        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Operational gates",
            EvaluationKeyVersion = 21,
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

        Assert.Empty(report.Metrics);
        Assert.True(report.HasPenalties);
        Assert.Equal(9, report.Penalties.Count);
        Assert.All(ids, id => Assert.Single(report.Penalties, penalty => penalty.Id == id));
        Assert.All(report.Penalties, penalty => Assert.Contains("multiplied", penalty.Note));
    }

    [Fact]
    public void Report_HidesSuccessfulGearAndFlapsAndHasNoPenaltySection()
    {
        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Clean landing",
            Criteria =
            {
                new HighscoreCriterionDetail
                {
                    Id = "gear", DisplayName = "Gear (not required)",
                    Status = MetricStatus.Informational, RawValue = 0
                },
                new HighscoreCriterionDetail
                {
                    Id = "flaps", DisplayName = "Flaps (safety gate)",
                    Status = MetricStatus.Informational, RawValue = 3
                },
                new HighscoreCriterionDetail
                {
                    Id = "touchdown_point", DisplayName = "Touchdown point",
                    Status = MetricStatus.Scored, RawValue = 1100, ScorePercent = 88, Unit = "ft"
                }
            }
        };

        var report = new LandingReportViewModel(entry);

        Assert.False(report.HasPenalties);
        Assert.Empty(report.Penalties);
        Assert.Single(report.Metrics);
        Assert.DoesNotContain(report.Metrics, metric => metric.Id is "gear" or "flaps");
    }

    [Fact]
    public void Report_ShowsRunwayLengthInHeaderWhenStored()
    {
        var entry = new HighscoreEntry
        {
            Utc = new DateTimeOffset(2026, 7, 16, 18, 48, 0, TimeSpan.Zero),
            ChallengeId = "free-mynn-28",
            ChallengeTitle = "Free · MYNN RWY 28",
            ScorePercent = 75.3,
            Grade = "B",
            RankedBucketId = "free-mynn-28|landing-evaluation-key|v9|hash",
            RunwayLengthMeters = 3353,
            Phases =
            {
                new HighscorePhaseDetail
                {
                    PhaseId = "touchdown", DisplayName = "Touchdown",
                    WeightPercent = 70, ScorePercent = 71.8, Used = true
                }
            },
            Criteria =
            {
                new HighscoreCriterionDetail
                {
                    Id = "touchdown_impact", DisplayName = "Touchdown impact",
                    ScorePercent = 80, RawValue = -421, Unit = "fpm",
                    Status = MetricStatus.Scored, PhaseId = "touchdown",
                    PhaseDisplayName = "Touchdown", PhaseImportancePercent = 55,
                    PhaseWeightPercent = 70, MaxOverallPoints = 38.5
                }
            }
        };

        var report = new LandingReportViewModel(entry);

        Assert.True(report.HasRunwayLength);
        Assert.Equal(3353, report.RunwayLengthMeters);
        Assert.Equal("3,353 m · 11,001 ft", report.RunwayLengthDisplay);

        var missing = new LandingReportViewModel(new HighscoreEntry
        {
            Utc = DateTimeOffset.UtcNow,
            ChallengeId = "x",
            ChallengeTitle = "No length",
            ScorePercent = 50,
            Grade = "C",
            RankedBucketId = "x|k|v9|h"
        });
        Assert.False(missing.HasRunwayLength);
        Assert.Equal("", missing.RunwayLengthDisplay);
    }

    [Fact]
    public void Report_RecoversRunwayLengthFromGateDiagnostics()
    {
        var entry = new HighscoreEntry
        {
            Utc = DateTimeOffset.UtcNow,
            ChallengeId = "test",
            ChallengeTitle = "Test",
            ScorePercent = 90,
            Grade = "A",
            RankedBucketId = "test|key|v9|h",
            Diagnostics = new LandingResultDiagnostics
            {
                OperationalGates = new LandingGateObservations { RunwayLengthMeters = 1628 }
            },
            Phases =
            {
                new HighscorePhaseDetail
                {
                    PhaseId = "touchdown", DisplayName = "Touchdown",
                    WeightPercent = 70, ScorePercent = 90, Used = true
                }
            }
        };

        var report = new LandingReportViewModel(entry);
        Assert.True(report.HasRunwayLength);
        Assert.Equal(1628, report.RunwayLengthMeters);
        Assert.Contains("1,628 m", report.RunwayLengthDisplay, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_DistinguishesAssumedAdjustmentsAndNotApplicableGates()
    {
        var entry = new HighscoreEntry
        {
            Utc = DateTimeOffset.UtcNow,
            ChallengeId = "free-test-01",
            ChallengeTitle = "Free test",
            ScorePercent = 61.2,
            Grade = "C",
            RankedBucketId = "free-test-01|free-flight-evaluation-key|v8|hash",
            Phases =
            {
                new HighscorePhaseDetail
                {
                    PhaseId = "touchdown", DisplayName = "Touchdown",
                    WeightPercent = 70, ScorePercent = 60, Used = true
                }
            },
            Criteria =
            {
                new HighscoreCriterionDetail
                {
                    Id = "spoiler_deployment", DisplayName = "Ground spoilers deployed",
                    Status = MetricStatus.Assumed, AppliedMultiplier = 0.95,
                    PhaseId = "touchdown", PhaseDisplayName = "Touchdown"
                },
                new HighscoreCriterionDetail
                {
                    Id = "touchdown_impact", DisplayName = "Touchdown impact",
                    Status = MetricStatus.Assumed, ScorePercent = 50,
                    PhaseId = "touchdown", PhaseDisplayName = "Touchdown",
                    PhaseImportancePercent = 54.4, PhaseWeightPercent = 70,
                    MaxOverallPoints = 38.08
                },
                new HighscoreCriterionDetail
                {
                    Id = "gear", DisplayName = "Gear - not applicable",
                    Status = MetricStatus.NotApplicable, AppliedMultiplier = 1,
                    PhaseId = "touchdown", PhaseDisplayName = "Touchdown"
                }
            }
        };

        var report = new LandingReportViewModel(entry);

        var adjustment = Assert.Single(report.Penalties);
        Assert.Equal("x 0.95", adjustment.MultiplierDisplay);
        Assert.Contains(report.Metrics, metric =>
            metric.Id == "touchdown_impact" && metric.Status == MetricStatus.Assumed
            && metric.Verdict == "ASSUMED" && metric.ScorePercent == 50);
        Assert.Contains(report.Metrics, metric =>
            metric.Id == "gear" && metric.Status == MetricStatus.NotApplicable
            && metric.Verdict == "N/A");
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
        Assert.Contains("x:Name=\"PenaltiesHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PhaseSummaryHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("LandingReport.ProjectedScoreHistory, Mode=OneWay", xaml, StringComparison.Ordinal);
        Assert.Contains("LandingReport.RunwayLengthDisplay", xaml, StringComparison.Ordinal);
        Assert.Contains("LandingReport.HasRunwayLength", xaml, StringComparison.Ordinal);
        Assert.Contains("MetricsHost.Children.Add(CreateMetricCard", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PenaltiesHost.Children.Add(CreatePenaltyCard", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding LandingReport.Notes}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Career\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Grade\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Profile\"", xaml, StringComparison.Ordinal);
    }
}
