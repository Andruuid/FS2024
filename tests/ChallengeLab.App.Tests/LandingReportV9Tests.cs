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
    public void Summary_OrdersPhasesAndWeightedMetricsByImportance()
    {
        static HighscoreCriterionDetail Metric(
            string id,
            string phaseId,
            double importance,
            MetricStatus status,
            double? score) => new()
        {
            Id = id,
            DisplayName = id.Replace('_', ' '),
            PhaseId = phaseId,
            PhaseDisplayName = phaseId,
            PhaseImportancePercent = importance,
            Status = status,
            ScorePercent = score,
            RawValue = score,
            Note = $"Explanation for {id}."
        };

        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Summary ordering",
            Phases =
            {
                new HighscorePhaseDetail
                    { PhaseId = "rollout", DisplayName = "Rollout", WeightPercent = 5, ScorePercent = 79.5 },
                new HighscorePhaseDetail
                    { PhaseId = "touchdown", DisplayName = "Touchdown", WeightPercent = 70, ScorePercent = 56.5 },
                new HighscorePhaseDetail
                    { PhaseId = "approach", DisplayName = "Approach", WeightPercent = 25, ScorePercent = 76.7 }
            },
            Criteria =
            {
                Metric("touchdown_point", "touchdown", 20, MetricStatus.Scored, 70),
                Metric("touchdown_impact", "touchdown", 54.4, MetricStatus.Scored, 62.1),
                Metric("touchdown_gate", "touchdown", 60, MetricStatus.GateFailed, null),
                Metric("touchdown_info", "touchdown", 50, MetricStatus.Informational, 100),
                Metric("touchdown_na", "touchdown", 40, MetricStatus.NotApplicable, null),
                Metric("approach_degraded", "approach", 26.67, MetricStatus.Degraded, 80),
                Metric("rollout_assumed", "rollout", 25, MetricStatus.Assumed, 50),
                Metric("rollout_unavailable", "rollout", 20, MetricStatus.Unavailable, null)
            }
        };

        var report = new LandingReportViewModel(entry);

        Assert.Equal(new[] { "touchdown", "approach", "rollout" },
            report.SummaryPhases.Select(phase => phase.PhaseId));
        Assert.Equal(new[] { "touchdown_impact", "touchdown_point" },
            report.SummaryPhases[0].Metrics.Select(metric => metric.Id));
        Assert.Equal(MetricStatus.Degraded, Assert.Single(report.SummaryPhases[1].Metrics).Status);
        Assert.Equal(
            new[] { MetricStatus.Assumed, MetricStatus.Unavailable },
            report.SummaryPhases[2].Metrics.Select(metric => metric.Status));
        Assert.DoesNotContain(report.SummaryPhases.SelectMany(phase => phase.Metrics), metric =>
            metric.Status is MetricStatus.GateFailed or MetricStatus.Informational or MetricStatus.NotApplicable);
    }

    [Theory]
    [InlineData(80.1, SummaryScoreBand.Green)]
    [InlineData(80d, SummaryScoreBand.Orange)]
    [InlineData(70d, SummaryScoreBand.Orange)]
    [InlineData(69.9, SummaryScoreBand.Red)]
    [InlineData(null, SummaryScoreBand.Unavailable)]
    public void Summary_UsesLiteralScoreBandBoundaries(double? score, SummaryScoreBand expected)
    {
        var metric = new SummaryMetricViewModel(new HighscoreCriterionDetail
        {
            Id = "metric",
            DisplayName = "Metric",
            PhaseId = "touchdown",
            PhaseDisplayName = "Touchdown",
            PhaseImportancePercent = 25,
            Status = score is null ? MetricStatus.Unavailable : MetricStatus.Scored,
            ScorePercent = score
        });

        Assert.Equal(expected, metric.ScoreBand);
        Assert.Equal(score is null ? "N/A" : ScoreBreakdownFormatter.Pct(score), metric.ScoreDisplay);
    }

    [Theory]
    [InlineData("airspeed", "IAS versus touchdown target", "On speed")]
    [InlineData("flare_efficiency", "Flare and float efficiency", "Flare & float")]
    [InlineData("approach_glideslope", "Average glideslope match", "Glideslope")]
    [InlineData("max_centerline", "Max lateral deviation (rollout)", "Max deviation")]
    public void Summary_UsesCompactMetricTitles(string id, string storedTitle, string expectedTitle)
    {
        var metric = new SummaryMetricViewModel(new HighscoreCriterionDetail
        {
            Id = id,
            DisplayName = storedTitle,
            PhaseImportancePercent = 25,
            Status = MetricStatus.Scored,
            ScorePercent = 90
        });

        Assert.Equal(expectedTitle, metric.DisplayName);
    }

    [Fact]
    public void Summary_ExplainsPhasePenaltyMathAndTouchdownPointContext()
    {
        HighscoreCriterionDetail RolloutMetric(string id, double score) => new()
        {
            Id = id,
            DisplayName = id,
            PhaseId = "rollout",
            PhaseDisplayName = "Rollout",
            PhaseImportancePercent = 25,
            Status = MetricStatus.Scored,
            ScorePercent = score
        };

        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Penalty math",
            ScorePercent = 79.5,
            Phases =
            {
                new HighscorePhaseDetail
                    { PhaseId = "rollout", DisplayName = "Rollout", WeightPercent = 100, ScorePercent = 79.5 }
            },
            Criteria =
            {
                RolloutMetric("post_td_alignment", 100),
                RolloutMetric("rollout_path", 100),
                RolloutMetric("rollout_weave", 92.8),
                RolloutMetric("max_centerline", 100),
                new HighscoreCriterionDetail
                {
                    Id = "manual_braking", DisplayName = "Manual braking penalty",
                    PhaseId = "rollout", PhaseDisplayName = "Rollout",
                    Status = MetricStatus.GateFailed, AppliedMultiplier = .9,
                    Note = "Both pedals were not applied in time."
                },
                new HighscoreCriterionDetail
                {
                    Id = "reverse_thrust", DisplayName = "Reverse thrust penalty",
                    PhaseId = "rollout", PhaseDisplayName = "Rollout",
                    Status = MetricStatus.GateFailed, AppliedMultiplier = .9,
                    Note = "Reverse thrust was not selected in time."
                }
            }
        };

        var phase = Assert.Single(new LandingReportViewModel(entry).SummaryPhases);
        var chain = Assert.IsType<SummaryPenaltyChainViewModel>(phase.PenaltyChain);

        Assert.Equal(98.2, chain.RawScorePercent);
        Assert.Equal("98.2%", chain.RawScoreDisplay);
        Assert.Equal("79.5%", chain.FinalScoreDisplay);
        Assert.Equal("−18.7 pts", chain.PointLossDisplay);
        Assert.Equal(new[] { "MANUAL BRAKES", "REVERSE THRUST" },
            chain.Penalties.Select(penalty => penalty.DisplayName));
        Assert.All(chain.Penalties, penalty => Assert.Equal("×0.9", penalty.MultiplierDisplay));

        var touchdownPoint = new SummaryMetricViewModel(new HighscoreCriterionDetail
        {
            Id = "touchdown_point",
            DisplayName = "Touchdown point",
            PhaseId = "touchdown",
            PhaseDisplayName = "Touchdown",
            PhaseImportancePercent = 20,
            Status = MetricStatus.Scored,
            ScorePercent = 0,
            RawValue = 619.8,
            Unit = "ft",
            Note = "Measured: touchdown 619.8 ft from threshold; perfect point 1200.0 ft; error -580.2 ft (early)."
        });
        Assert.Equal("580 ft early", touchdownPoint.DetailDisplay);
    }

    [Fact]
    public void Summary_ExplainsOverallPenaltyMath()
    {
        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Overall penalty",
            ScorePercent = 81,
            Phases =
            {
                new HighscorePhaseDetail
                    { PhaseId = "touchdown", DisplayName = "Touchdown", WeightPercent = 100, ScorePercent = 90 }
            },
            Criteria =
            {
                new HighscoreCriterionDetail
                {
                    Id = "pause_usage", DisplayName = "Pause penalty",
                    Status = MetricStatus.GateFailed, AppliedMultiplier = .9,
                    Note = "Pause was used during the attempt."
                }
            }
        };

        var chain = Assert.IsType<SummaryPenaltyChainViewModel>(
            new LandingReportViewModel(entry).OverallPenaltyChain);

        Assert.Equal("90%", chain.RawScoreDisplay);
        Assert.Equal("81%", chain.FinalScoreDisplay);
        Assert.Equal("PAUSE", Assert.Single(chain.Penalties).DisplayName);
    }

    [Fact]
    public void Summary_TooltipsExplainMetricsAndPhasePenaltySemantics()
    {
        var entry = new HighscoreEntry
        {
            ChallengeTitle = "Summary tooltips",
            Phases =
            {
                new HighscorePhaseDetail
                    { PhaseId = "touchdown", DisplayName = "Touchdown", WeightPercent = 70, ScorePercent = 74 }
            },
            Criteria =
            {
                new HighscoreCriterionDetail
                {
                    Id = "touchdown_point",
                    DisplayName = "Touchdown point",
                    PhaseId = "touchdown",
                    PhaseDisplayName = "Touchdown",
                    PhaseImportancePercent = 20,
                    Status = MetricStatus.Scored,
                    ScorePercent = 75,
                    RawValue = 120,
                    Unit = "ft",
                    Note = "Distance from the ideal touchdown point."
                }
            }
        };

        var phase = Assert.Single(new LandingReportViewModel(entry).SummaryPhases);
        var metric = Assert.Single(phase.Metrics);

        Assert.Contains("after phase-specific penalties", phase.ToolTip, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("70%", phase.ToolTip, StringComparison.Ordinal);
        Assert.Contains("Distance from the ideal touchdown point", metric.ToolTip, StringComparison.Ordinal);
        Assert.Contains("Measured value: 120.0 ft from threshold", metric.ToolTip, StringComparison.Ordinal);
        Assert.Contains("Status: Scored", metric.ToolTip, StringComparison.Ordinal);
        Assert.DoesNotContain("% of Touchdown", metric.ToolTip, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_HandlesLegacyAndPartiallyStoredResults()
    {
        var legacy = new LandingReportViewModel(new HighscoreEntry
        {
            ChallengeTitle = "Legacy",
            ScorePercent = 50
        });

        Assert.False(legacy.HasSummary);
        Assert.Empty(legacy.SummaryPhases);
        Assert.Contains("historical result", legacy.SummaryUnavailableText, StringComparison.OrdinalIgnoreCase);

        var partial = new LandingReportViewModel(new HighscoreEntry
        {
            ChallengeTitle = "Partial",
            Phases =
            {
                new HighscorePhaseDetail
                    { PhaseId = "approach", DisplayName = "Approach", WeightPercent = 25, ScorePercent = null }
            }
        });

        var phase = Assert.Single(partial.SummaryPhases);
        Assert.Equal("N/A", phase.ScoreDisplay);
        Assert.Equal(SummaryScoreBand.Unavailable, phase.ScoreBand);
        Assert.Empty(phase.Metrics);
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

    [Theory]
    [InlineData("inside ideal band")]
    [InlineData("200.0 ft short")]
    [InlineData("200.0 ft long")]
    public void Report_FormatsVersionTwentySevenTouchdownBand(string position)
    {
        var criterion = new HighscoreCriterionDetail
        {
            Id = "touchdown_point",
            DisplayName = "Touchdown point",
            ScorePercent = 75,
            RawValue = 1_100,
            Unit = "ft",
            Status = MetricStatus.Scored,
            Note = $"Measured: touchdown 1100.0 ft from threshold; aiming marker 1000.0 ft; ideal band 1300.0-1500.0 ft; {position}.",
            PhaseId = "touchdown",
            PhaseDisplayName = "Touchdown",
            PhaseImportancePercent = 19,
            PhaseWeightPercent = 70,
            MaxOverallPoints = 13.3
        };

        var reportMetric = new ReportMetricViewModel(criterion, false);
        var summaryMetric = new SummaryMetricViewModel(criterion);

        Assert.Equal(
            $"1100.0 ft from threshold · marker 1000.0 ft · ideal 1300.0-1500.0 ft · {position}",
            reportMetric.RawDisplay);
        Assert.Equal(position, summaryMetric.DetailDisplay);
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
        Assert.Contains("Header=\"SUMMARY\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SummaryHost\"", xaml, StringComparison.Ordinal);
        Assert.True(
            xaml.IndexOf("Header=\"SUMMARY\"", StringComparison.Ordinal) <
            xaml.IndexOf("Header=\"LANDING VISUAL\"", StringComparison.Ordinal));
        Assert.Contains("LandingReport.ProjectedScoreHistory, Mode=OneWay", xaml, StringComparison.Ordinal);
        Assert.Contains("LandingReport.RunwayLengthDisplay", xaml, StringComparison.Ordinal);
        Assert.Contains("LandingReport.HasRunwayLength", xaml, StringComparison.Ordinal);
        Assert.Contains("MetricsHost.Children.Add(CreateMetricCard", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PenaltiesHost.Children.Add(CreatePenaltyCard", codeBehind, StringComparison.Ordinal);
        Assert.Contains("SummaryHost.Children.Add(CreateSummaryPhaseSection", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateSummaryBarRow", codeBehind, StringComparison.Ordinal);
        Assert.Contains("CreateSummaryPenaltyChain", codeBehind, StringComparison.Ordinal);
        Assert.Contains("PENALTY MATH", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("metric.ImportanceDisplay", codeBehind, StringComparison.Ordinal);
        Assert.Contains("0x62, 0xE6, 0xA7", codeBehind, StringComparison.Ordinal);
        Assert.Contains("0xFF, 0xB0, 0x20", codeBehind, StringComparison.Ordinal);
        Assert.Contains("0xFF, 0x4D, 0x6A", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"{Binding LandingReport.Notes}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Career\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Grade\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Profile\"", xaml, StringComparison.Ordinal);
    }
}
