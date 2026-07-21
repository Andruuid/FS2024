using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring.Evaluators;

namespace ChallengeLab.Core.Scoring;

public sealed class ScoreEngine
{
    private readonly LandingEvaluationKey _key;
    private readonly string _profileHash;

    public ScoreEngine(LandingEvaluationKey evaluationKey, string? profileHash = null)
    {
        var errors = EvaluationKeyValidator.Validate(evaluationKey);
        if (errors.Count > 0)
            throw new ArgumentException(
                "Evaluation key is invalid: " + string.Join(" | ", errors),
                nameof(evaluationKey));
        _key = evaluationKey;
        _profileHash = profileHash ?? EffectiveEvaluationProfileBuilder.Build(evaluationKey).ProfileHash;
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
        var phaseScores01 = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        var incompleteReasons = new List<string>();
        var diagnostics = new LandingResultDiagnostics();
        diagnostics.FreeFlightCapabilities = challenge.FreeFlightCapabilities;
        var freeGateContext = new FreeGateEvaluationContext(
            _key.FreeMode, challenge.FreeFlightCapabilities);
        double totalBeforeGate = 0;

        foreach (var phase in _key.Phases)
        {
            var phaseComplete = true;
            double phaseScore01 = 0;

            foreach (var metric in phase.Metrics)
            {
                if (EvaluatorFactory.IsComposite(metric.Evaluator))
                {
                    var composite = CompositeMetricEvaluator.Evaluate(metric, _key, snapshot, diagnostics);
                    var compositeMaxOverallPoints = metric.ImportancePercent * phase.WeightPercent / 100.0;
                    if (!composite.IsAvailable)
                    {
                        if (preview)
                        {
                            phaseScore01 += metric.ImportancePercent / 100.0;
                            criteria.Add(new CriterionScore
                            {
                                Id = metric.Id, DisplayName = metric.DisplayName, Score01 = 1,
                                Unit = metric.Unit, Status = MetricStatus.Informational,
                                UnavailableReason = composite.DegradedReason,
                                Note = $"[PREVIEW · assumed 100%] Not measured yet: {composite.DegradedReason}",
                                PhaseId = phase.Id, PhaseDisplayName = phase.DisplayName,
                                PhaseImportancePercent = metric.ImportancePercent,
                                PhaseWeightPercent = phase.WeightPercent, MaxOverallPoints = compositeMaxOverallPoints
                            });
                            continue;
                        }

                        if (_key.FreeMode is { } freeMode)
                        {
                            var assumedScore01 = freeMode.UnavailableMetricScorePercent / 100.0;
                            phaseScore01 += assumedScore01 * metric.ImportancePercent / 100.0;
                            var assumedReason = composite.DegradedReason
                                                ?? "Required touchdown analysis is unavailable.";
                            criteria.Add(new CriterionScore
                            {
                                Id = metric.Id, DisplayName = metric.DisplayName,
                                Score01 = assumedScore01, Unit = metric.Unit,
                                Status = MetricStatus.Assumed, UnavailableReason = assumedReason,
                                Note = $"[FREE · assumed {freeMode.UnavailableMetricScorePercent:0.#}%] " +
                                       $"Telemetry unavailable: {assumedReason}",
                                PhaseId = phase.Id, PhaseDisplayName = phase.DisplayName,
                                PhaseImportancePercent = metric.ImportancePercent,
                                PhaseWeightPercent = phase.WeightPercent,
                                MaxOverallPoints = compositeMaxOverallPoints
                            });
                            continue;
                        }

                        phaseComplete = false;
                        var reason = composite.DegradedReason ?? "Required touchdown analysis is unavailable.";
                        incompleteReasons.Add($"{phase.DisplayName} / {metric.DisplayName}: {reason}");
                        criteria.Add(new CriterionScore
                        {
                            Id = metric.Id, DisplayName = metric.DisplayName, Status = MetricStatus.Unavailable,
                            UnavailableReason = reason, Note = reason, Unit = metric.Unit,
                            PhaseId = phase.Id, PhaseDisplayName = phase.DisplayName,
                            PhaseImportancePercent = metric.ImportancePercent,
                            PhaseWeightPercent = phase.WeightPercent, MaxOverallPoints = compositeMaxOverallPoints
                        });
                        continue;
                    }

                    var compositeScore01 = Math.Clamp(composite.ScorePercent / 100.0, 0, 1);
                    phaseScore01 += compositeScore01 * metric.ImportancePercent / 100.0;
                    if (composite.IsDegraded && _key.FreeMode is null)
                    {
                        phaseComplete = false;
                        incompleteReasons.Add(
                            $"{phase.DisplayName} / {metric.DisplayName}: {composite.DegradedReason ?? "Telemetry degraded."}");
                    }
                    var compositeExplanation = preview ? "[PREVIEW · measured] " + composite.Explanation : composite.Explanation;
                    compositeExplanation =
                        $"[{phase.DisplayName} · {metric.ImportancePercent:0.#}% of phase · {compositeMaxOverallPoints:0.##} max overall points] " +
                        compositeExplanation;
                    criteria.Add(new CriterionScore
                    {
                        Id = metric.Id, DisplayName = metric.DisplayName, Score01 = compositeScore01,
                        RawValue = composite.RawValue, Unit = composite.Unit,
                        Status = composite.IsDegraded ? MetricStatus.Degraded : MetricStatus.Scored,
                        UnavailableReason = composite.IsDegraded ? composite.DegradedReason : null,
                        Note = compositeExplanation, PhaseId = phase.Id, PhaseDisplayName = phase.DisplayName,
                        PhaseImportancePercent = metric.ImportancePercent,
                        PhaseWeightPercent = phase.WeightPercent, MaxOverallPoints = compositeMaxOverallPoints
                    });
                    continue;
                }

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


                    if (_key.FreeMode is { } freeMode)
                    {
                        var assumedScore01 = freeMode.UnavailableMetricScorePercent / 100.0;
                        phaseScore01 += assumedScore01 * metric.ImportancePercent / 100.0;
                        var assumedReason = observation.UnavailableReason ?? "Required telemetry is unavailable.";
                        criteria.Add(new CriterionScore
                        {
                            Id = metric.Id,
                            DisplayName = metric.DisplayName,
                            Score01 = assumedScore01,
                            RawValue = null,
                            Unit = metric.Unit,
                            Status = MetricStatus.Assumed,
                            UnavailableReason = assumedReason,
                            Note = $"[FREE · assumed {freeMode.UnavailableMetricScorePercent:0.#}%] " +
                                   $"{MetricExplanations.DefaultCatalog(metric.Id, metric.DisplayName)} " +
                                   $"Telemetry unavailable: {assumedReason}",
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
                else if ((metric.Metric.Equals("touchdownPointErrorFt", StringComparison.OrdinalIgnoreCase)
                          || metric.Metric.Equals("touchdownOffsetFromAimingMarkerFt", StringComparison.OrdinalIgnoreCase))
                         && snapshot.Touchdown is not null
                         && TouchdownPointCalculator.TryCalculate(
                             challenge.Runway,
                             snapshot.Touchdown,
                             out var touchdownPoint,
                             out _))
                    displayRaw = touchdownPoint.ActualDistanceFeet;

                var explanation = MetricExplanations.For(metric, snapshot, challenge, score01, raw);
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
            phaseScores01[phase.Id] = phaseComplete || preview ? phaseScore01 : null;

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

        var generalPenaltyMultiplier = ApplyGeneralPenaltyChain(
            challenge, snapshot, criteria, incompleteReasons, diagnostics, preview, freeGateContext);
        var gearPenaltyApplied = criteria.Any(c =>
            c.Id == FreeFlightGateIds.Gear && c.Status == MetricStatus.GateFailed && c.AppliedMultiplier is < 1);
        var flapsPenaltyApplied = criteria.Any(c =>
            c.Id == FreeFlightGateIds.Flaps && c.Status == MetricStatus.GateFailed && c.AppliedMultiplier is < 1);
        diagnostics.OperationalGates = snapshot.GateObservations;
        // Free-flight lock (and catalog challenges) already know LengthM from facilities/config.
        // Always latch it on the result so highscores do not depend on rollout-gate evaluation.
        EnsureRunwayLengthLatched(diagnostics, challenge);
        var ranked = preview || _key.FreeMode is not null || incompleteReasons.Count == 0;
        double? scoreBeforeGates = null;
        double? scorePercent = null;
        var grade = "UNRANKED";

        if (ranked)
        {
            var rawScoreBeforeGates = Math.Clamp(totalBeforeGate, 0, 100);
            scoreBeforeGates = Math.Round(rawScoreBeforeGates, 1);
            var final = rawScoreBeforeGates * generalPenaltyMultiplier;

            scorePercent = Math.Round(Math.Clamp(final, 0, 100), 1);
            grade = ScoreResult.GradeFromPercent(scorePercent.Value);
        }

        // Phase scores stay pure metric quality; all gates multiply the combined score only.
        phases = phases.Select(phase =>
        {
            var rawScore01 = phaseScores01[phase.PhaseId];
            return new PhaseScore
            {
                PhaseId = phase.PhaseId,
                DisplayName = phase.DisplayName,
                WeightPercent = phase.WeightPercent,
                ScorePercent = rawScore01 is null
                    ? null
                    : Math.Round(Math.Clamp(rawScore01.Value * 100, 0, 100), 1),
                IsComplete = phase.IsComplete,
                Note = phase.Note
            };
        }).ToList();

        var summary = $"Scoring profile: {_key.Id} v{_key.Version} · {_profileHash}{Environment.NewLine}" +
            ScoreBreakdownFormatter.Format(
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
            PhaseScores = phases,
            EvaluationKeyId = _key.Id,
            EvaluationKeyVersion = _key.Version,
            ScoringProfileHash = _profileHash,
            RankedBucketId = $"{challenge.Id}|{_key.Id}|v{_key.Version}|{_profileHash}",
            Diagnostics = diagnostics,
            LandingVisualization = BuildLandingVisualization(challenge, snapshot, diagnostics)
        };
    }

    private LandingVisualizationData? BuildLandingVisualization(
        ChallengeConfig challenge,
        LandingSnapshot snapshot,
        LandingResultDiagnostics diagnostics)
    {
        var touchdownMetric = _key.Phases
            .SelectMany(phase => phase.Metrics)
            .FirstOrDefault(metric => metric.Id.Equals("touchdown_point", StringComparison.OrdinalIgnoreCase));
        var nearOffset = Parameter(
            touchdownMetric,
            "idealNearOffsetFt",
            TouchdownPointCalculator.DefaultIdealNearOffsetFeet);
        var farOffset = Parameter(
            touchdownMetric,
            "idealFarOffsetFt",
            TouchdownPointCalculator.DefaultIdealFarOffsetFeet);
        if (snapshot.Touchdown is null
            || !TouchdownPointCalculator.TryCalculate(
                challenge.Runway,
                snapshot.Touchdown,
                out var point,
                out _,
                nearOffset,
                farOffset)
            || !double.IsFinite(challenge.Runway.WidthM)
            || challenge.Runway.WidthM <= 0
            || !double.IsFinite(snapshot.TouchdownLateralOffsetM))
        {
            return null;
        }

        var impact = snapshot.InitialImpact;
        var rawPeakG = VisualizationFiniteFallback(
            diagnostics.TouchdownRawPeakG,
            impact?.RawPeakG ?? snapshot.PeakGForce);
        var robustPeakG = VisualizationFiniteFallback(
            diagnostics.TouchdownRobustPeakG,
            impact?.RobustPeakG ?? snapshot.PeakGForce);
        var sinkRate = VisualizationFiniteFallback(
            diagnostics.TouchdownSinkRateFpm,
            snapshot.TouchdownSinkRateFpm);

        return new LandingVisualizationData
        {
            AirportIcao = challenge.Runway.AirportIcao,
            RunwayId = challenge.Runway.RunwayId,
            RunwayHeadingTrueDeg = VisualizationFiniteOrZero(challenge.Runway.HeadingTrueDeg),
            RunwayLengthM = challenge.Runway.LengthM,
            RunwayWidthM = challenge.Runway.WidthM,
            TouchdownDistanceFromThresholdM = point.ActualDistanceFeet * RunwayPathGeometry.MetersPerFoot,
            IdealTouchdownDistanceFromThresholdM = point.PerfectDistanceFeet * RunwayPathGeometry.MetersPerFoot,
            IdealTouchdownNearDistanceFromThresholdM = point.IdealNearDistanceFeet * RunwayPathGeometry.MetersPerFoot,
            IdealTouchdownFarDistanceFromThresholdM = point.IdealFarDistanceFeet * RunwayPathGeometry.MetersPerFoot,
            AimingMarkerStartDistanceFromThresholdM = challenge.Runway.AimingMarkerStartM,
            AimingMarkerNominalLengthM = challenge.Runway.AimingMarkerLengthM,
            AimingMarkerCenterDistanceFromThresholdM = challenge.Runway.AimingMarkerCenterM,
            AimingMarkerSource = challenge.Runway.AimingMarkerSource,
            AimingMarkerConfidence = challenge.Runway.AimingMarkerConfidence,
            TouchdownLateralOffsetM = snapshot.TouchdownLateralOffsetM,
            TouchdownHeadingErrorDeg = VisualizationFiniteOrZero(snapshot.TouchdownHeadingErrorDeg),
            TouchdownGroundTrackTrueDeg = snapshot.TouchdownGroundTrackTrueDeg,
            TouchdownGroundTrackSource = snapshot.TouchdownGroundTrackSource,
            TouchdownTrackErrorDeg = VisualizationFiniteOrZero(snapshot.TouchdownTrackErrorDeg),
            TouchdownTrueCrabAngleDeg = VisualizationFiniteOrZero(snapshot.TouchdownTrueCrabAngleDeg),
            TouchdownBankDeg = VisualizationFiniteOrZero(snapshot.BankAtTouchdownDeg),
            TouchdownPitchDeg = VisualizationFiniteOrZero(snapshot.PitchAtTouchdownDeg),
            TouchdownSinkRateFpm = sinkRate,
            TouchdownNormalVelocityFpm = snapshot.TouchdownNormalVelocityFpm,
            TouchdownVerticalSpeedFpm = sinkRate,
            TouchdownRawPeakG = rawPeakG,
            TouchdownRobustPeakG = robustPeakG,
            TouchdownAirspeedKts = VisualizationFiniteOrZero(snapshot.AirspeedAtTouchdownKts),
            TargetTouchdownAirspeedKts = VisualizationFiniteOrZero(snapshot.TargetTouchdownIasKts)
        };
    }

    private static double Parameter(EvaluationMetric? metric, string name, double fallback) =>
        metric?.Params.TryGetValue(name, out var value) == true && double.IsFinite(value)
            ? value
            : fallback;

    private static double VisualizationFiniteFallback(double preferred, double fallback) =>
        double.IsFinite(preferred) && Math.Abs(preferred) > .000_001
            ? preferred
            : VisualizationFiniteOrZero(fallback);

    private static double VisualizationFiniteOrZero(double value) => double.IsFinite(value) ? value : 0;

    /// <summary>
    /// The single ordered chain of every general penalty gate: contact stability,
    /// stall warning, overspeed warning, gear, flaps, then the operational gates
    /// (spoilers, manual braking, automation, nose-gear impact, rollout distance,
    /// reverse thrust, pause usage, simulation rate, cockpit view). All gates
    /// multiply the combined ranked score; phase scores stay pure metric quality.
    /// </summary>
    private double ApplyGeneralPenaltyChain(
        ChallengeConfig challenge,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        LandingResultDiagnostics diagnostics,
        bool preview,
        FreeGateEvaluationContext freeGateContext)
    {
        var penalties = _key.GeneralPenalties;
        var multiplier = 1.0;

        if (penalties?.ContactStability is { } contactStability)
        {
            multiplier *= preview
                ? AppendContactStabilityGatePreview(contactStability, snapshot, criteria, diagnostics, freeGateContext)
                : AppendContactStabilityGate(
                    contactStability, snapshot, criteria, incompleteReasons, diagnostics, freeGateContext);
        }

        if (penalties?.StallWarning is { } stallWarning)
            multiplier *= AppendAircraftWarningGate(
                FreeFlightGateIds.StallWarning,
                "Aircraft stall warning",
                snapshot.StallWarningCoverageAvailable,
                snapshot.StallWarningOccurred,
                stallWarning.MultiplierOnWarning,
                stallWarning.PenaltyDescription,
                criteria, incompleteReasons, preview, freeGateContext);

        if (penalties?.OverspeedWarning is { } overspeedWarning)
            multiplier *= AppendAircraftWarningGate(
                FreeFlightGateIds.OverspeedWarning,
                "Aircraft overspeed warning",
                snapshot.OverspeedWarningCoverageAvailable,
                snapshot.OverspeedWarningOccurred,
                overspeedWarning.MultiplierOnWarning,
                overspeedWarning.PenaltyDescription,
                criteria, incompleteReasons, preview, freeGateContext);

        if (penalties?.Gear is { } gear)
            multiplier *= preview
                ? AppendGearGatePreview(gear, challenge, snapshot, criteria, freeGateContext)
                : AppendGearGate(gear, challenge, snapshot, criteria, incompleteReasons, freeGateContext);

        if (penalties?.Flaps is { } flaps)
            multiplier *= preview
                ? AppendFlapsGatePreview(flaps, snapshot, criteria, freeGateContext)
                : AppendFlapsGate(flaps, snapshot, criteria, incompleteReasons, freeGateContext);

        multiplier *= OperationalGateEvaluator.AppendGeneral(
            penalties, snapshot, criteria, incompleteReasons, preview, freeGateContext);
        return multiplier;
    }

    /// <summary>Before bounce analysis completes, preview assumes no bounce penalty.</summary>
    private double AppendContactStabilityGatePreview(
        ContactStabilityGateConfig cfg,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        LandingResultDiagnostics diagnostics,
        FreeGateEvaluationContext context)
    {
        if (AppendNotApplicable(context, FreeFlightGateIds.ContactStability,
                "Contact stability (bounce gate)", criteria))
            return 1;
        if (snapshot.ContactStability is null)
        {
            criteria.Add(new CriterionScore
            {
                Id = "contact_stability",
                DisplayName = "Contact stability (bounce gate)",
                Status = MetricStatus.Informational,
                Note = "[PREVIEW · assumed OK] Bounce analysis is not complete yet."
            });
            return 1;
        }

        var incomplete = new List<string>();
        return AppendContactStabilityGate(cfg, snapshot, criteria, incomplete, diagnostics, context);
    }

    private double AppendContactStabilityGate(
        ContactStabilityGateConfig cfg,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        LandingResultDiagnostics diagnostics,
        FreeGateEvaluationContext context)
    {
        const string gateId = FreeFlightGateIds.ContactStability;
        const string gateName = "Contact stability (bounce gate)";
        if (AppendNotApplicable(context, gateId, gateName, criteria))
            return 1;
        if (snapshot.ContactStability is not { } analysis)
        {
            const string reason = "Contact-stability analysis is not complete.";
            if (context.IsFree)
                return AppendUnavailableGate(gateId, gateName, reason, cfg.OneBounceMultiplier,
                    criteria, incompleteReasons, context);
            incompleteReasons.Add("Contact-stability gate: " + reason);
            criteria.Add(new CriterionScore
            {
                Id = "contact_stability",
                DisplayName = "Contact stability (bounce gate)",
                Status = MetricStatus.Unavailable,
                UnavailableReason = reason,
                Note = reason
            });
            return 1;
        }

        if (!analysis.CoverageSufficient)
        {
            var reason = analysis.DegradedReason
                         ?? "Independent main-gear coverage was insufficient.";
            if (context.IsFree)
            {
                diagnostics.ContactTelemetryDegraded = true;
                return AppendUnavailableGate(gateId, gateName, reason, cfg.OneBounceMultiplier,
                    criteria, incompleteReasons, context, analysis.BounceCount, "bounces");
            }
            incompleteReasons.Add("Contact-stability gate: " + reason);
            diagnostics.ContactTelemetryDegraded = true;
            criteria.Add(new CriterionScore
            {
                Id = "contact_stability",
                DisplayName = "Contact stability (bounce gate)",
                RawValue = analysis.BounceCount,
                Unit = "bounces",
                Status = MetricStatus.Degraded,
                UnavailableReason = reason,
                Note = $"Bounce gate not applied because telemetry was degraded. {reason}"
            });
            return 1;
        }

        diagnostics.BounceCount = analysis.BounceCount;
        diagnostics.MaximumBounceAirborneSeconds = analysis.MaximumAirborneDurationSeconds;
        if (analysis.BounceCount == 0)
        {
            diagnostics.ContactStabilityScore = 100;
            criteria.Add(new CriterionScore
            {
                Id = "contact_stability",
                DisplayName = "Contact stability (bounce gate)",
                RawValue = 0,
                Unit = "bounces",
                Status = MetricStatus.Informational,
                AppliedMultiplier = 1,
                Note = "No bounce detected. The initial landing is the expected baseline and awards no points."
            });
            return 1;
        }

        var multiplier = analysis.BounceCount == 1
            ? cfg.OneBounceMultiplier
            : cfg.TwoOrMoreBouncesMultiplier;
        diagnostics.ContactStabilityScore = multiplier * 100;
        var touchdownLabel = analysis.BounceCount == 1
            ? "second touchdown"
            : analysis.BounceCount == 2
                ? "third touchdown"
                : $"touchdown {analysis.BounceCount + 1}";
        criteria.Add(new CriterionScore
        {
            Id = "contact_stability",
            DisplayName = "Bounce — penalty gate",
            RawValue = analysis.BounceCount,
            Unit = "bounces",
            Status = MetricStatus.GateFailed,
            AppliedMultiplier = multiplier,
            Note =
                $"{analysis.BounceCount} valid bounce{(analysis.BounceCount == 1 ? "" : "s")} " +
                $"({touchdownLabel}). Combined ranked score × {multiplier:0.##}. " +
                cfg.PenaltyDescription
        });
        return multiplier;
    }

    private double AppendAircraftWarningGate(
        string gateId,
        string displayName,
        bool coverageAvailable,
        bool warningOccurred,
        double multiplierOnWarning,
        string? penaltyDescription,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        bool preview,
        FreeGateEvaluationContext context)
    {
        if (!coverageAvailable)
        {
            if (preview)
            {
                criteria.Add(new CriterionScore
                {
                    Id = gateId,
                    DisplayName = displayName + " (safety gate)",
                    Status = MetricStatus.Informational,
                    Note = $"[PREVIEW - pending] {displayName} telemetry is not covered yet."
                });
                return 1;
            }
            return AppendUnavailableGate(
                gateId, displayName + " (safety gate)",
                $"{displayName} telemetry is unavailable.", multiplierOnWarning,
                criteria, incompleteReasons, context);
        }

        if (!warningOccurred)
        {
            criteria.Add(new CriterionScore
            {
                Id = gateId,
                DisplayName = displayName + " (safety gate)",
                RawValue = 0,
                Status = MetricStatus.Informational,
                AppliedMultiplier = 1,
                Note = $"No {displayName.ToLowerInvariant()} occurred. This is the required baseline and awards no points."
            });
            return 1;
        }

        criteria.Add(new CriterionScore
        {
            Id = gateId,
            DisplayName = displayName.ToUpperInvariant() + " — penalty",
            RawValue = 1,
            Status = MetricStatus.GateFailed,
            AppliedMultiplier = multiplierOnWarning,
            Note =
                $"The MSFS {displayName.ToUpperInvariant()} state activated during the armed attempt. " +
                $"Combined ranked score × {multiplierOnWarning:0.##}. {penaltyDescription}"
        });
        return multiplierOnWarning;
    }

    /// <summary>Before touchdown, preview assumes the required gear state.</summary>
    private double AppendGearGatePreview(
        GearGateConfig cfg,
        ChallengeConfig challenge,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        FreeGateEvaluationContext context)
    {
        if (AppendNotApplicable(context, FreeFlightGateIds.Gear, "Gear (safety gate)", criteria))
            return 1;
        if (snapshot.Touchdown is null)
        {
            criteria.Add(new CriterionScore
            {
                Id = "gear",
                DisplayName = "Gear (safety gate)",
                Status = MetricStatus.Informational,
                Note = "[PREVIEW · assumed OK] Gear not yet assessed (no touchdown)."
            });
            return 1;
        }

        // After TD, same gate semantics as final (informational / fail).
        var incomplete = new List<string>();
        return AppendGearGate(cfg, challenge, snapshot, criteria, incomplete, context);
    }

    private double AppendGearGate(
        GearGateConfig cfg,
        ChallengeConfig challenge,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        FreeGateEvaluationContext context)
    {
        if (AppendNotApplicable(context, FreeFlightGateIds.Gear, "Gear (safety gate)", criteria))
            return 1;
        if (snapshot.Touchdown is null)
        {
            const string reason = "Touchdown telemetry was not captured.";
            if (context.IsFree)
                return AppendUnavailableGate(FreeFlightGateIds.Gear, "Gear (safety gate)", reason,
                    cfg.MultiplierOnFail, criteria, incompleteReasons, context);
            incompleteReasons.Add("Gear gate: " + reason);
            criteria.Add(new CriterionScore
            {
                Id = "gear",
                DisplayName = "Gear (safety gate)",
                Status = MetricStatus.Unavailable,
                UnavailableReason = reason,
                Note = reason
            });
            return 1;
        }

        if (!challenge.RequireGearDown)
        {
            criteria.Add(new CriterionScore
            {
                Id = "gear",
                DisplayName = "Gear (not required)",
                RawValue = snapshot.GearDownAtTouchdown ? 1 : 0,
                Status = MetricStatus.Informational,
                AppliedMultiplier = 1,
                Note = "Challenge allows gear-up landings. Gear position is informational and awards no points."
            });
            return 1;
        }

        if (snapshot.GearDownAtTouchdown)
        {
            criteria.Add(new CriterionScore
            {
                Id = "gear",
                DisplayName = "Gear (safety gate)",
                RawValue = 1,
                Status = MetricStatus.Informational,
                AppliedMultiplier = 1,
                Note = "Gear down is the required baseline and awards no score credit."
            });
            return 1;
        }

        var multiplier = cfg.MultiplierOnFail;
        criteria.Add(new CriterionScore
        {
            Id = "gear",
            DisplayName = "Gear UP — hard penalty",
            RawValue = 0,
            Status = MetricStatus.GateFailed,
            AppliedMultiplier = multiplier,
            Note =
                $"Gear up at touchdown. Combined ranked score × {multiplier:0.##} " +
                $"(~{(1 - multiplier) * 100:0}% overall cut). {cfg.PenaltyDescription}"
        });
        return multiplier;
    }

    /// <summary>Preview flaps: no TD yet → assume OK; after TD use real flaps index.</summary>
    private double AppendFlapsGatePreview(
        FlapsGateConfig cfg,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        FreeGateEvaluationContext context)
    {
        if (AppendNotApplicable(context, FreeFlightGateIds.Flaps, "Flaps (safety gate)", criteria))
            return 1;
        if (snapshot.Touchdown is null)
        {
            criteria.Add(new CriterionScore
            {
                Id = "flaps",
                DisplayName = "Flaps (safety gate)",
                Status = MetricStatus.Informational,
                Note = "[PREVIEW · assumed OK] Flaps not yet assessed (no touchdown)."
            });
            return 1;
        }

        var incomplete = new List<string>();
        return AppendFlapsGate(cfg, snapshot, criteria, incomplete, context);
    }

    /// <summary>
    /// Flaps gate like gear: correct landing flaps award no points;
    /// flaps not set multiplies the combined ranked score.
    /// Free Flight adapts the band from frozen handle-position count; if the measured
    /// index exceeds that count (NUM HANDLE POSITIONS under-report), treat as full flaps.
    /// </summary>
    private double AppendFlapsGate(
        FlapsGateConfig cfg,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        FreeGateEvaluationContext context)
    {
        if (AppendNotApplicable(context, FreeFlightGateIds.Flaps, "Flaps (safety gate)", criteria))
            return 1;
        if (snapshot.Touchdown is null)
        {
            const string reason = "Touchdown telemetry was not captured.";
            if (context.IsFree)
                return AppendUnavailableGate(FreeFlightGateIds.Flaps, "Flaps (safety gate)", reason,
                    cfg.MultiplierOnFail, criteria, incompleteReasons, context);
            incompleteReasons.Add("Flaps gate: " + reason);
            criteria.Add(new CriterionScore
            {
                Id = "flaps",
                DisplayName = "Flaps (safety gate)",
                Status = MetricStatus.Unavailable,
                UnavailableReason = reason,
                Note = reason
            });
            return 1;
        }

        var index = snapshot.FlapsIndexAtTouchdown;
        var min = cfg.MinIndex;
        var max = cfg.MaxIndex;
        if (context.IsFree && context.Capabilities?.FlapHandlePositionCount is { } positions and >= 2)
        {
            min = Math.Min(2, positions - 1);
            // Last detent from frozen FLAPS NUM HANDLE POSITIONS (indices 0…N-1).
            max = positions - 1;
            // Some aircraft report HANDLE INDEX beyond the frozen position count
            // (under-counted NUM HANDLE POSITIONS at arm). More flaps than the
            // reported max is still a landing configuration — never "not set".
            if (index > max)
                max = index;
        }
        if (index >= min && index <= max)
        {
            criteria.Add(new CriterionScore
            {
                Id = "flaps",
                DisplayName = "Flaps (safety gate)",
                RawValue = index,
                Unit = "index",
                Status = MetricStatus.Informational,
                AppliedMultiplier = 1,
                Note =
                    $"Flaps index {index} in required landing band [{min:0}…{max:0}]. " +
                    "Baseline only — awards no score credit."
            });
            return 1;
        }

        var multiplier = cfg.MultiplierOnFail;
        criteria.Add(new CriterionScore
        {
            Id = "flaps",
            DisplayName = "Flaps not set — penalty",
            RawValue = index,
            Unit = "index",
            Status = MetricStatus.GateFailed,
            AppliedMultiplier = multiplier,
            Note =
                $"Flaps index {index} outside landing band [{min:0}…{max:0}]. " +
                $"Combined ranked score × {multiplier:0.##} " +
                $"(~{(1 - multiplier) * 100:0}% overall cut). {cfg.PenaltyDescription}"
        });
        return multiplier;
    }

    private static bool AppendNotApplicable(
        FreeGateEvaluationContext context,
        string id,
        string name,
        List<CriterionScore> criteria)
    {
        if (!context.IsFree) return false;
        var decision = context.DecisionFor(id);
        if (decision.Applicability != FreeFlightGateApplicability.NotApplicable) return false;
        criteria.Add(new CriterionScore
        {
            Id = id,
            DisplayName = name + " - not applicable",
            Status = MetricStatus.NotApplicable,
            AppliedMultiplier = 1,
            Note = decision.Reason
        });
        return true;
    }

    private static double AppendUnavailableGate(
        string id,
        string name,
        string reason,
        double configuredFailureMultiplier,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        FreeGateEvaluationContext context,
        double? raw = null,
        string? unit = null)
    {
        if (context.IsFree)
        {
            var multiplier = context.MissingGateMultiplier(configuredFailureMultiplier);
            criteria.Add(new CriterionScore
            {
                Id = id,
                DisplayName = name + " - assumed telemetry adjustment",
                RawValue = raw,
                Unit = unit,
                Status = MetricStatus.Assumed,
                AppliedMultiplier = multiplier,
                UnavailableReason = reason,
                Note = $"Telemetry was unavailable, so Free Flight applied half of the configured gate loss: " +
                       $"multiplier {multiplier:0.###} (normal failure {configuredFailureMultiplier:0.###}). " +
                       $"{reason} {context.DecisionFor(id).Reason}"
            });
            return multiplier;
        }

        incompleteReasons.Add($"{name}: {reason}");
        criteria.Add(new CriterionScore
        {
            Id = id,
            DisplayName = name,
            RawValue = raw,
            Unit = unit,
            Status = MetricStatus.Unavailable,
            UnavailableReason = reason,
            Note = reason
        });
        return 1;
    }

    /// <summary>
    /// Facility/catalog runway length is known as soon as free-flight locks (or a catalog
    /// challenge is selected). Persist it on diagnostics for highscore reports even when
    /// the rollout settle gate never evaluated remaining distance.
    /// </summary>
    private static void EnsureRunwayLengthLatched(
        LandingResultDiagnostics diagnostics,
        ChallengeConfig challenge)
    {
        if (diagnostics.OperationalGates.RunwayLengthMeters is > 0)
            return;
        var length = challenge.Runway.LengthM;
        if (double.IsFinite(length) && length > 0)
            diagnostics.OperationalGates.RunwayLengthMeters = length;
    }
}
