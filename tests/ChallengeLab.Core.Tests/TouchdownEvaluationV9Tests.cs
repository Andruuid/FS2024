using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class TouchdownEvaluationV9Tests
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

    private static (LandingEvaluationKey Key, ChallengeConfig Challenge) Load()
    {
        var root = FindRepositoryRoot();
        var loader = new ConfigLoader(Path.Combine(root, "config"));
        var loaded = loader.LoadEvaluationKey();
        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        return (loaded.Key!, loader.LoadChallenge("challenges/barcelona-crosswind-final.json"));
    }

    private static LandingTelemetrySample Sample(
        double time,
        double agl = 0,
        double groundSpeed = 100,
        double verticalSpeed = -200,
        double g = 1,
        bool left = false,
        bool right = false,
        bool contactsAvailable = true) =>
        new(time, agl, groundSpeed, verticalSpeed, g, left, right, false, contactsAvailable);

    private static ImpactAnalysis Impact(double vs, double g) =>
        new(true, false, 10, vs, "PLANE TOUCHDOWN NORMAL VELOCITY", g, g, 12, 1, null);

    [Fact]
    public void ShippedKeys_HaveExpectedCompositeWeightsAndNoActiveLegacyMetric()
    {
        var root = FindRepositoryRoot();
        var loader = new ConfigLoader(Path.Combine(root, "config"));
        var catalog = loader.LoadCatalog();
        foreach (var (path, version) in new[]
                 {
                     (catalog.EvaluationKey, 21),
                     (catalog.FreeFlightEvaluationKey, 7)
                 })
        {
            var loaded = loader.LoadEvaluationKey(path);
            Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
            Assert.Equal(version, loaded.Key!.Version);
            Assert.Equal(100, loaded.Key.Phases.Sum(p => p.WeightPercent), 6);
            var touchdown = loaded.Key.Phases.Single(p => p.Id == "touchdown");
            Assert.Equal(100, touchdown.Metrics.Sum(m => m.ImportancePercent), 6);
            var isNormal = path == catalog.EvaluationKey;
            Assert.Equal(isNormal ? 54.4 : 44,
                touchdown.Metrics.Single(m => m.Id == "touchdown_impact").ImportancePercent);
            Assert.Equal(20, touchdown.Metrics.Single(m => m.Id == "touchdown_point").ImportancePercent);
            Assert.Equal(8, touchdown.Metrics.Single(m => m.Id == "flare_efficiency").ImportancePercent);
            if (isNormal)
            {
                Assert.DoesNotContain(touchdown.Metrics, m => m.Id == "contact_stability");
                Assert.DoesNotContain(touchdown.Metrics, m => m.Id == "ground_track");
            }
            else
                Assert.Equal(8, touchdown.Metrics.Single(m => m.Id == "contact_stability").ImportancePercent);
            Assert.DoesNotContain(touchdown.Metrics, m => m.Id == "touchdown_vs");
            Assert.Equal(70, touchdown.WeightPercent);
            Assert.Equal(25, loaded.Key.Phases.Single(p => p.Id == "approach").WeightPercent);
            Assert.Equal(5, loaded.Key.Phases.Single(p => p.Id == "rollout").WeightPercent);
        }

        var normal = loader.LoadEvaluationKey(catalog.EvaluationKey).Key!;
        var touchdownPenalties = normal.Phases.Single(p => p.Id == "touchdown").Penalties!;
        var approachPenalties = normal.Phases.Single(p => p.Id == "approach").Penalties!;
        Assert.Equal(0.1, touchdownPenalties.Gear!.MultiplierOnFail, 6);
        Assert.Equal(0.9, touchdownPenalties.ContactStability!.OneBounceMultiplier, 6);
        Assert.Equal(0.8, touchdownPenalties.ContactStability.TwoOrMoreBouncesMultiplier, 6);
        Assert.Equal(0.5, approachPenalties.StallWarning!.MultiplierOnWarning, 6);
        Assert.Equal(2, touchdownPenalties.Flaps!.MinIndex, 6);
        Assert.Equal(5, touchdownPenalties.Flaps.MaxIndex, 6);
        Assert.Equal(0.8, touchdownPenalties.Flaps.MultiplierOnFail, 6);
        var free = loader.LoadEvaluationKey(catalog.FreeFlightEvaluationKey).Key!;
        var freeTouchdownPenalties = free.Phases.Single(p => p.Id == "touchdown").Penalties!;
        Assert.Equal(0.1, freeTouchdownPenalties.Gear!.MultiplierOnFail, 6);
        Assert.Null(freeTouchdownPenalties.Flaps);
    }

    [Fact]
    public void EffectiveProfile_DeepMergesKnownValuesAndRejectsUnknownOnes()
    {
        var (key, challenge) = Load();
        challenge.ScoringOverrides = new ChallengeScoringOverrides
        {
            Metrics =
            {
                new EvaluationMetricOverride
                {
                    Id = "touchdown_impact",
                    Params = { ["peakGWeight"] = 2 },
                    Curves =
                    {
                        ["peakG"] = new()
                        {
                            new ScorePoint { V = 1, S = 100 },
                            new ScorePoint { V = 2, S = 0 }
                        }
                    }
                }
            }
        };

        var effective = EffectiveEvaluationProfileBuilder.Build(key, challenge);
        var impact = effective.Key.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "touchdown_impact");
        Assert.Equal(2, impact.Params["peakGWeight"]);
        Assert.Equal(0.4, impact.Params["verticalSpeedWeight"]);
        Assert.Equal(2, impact.Curves["peakG"].Count);
        Assert.NotEqual(2, key.Phases.SelectMany(p => p.Metrics)
            .Single(m => m.Id == "touchdown_impact").Params["peakGWeight"]);

        challenge.ScoringOverrides.Metrics[0].Params["notAParameter"] = 1;
        var error = Assert.Throws<ArgumentException>(() => EffectiveEvaluationProfileBuilder.Build(key, challenge));
        Assert.Contains("unknown parameter", error.Message, StringComparison.OrdinalIgnoreCase);

        challenge.ScoringOverrides.Metrics[0].Params.Remove("notAParameter");
        challenge.ScoringOverrides.Metrics[0].Curves["notACurve"] =
            new() { new ScorePoint(), new ScorePoint { V = 1, S = 100 } };
        error = Assert.Throws<ArgumentException>(() => EffectiveEvaluationProfileBuilder.Build(key, challenge));
        Assert.Contains("unknown curve", error.Message, StringComparison.OrdinalIgnoreCase);

        challenge.ScoringOverrides.Metrics[0].Curves.Remove("notACurve");
        challenge.ScoringOverrides.Metrics[0].Id = "not_a_metric";
        error = Assert.Throws<ArgumentException>(() => EffectiveEvaluationProfileBuilder.Build(key, challenge));
        Assert.Contains("unknown metric", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompositeWeightValidation_RejectsNegativeAndZeroTotalWeights()
    {
        var (negative, _) = Load();
        var impact = negative.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "touchdown_impact");
        impact.Params["peakGWeight"] = -1;
        Assert.Contains(EvaluationKeyValidator.Validate(negative),
            error => error.Contains("nonnegative", StringComparison.OrdinalIgnoreCase));

        var (zero, _) = Load();
        impact = zero.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "touchdown_impact");
        impact.Params["verticalSpeedWeight"] = 0;
        impact.Params["peakGWeight"] = 0;
        Assert.Contains(EvaluationKeyValidator.Validate(zero),
            error => error.Contains("at least one positive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void KeyLoader_DisallowsUnknownSerializedMembers()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root, "config", "scoring", "profiles", "landing-evaluation-key.json"));
        var path = Path.Combine(Path.GetTempPath(), $"challenge-key-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, source.Insert(source.IndexOf('{') + 1,
                "\n  \"unknownScoreField\": 123,"));
            var loaded = new ConfigLoader(Path.Combine(root, "config")).LoadEvaluationKey(path);
            Assert.False(loaded.IsValid);
            Assert.Contains(loaded.Errors,
                error => error.Contains("unmapped", StringComparison.OrdinalIgnoreCase)
                         || error.Contains("could not be mapped", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void EvaluationIdentity_SeparatesVersionsAndProfileHashes()
    {
        var (v11, challenge) = Load();
        var profile11 = EffectiveEvaluationProfileBuilder.Build(v11, challenge);
        var (v8, _) = Load();
        v8.Version = 8;
        var profile8 = EffectiveEvaluationProfileBuilder.Build(v8, challenge);

        Assert.NotEqual(profile8.ProfileHash, profile11.ProfileHash);
        Assert.NotEqual(profile8.BucketId(challenge.Id), profile11.BucketId(challenge.Id));
        var result = new ScoreEngine(profile11.Key, profile11.ProfileHash)
            .EvaluatePreview(challenge, new LandingSnapshot());
        Assert.Equal(profile11.Key.Id, result.EvaluationKeyId);
        Assert.Equal(21, result.EvaluationKeyVersion);
        Assert.Equal(profile11.ProfileHash, result.ScoringProfileHash);
        Assert.Equal(profile11.BucketId(challenge.Id), result.RankedBucketId);

        var mappedChallenge = new ChallengeConfig
        {
            Id = challenge.Id,
            ContactMapping = new LandingContactMapping
            {
                LeftMainGearIndex = 3,
                RightMainGearIndex = 4,
                NoseGearIndex = 0
            }
        };
        var mapped = EffectiveEvaluationProfileBuilder.Build(v11, mappedChallenge);
        Assert.NotEqual(profile11.ProfileHash, mapped.ProfileHash);
        Assert.NotEqual(profile11.BucketId(challenge.Id), mapped.BucketId(challenge.Id));
    }

    [Theory]
    [InlineData(100, 100, 100)]
    [InlineData(80, 80, 80)]
    [InlineData(100, 0, 22.5403)]
    [InlineData(0, 100, 36.7544)]
    public void WeightedPenaltyRms_MatchesReferenceValues(double vs, double g, double expected)
    {
        var actual = CompositeScoringMath.CombineScoresByPenaltyRms((vs, 0.4), (g, 0.6));
        Assert.Equal(expected, actual, 3);
    }

    [Fact]
    public void WeightedPenaltyRms_ClampsScoresNormalizesWeightsAndRejectsBadWeights()
    {
        Assert.Equal(100, CompositeScoringMath.CombineScoresByPenaltyRms((120, 4), (110, 6)), 6);
        Assert.Equal(0, CompositeScoringMath.CombineScoresByPenaltyRms((-20, 4), (-1, 6)), 6);
        Assert.Equal(80, CompositeScoringMath.CombineScoresByPenaltyRms((80, 4), (80, 6)), 6);
        Assert.Throws<ArgumentException>(() => CompositeScoringMath.CombineScoresByPenaltyRms((50, 0), (50, 0)));
        Assert.Throws<ArgumentException>(() => CompositeScoringMath.CombineScoresByPenaltyRms((50, -1), (50, 2)));
        Assert.Throws<ArgumentException>(() => CompositeScoringMath.CombineScoresByPenaltyRms((50, double.NaN)));
    }

    [Fact]
    public void ImpactFilter_HandlesSustainedPulseSpikeIrregularTimingAndNyquistCap()
    {
        var (key, _) = Load();
        var settings = key.ToSessionSettings();
        var sustained = Enumerable.Range(0, 22)
            .Select(i => Sample(i * 0.05, g: i < 5 ? 1 : 1.3))
            .ToArray();
        var stable = TouchdownAnalysisCalculator.AnalyzeImpact(
            sustained, 0.25, -250, "official", settings);
        Assert.False(stable.TelemetryDegraded);
        Assert.InRange(stable.RobustPeakG, 1.25, 1.31);

        var isolated = sustained.ToArray();
        isolated[11] = Sample(isolated[11].TimeSeconds, g: 2.5);
        var spiky = TouchdownAnalysisCalculator.AnalyzeImpact(
            isolated, 0.25, -250, "official", settings);
        Assert.Equal(2.5, spiky.RawPeakG, 6);
        Assert.True(spiky.RobustPeakG < spiky.RawPeakG);

        var irregular = new[] { 0.0, 0.03, 0.09, 0.14, 0.22, 0.31, 0.37, 0.46, 0.55, 0.63, 0.72, 0.81 }
            .Select((t, i) => Sample(t, g: i < 3 ? 1 : 1.3))
            .ToArray();
        var irregularResult = TouchdownAnalysisCalculator.AnalyzeImpact(
            irregular, 0.14, -250, "official", settings with { MinImpactSamples = 8 });
        Assert.True(double.IsFinite(irregularResult.RobustPeakG));

        var values = Enumerable.Repeat(1.0, 20).ToArray();
        values[10] = 2.5;
        var times = Enumerable.Range(0, 20).Select(i => i * 0.05).ToArray();
        var capped = CompositeScoringMath.ZeroPhaseOnePoleLowPass(values, times, 10);
        var explicitSafeCutoff = CompositeScoringMath.ZeroPhaseOnePoleLowPass(values, times, 5);
        Assert.Equal(explicitSafeCutoff, capped);
    }

    [Fact]
    public void InitialImpact_ExcludesLaterBounceAndIgnoresNonFiniteSamples()
    {
        var (key, _) = Load();
        var settings = key.ToSessionSettings() with { MinImpactSamples = 5 };
        var samples = new List<LandingTelemetrySample>();
        for (var i = 0; i < 12; i++)
            samples.Add(Sample(1 + i * 0.05, g: i == 3 ? double.NaN : 1.3));
        samples.Add(Sample(1.60, g: double.PositiveInfinity));
        samples.Add(Sample(1.70, g: 2.8));

        var result = TouchdownAnalysisCalculator.AnalyzeImpact(
            samples, 1, -250, "official", settings, endAtSeconds: 1.45);
        Assert.False(result.TelemetryDegraded);
        Assert.InRange(result.RawPeakG, 1.29, 1.31);
        Assert.True(double.IsFinite(result.RobustPeakG));
    }

    [Fact]
    public void Impact_InsufficientSamplesUsesFiniteDegradedFallback()
    {
        var (key, _) = Load();
        var result = TouchdownAnalysisCalculator.AnalyzeImpact(
            new[] { Sample(1, g: 1.2), Sample(1.1, g: double.NaN) },
            1, -300, "fallback", key.ToSessionSettings());
        Assert.True(result.TelemetryDegraded);
        Assert.True(double.IsFinite(result.RawPeakG));
        Assert.True(double.IsFinite(result.RobustPeakG));
        Assert.Contains("requires", result.DegradedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ImpactCurve_SustainedOnePointSevenGScoringIsWorseThanOnePointThreeG()
    {
        var (key, _) = Load();
        var metric = key.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "touchdown_impact");
        double Score(double g) => CompositeMetricEvaluator.Evaluate(
            metric, key, new LandingSnapshot { InitialImpact = Impact(-250, g) },
            new LandingResultDiagnostics()).ScorePercent;
        Assert.True(Score(1.7) < Score(1.3));
    }

    [Fact]
    public void ChallengeProfiles_CanScoreIdenticalImpactDifferentlyAndDeterministically()
    {
        var (key, challenge) = Load();
        ChallengeConfig Profile(double score) => new()
        {
            Id = challenge.Id,
            ScoringOverrides = new ChallengeScoringOverrides
            {
                Metrics =
                {
                    new EvaluationMetricOverride
                    {
                        Id = "touchdown_impact",
                        Curves =
                        {
                            ["verticalSpeed"] = new()
                            {
                                new ScorePoint { V = -2000, S = 0 },
                                new ScorePoint { V = -250, S = score },
                                new ScorePoint { V = 0, S = 0 }
                            },
                            ["peakG"] = new()
                            {
                                new ScorePoint { V = 0.85, S = 0 },
                                new ScorePoint { V = 1.33, S = score },
                                new ScorePoint { V = 2.1, S = 0 }
                            }
                        }
                    }
                }
            }
        };

        var comfort = EffectiveEvaluationProfileBuilder.Build(key, Profile(65));
        var firm = EffectiveEvaluationProfileBuilder.Build(key, Profile(100));
        double Evaluate(EffectiveEvaluationProfile profile)
        {
            var metric = profile.Key.Phases.SelectMany(p => p.Metrics)
                .Single(m => m.Id == "touchdown_impact");
            var snapshot = new LandingSnapshot { InitialImpact = Impact(-250, 1.33) };
            return CompositeMetricEvaluator.Evaluate(
                metric, profile.Key, snapshot, new LandingResultDiagnostics()).ScorePercent;
        }

        Assert.True(Evaluate(firm) > Evaluate(comfort));
        Assert.Equal(Evaluate(firm), Evaluate(firm), 10);
        Assert.NotEqual(comfort.ProfileHash, firm.ProfileHash);
    }

    [Fact]
    public void FloatAnalysis_DetectsTimestampedDistanceAndBallooning()
    {
        var samples = new[]
        {
            Sample(0, agl: 60, verticalSpeed: -20), // excluded above flare height
            Sample(0.8, agl: 45, verticalSpeed: -200),
            Sample(1.0, agl: 35, verticalSpeed: -20),
            Sample(1.2, agl: 30, verticalSpeed: -20),
            Sample(2.0, agl: 15, verticalSpeed: 50),
            Sample(3.0, agl: 0, verticalSpeed: -250, left: true),
            Sample(4.0, agl: 0, verticalSpeed: 200, left: true) // after first contact
        };
        var result = TouchdownAnalysisCalculator.AnalyzeFloat(samples, 3, 50, -80, 0.15);
        Assert.True(result.CoverageSufficient);
        Assert.True(result.Detected);
        Assert.Equal(1, result.StartTimeSeconds, 6);
        Assert.Equal(2, result.DurationSeconds, 6);
        Assert.Equal(100 * 0.514444 * 2, result.DistanceMetres, 4);
        Assert.Equal(1.8, result.PositiveVerticalSpeedSeconds, 6);
        Assert.Equal(50, result.MaximumPositiveVerticalSpeedFpm);
    }

    [Fact]
    public void FloatAnalysis_IgnoresNormalFlareAndShortThresholdCrossing()
    {
        var normal = new[]
        {
            Sample(0, agl: 40, verticalSpeed: -250),
            Sample(0.1, agl: 20, verticalSpeed: -200),
            Sample(0.2, agl: 5, verticalSpeed: -20),
            Sample(0.3, agl: 0, verticalSpeed: -180, left: true)
        };
        var result = TouchdownAnalysisCalculator.AnalyzeFloat(normal, 0.3, 50, -80, 0.15);
        Assert.True(result.CoverageSufficient);
        Assert.False(result.Detected);

        var shortCoverage = TouchdownAnalysisCalculator.AnalyzeFloat(
            new[] { Sample(0.2, agl: 5), Sample(0.3, agl: 0, left: true) },
            0.3, 50, -80, 0.15);
        Assert.False(shortCoverage.CoverageSufficient);
        Assert.False(shortCoverage.Detected);
    }

    [Fact]
    public void FlareScore_IsIndependentOfExistingExcessSpeedMetric()
    {
        var (key, _) = Load();
        var metric = key.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "flare_efficiency");
        var analysis = new FloatAnalysis(true, true, 1, 3, 200, 0.4, 50, null);
        double Score(double excessSpeed)
        {
            var snapshot = new LandingSnapshot { FloatAnalysis = analysis, ExcessSpeedOverVappKts = excessSpeed };
            return CompositeMetricEvaluator.Evaluate(metric, key, snapshot, new LandingResultDiagnostics()).ScorePercent;
        }
        Assert.Equal(Score(0), Score(50), 10);

        double AnalysisScore(FloatAnalysis value) => CompositeMetricEvaluator.Evaluate(
            metric, key, new LandingSnapshot { FloatAnalysis = value },
            new LandingResultDiagnostics()).ScorePercent;
        var shortFloat = new FloatAnalysis(true, true, 1, 1, 60, 0, 0, null);
        var longFloat = new FloatAnalysis(true, true, 1, 4, 350, 0, 0, null);
        var balloon = new FloatAnalysis(true, true, 1, 1, 60, .8, 80, null);
        Assert.True(AnalysisScore(longFloat) < AnalysisScore(shortFloat));
        Assert.True(AnalysisScore(balloon) < AnalysisScore(shortFloat));
    }

    [Fact]
    public void BounceDetector_IgnoresAsymmetricContactAndChatter()
    {
        var samples = new[]
        {
            Sample(0, left: true),
            Sample(0.05, left: true, right: true),
            Sample(0.20),
            Sample(0.25, right: true), // 0.05 s chatter
            Sample(0.30, left: true, right: true)
        };
        var intervals = TouchdownAnalysisCalculator.DetectBounceIntervals(samples, 0, 3, 0.08);
        Assert.Empty(intervals);
    }

    [Fact]
    public void BounceDetector_RecognizesOneAndTwoCyclesAndHonorsWindow()
    {
        var samples = new[]
        {
            Sample(0, left: true, right: true),
            Sample(0.2),
            Sample(0.4, left: true),
            Sample(0.6),
            Sample(0.9, right: true),
            Sample(3.2),
            Sample(3.5, left: true)
        };
        var intervals = TouchdownAnalysisCalculator.DetectBounceIntervals(samples, 0, 3, 0.08);
        Assert.Equal(2, intervals.Count);
        Assert.Equal(0.2, intervals[0].Start, 6);
        Assert.Equal(0.9, intervals[1].Recontact, 6);
    }

    [Fact]
    public void ContactStability_TwoOrHarderBouncesScoreWorseAndDoNotMutateInitialImpact()
    {
        var root = FindRepositoryRoot();
        var loader = new ConfigLoader(Path.Combine(root, "config"));
        var catalog = loader.LoadCatalog();
        var loaded = loader.LoadEvaluationKey(catalog.FreeFlightEvaluationKey);
        Assert.True(loaded.IsValid, string.Join("; ", loaded.Errors));
        var key = loaded.Key!;
        var metric = key.Phases.SelectMany(p => p.Metrics).Single(m => m.Id == "contact_stability");
        var initial = Impact(-100, 1.2);
        var soft = new BounceEvent(1, 1.2, 0.2, -100, 1.2, 1.2, false);
        var hard = new BounceEvent(2, 2.3, 0.3, -800, 1.7, 1.7, false);
        double Score(params BounceEvent[] events)
        {
            var snapshot = new LandingSnapshot
            {
                InitialImpact = initial,
                ContactStability = new ContactStabilityAnalysis(
                    true, events, events.Length,
                    events.Length == 0 ? 0 : events.Max(x => x.AirborneDurationSeconds), null)
            };
            var score = CompositeMetricEvaluator.Evaluate(
                metric, key, snapshot, new LandingResultDiagnostics()).ScorePercent;
            Assert.Same(initial, snapshot.InitialImpact);
            Assert.Equal(-100, snapshot.InitialImpact.VerticalSpeedFpm);
            return score;
        }

        Assert.Equal(100, Score(), 6);
        Assert.True(Score(soft, hard) < Score(soft));
        Assert.True(Score(hard) < Score(soft));
    }

    [Fact]
    public void ContactAnalysis_MissingIndependentGearDataReturnsDegradedCoverage()
    {
        var (key, _) = Load();
        var result = TouchdownAnalysisCalculator.AnalyzeContactStability(
            new[] { Sample(0, left: true, contactsAvailable: false) },
            0, key.ToSessionSettings(), windowComplete: true);
        Assert.False(result.CoverageSufficient);
        Assert.Contains("unavailable", result.DegradedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LandingSession_UsesMainContactEdgeAndFreezesFirstOfficialTouchdownVelocity()
    {
        var (key, challenge) = Load();
        var settings = key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            MinAirborneSamples = 2,
            MinImpactSamples = 3
        };
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        TelemetrySample Telemetry(double time, bool left, bool right, double tdVelocity, double g = 1.2) => new()
        {
            Timestamp = t0.AddSeconds(time),
            SimulationTimeSeconds = time,
            SimOnGround = left || right,
            AglFeet = left || right ? 0 : 100,
            RadioHeightFeet = left || right ? 0 : 100,
            VerticalSpeedFpm = -999,
            TouchdownNormalVelocityFps = tdVelocity,
            GearOnGroundByIndex = new Dictionary<int, bool> { [0] = false, [1] = left, [2] = right },
            IsGearWheels = true,
            GForce = g,
            GroundSpeedKts = 100,
            AirspeedKts = 140,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            Latitude = challenge.Runway.ThresholdLatitude,
            Longitude = challenge.Runway.ThresholdLongitude,
            HeadingTrueDeg = challenge.Runway.HeadingTrueDeg,
            GroundTrackTrueDeg = challenge.Runway.HeadingTrueDeg
        };

        session.Arm();
        session.Ingest(Telemetry(0, false, false, 0));
        session.Ingest(Telemetry(0.1, false, false, 0));
        session.Ingest(Telemetry(0.2, true, false, 250.0 / 60.0));
        for (var i = 1; i <= 12; i++)
            session.Ingest(Telemetry(0.2 + i * 0.07, true, true, 10, 1.3));

        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.Equal(-250, session.Snapshot.VerticalSpeedAtTouchdownFpm, 5);
        Assert.NotNull(session.Snapshot.InitialImpact);
        Assert.False(session.Snapshot.InitialImpact!.TelemetryDegraded);
        Assert.Equal("PLANE TOUCHDOWN NORMAL VELOCITY", session.Snapshot.InitialImpact.VerticalSpeedSource);
    }

    [Fact]
    public void LandingSession_SettleWindowUsesSimulationTimeAndDoesNotAdvanceWhilePaused()
    {
        var (key, challenge) = Load();
        var settings = key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false,
            SettledHoldSeconds = 1,
            OperationalGates = new OperationalGateSessionSettings()
        };
        var session = new LandingSession(challenge, settings);
        var wall = DateTimeOffset.UtcNow;
        TelemetrySample At(double simTime, DateTimeOffset utc, bool contact, double gs) => new()
        {
            Timestamp = utc,
            SimulationTimeSeconds = simTime,
            SimOnGround = contact,
            GearOnGroundByIndex = new Dictionary<int, bool> { [0] = false, [1] = contact, [2] = contact },
            IsGearWheels = true,
            TouchdownNormalVelocityFps = contact ? 3 : 0,
            GForce = 1.2,
            GroundSpeedKts = gs,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3
        };
        session.Arm();
        session.Ingest(At(0, wall, false, 100));
        session.Ingest(At(.1, wall.AddSeconds(.1), true, 100));
        session.Ingest(At(1, wall.AddSeconds(1), true, 40));
        session.Ingest(At(1, wall.AddMinutes(5), true, 40)); // paused: UTC moves, sim clock does not
        Assert.False(session.IsComplete);
        session.Ingest(At(2.1, wall.AddMinutes(5).AddSeconds(1.1), true, 40));
        Assert.True(session.IsComplete);
    }

    [Fact]
    public void TaildraggerRequiresAircraftSpecificContactMapping()
    {
        var (key, challenge) = Load();
        var fallbackSession = new LandingSession(challenge, key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false
        });
        var now = DateTimeOffset.UtcNow;
        TelemetrySample TailSample(double time, bool contact) => new()
        {
            Timestamp = now.AddSeconds(time),
            SimulationTimeSeconds = time,
            SimOnGround = contact,
            IsGearWheels = true,
            IsTailDragger = true,
            GearOnGroundByIndex = new Dictionary<int, bool> { [0] = false, [1] = contact, [2] = contact },
            GForce = 1,
            GroundSpeedKts = 100
        };
        fallbackSession.Arm();
        fallbackSession.Ingest(TailSample(0, false));
        fallbackSession.Ingest(TailSample(0.1, true));
        Assert.True(fallbackSession.Snapshot.ContactMappingDegraded);

        challenge.ContactMapping = new LandingContactMapping
        {
            LeftMainGearIndex = 1,
            RightMainGearIndex = 2,
            NoseGearIndex = 0
        };
        var explicitProfile = EffectiveEvaluationProfileBuilder.Build(key, challenge);
        Assert.True(explicitProfile.Key.ContactMapping.IsAircraftSpecific);
        var explicitSession = new LandingSession(challenge, explicitProfile.Key.ToSessionSettings() with
        {
            PostArmIgnoreSeconds = 0,
            RequireAirborneBeforeTouchdown = false
        });
        explicitSession.Arm();
        explicitSession.Ingest(TailSample(0, false));
        explicitSession.Ingest(TailSample(0.1, true));
        Assert.False(explicitSession.Snapshot.ContactMappingDegraded);
    }

    [Fact]
    public void HighscoreStore_PreservesV9IdentityAndReadsLegacyJson()
    {
        var path = Path.Combine(Path.GetTempPath(), $"challenge-lab-v9-{Guid.NewGuid():N}.json");
        try
        {
            var result = new ScoreResult
            {
                ChallengeId = "test",
                ChallengeTitle = "Test",
                ScorePercent = 91,
                Grade = "A",
                IsRanked = true,
                EvaluationKeyId = "landing-evaluation-key",
                EvaluationKeyVersion = 9,
                ScoringProfileHash = "abc123",
                RankedBucketId = "test|landing-evaluation-key|v9|abc123",
                Diagnostics = new LandingResultDiagnostics
                {
                    TouchdownVerticalSpeedFpm = -250,
                    TouchdownImpactScore = 92,
                    TouchdownRobustPeakG = 1.33
                },
                PhaseScores = new[]
                {
                    new PhaseScore
                    {
                        PhaseId = "touchdown", DisplayName = "Touchdown", WeightPercent = 70,
                        ScorePercent = 91, IsComplete = true
                    }
                },
                Criteria = new[]
                {
                    new CriterionScore
                    {
                        Id = "touchdown_impact", DisplayName = "Touchdown impact",
                        Status = MetricStatus.Scored, Score01 = .92, RawValue = 92, Unit = "%",
                        PhaseId = "touchdown", PhaseDisplayName = "Touchdown"
                    }
                }
            };
            var store = new HighscoreStore(path);
            var projectedHistory = Enumerable.Range(0, 2401)
                .Select(index => new ScoreHistoryPoint(index * .25, 100 - index % 30))
                .ToList();
            store.Add(result, projectedScoreHistory: projectedHistory);
            var reloaded = new HighscoreStore(path).Entries.Single();
            Assert.False(reloaded.IsLegacy);
            Assert.Equal(result.RankedBucketId, reloaded.EffectiveRankedBucketId);
            Assert.Equal(-250, reloaded.ResolveVerticalSpeedFpm());
            Assert.Equal("-250", reloaded.VerticalSpeedDisplay);
            Assert.NotNull(reloaded.Diagnostics);
            Assert.True(reloaded.HasProjectedScoreHistory);
            Assert.NotNull(reloaded.ProjectedScoreHistory);
            Assert.InRange(reloaded.ProjectedScoreHistory!.Count, 2, 600);
            Assert.Equal(0, reloaded.ProjectedScoreHistory[0].ElapsedSeconds);
            Assert.Equal(600, reloaded.ProjectedScoreHistory[^1].ElapsedSeconds);
            Assert.Equal(projectedHistory[^1].ScorePercent, reloaded.ProjectedScoreHistory[^1].ScorePercent);

            File.WriteAllText(path,
                "[{\"Utc\":\"2025-01-01T00:00:00Z\",\"ChallengeId\":\"old\",\"ChallengeTitle\":\"Old\",\"ScorePercent\":80,\"Grade\":\"B\",\"VerticalSpeedFpm\":-300}]");
            var legacyStore = new HighscoreStore(path);
            var legacy = legacyStore.Entries.Single();
            Assert.True(legacy.IsLegacy);
            Assert.Equal("legacy|unknown-scoring-profile", legacy.EffectiveRankedBucketId);
            Assert.Equal(-300, legacy.ResolveVerticalSpeedFpm());
            Assert.False(legacy.HasProjectedScoreHistory);
            legacyStore.RewriteClean();
            var rewritten = File.ReadAllText(path);
            Assert.DoesNotContain("HasDetail", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("CriteriaForReport", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("VerticalSpeedDisplay", rewritten, StringComparison.Ordinal);
            Assert.DoesNotContain("HasProjectedScoreHistory", rewritten, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LandingTrace_PersistsIdentityDiagnosticsGAndIndexedContacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"challenge-trace-v9-{Guid.NewGuid():N}");
        try
        {
            var (key, challenge) = Load();
            var profile = EffectiveEvaluationProfileBuilder.Build(key, challenge);
            var result = new ScoreEngine(profile.Key, profile.ProfileHash)
                .EvaluatePreview(challenge, new LandingSnapshot());
            var snapshot = new LandingSnapshot();
            snapshot.ApproachSamples.Add(new TelemetrySample
            {
                Timestamp = DateTimeOffset.UtcNow,
                SimulationTimeSeconds = double.NaN,
                GForce = 1.3,
                GearOnGroundByIndex = new Dictionary<int, bool> { [0] = false, [1] = true, [2] = false }
            });

            var path = new LandingTraceStore(directory).Save(result, snapshot);
            var json = File.ReadAllText(path);
            Assert.Contains(result.RankedBucketId, json, StringComparison.Ordinal);
            Assert.Contains("\"Diagnostics\"", json, StringComparison.Ordinal);
            Assert.Contains("\"G\": 1.3", json, StringComparison.Ordinal);
            Assert.Contains("\"GearContacts\"", json, StringComparison.Ordinal);
            Assert.DoesNotContain("NaN", json, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SimConnectSource_UsesCurrentGAndNeverLifetimeMaximumG()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "src", "ChallengeLab.SimConnect", "SimConnectClient.cs"));
        Assert.Contains("\"G FORCE\"", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MAX G FORCE", source, StringComparison.Ordinal);
        Assert.Contains("PLANE TOUCHDOWN NORMAL VELOCITY", source, StringComparison.Ordinal);
        Assert.Contains("GEAR IS ON GROUND:", source, StringComparison.Ordinal);
        Assert.Contains("SIMULATION TIME", source, StringComparison.Ordinal);
    }
}
