using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

internal static class OperationalGateEvaluator
{
    public static double AppendPhase(
        EvaluationPhasePenalties? penalties,
        string phaseDisplayName,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        bool preview,
        FreeGateEvaluationContext context)
    {
        if (penalties is null) return 1;

        var observations = snapshot.GateObservations;
        var multiplier = 1.0;
        var scoreTarget = $"{phaseDisplayName} phase";

        if (penalties.SpoilerDeployment is { } spoiler)
            multiplier *= AppendSpoilers(spoiler, snapshot, observations, criteria, incompleteReasons, preview, scoreTarget, context);
        if (penalties.ManualBraking is { } brakes)
            multiplier *= AppendBrakes(brakes, observations, criteria, incompleteReasons, preview, scoreTarget, context);
        if (penalties.NoseGearImpact is { } noseImpact)
            multiplier *= AppendNoseGearImpact(noseImpact, observations, criteria, incompleteReasons, preview, scoreTarget, context);
        if (penalties.Automation is { } automation)
            multiplier *= AppendAutomation(automation, observations, criteria, incompleteReasons, preview, scoreTarget, context);
        if (penalties.Rollout is { } rollout)
            multiplier *= AppendRollout(rollout, observations, criteria, incompleteReasons, preview, scoreTarget, context);
        if (penalties.ReverseThrust is { } reverseThrust)
            multiplier *= AppendReverseThrust(reverseThrust, snapshot, observations, criteria, incompleteReasons, preview, scoreTarget, context);

        return multiplier;
    }

    public static double AppendGeneral(
        GeneralPenaltyConfig? penalties,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        bool preview,
        FreeGateEvaluationContext context)
    {
        if (penalties is null) return 1;

        var observations = snapshot.GateObservations;
        var multiplier = 1.0;
        const string scoreTarget = "combined ranked score";

        if (penalties.PauseUsage is { } pause)
            multiplier *= AppendPause(pause, observations, criteria, incompleteReasons, preview, scoreTarget, context);
        if (penalties.SimulationRate is { } rate)
            multiplier *= AppendSimulationRate(rate, observations, criteria, incompleteReasons, preview, scoreTarget, context);

        return multiplier;
    }

    private static double AppendSpoilers(
        SpoilerDeploymentGateConfig cfg,
        LandingSnapshot snapshot,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        string scoreTarget,
        FreeGateEvaluationContext context)
    {
        const string id = "spoiler_deployment";
        const string name = "Ground spoilers deployed";
        if (AppendNotApplicableIfNeeded(context, id, name, criteria))
            return 1;
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview, cfg.MultiplierOnFail, context,
                out var monitoringMultiplier))
            return monitoringMultiplier;
        if (obs.MainGearTouchdownTimeSeconds is null)
            return PendingOrUnavailable(id, name, "Accepted main-gear touchdown timing is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (!obs.SpoilerTelemetryCoverageAvailable)
            return PendingOrUnavailable(id, name, "Independent left/right spoiler-surface telemetry is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);

        var elapsed = obs.FirstSpoilerDeploymentTimeSeconds - obs.MainGearTouchdownTimeSeconds;
        if (elapsed is { } seconds && seconds <= cfg.DeadlineSecondsAfterTouchdown + 1e-9)
        {
            AddPassed(criteria, id, name, seconds, "s after touchdown",
                $"Both spoiler surfaces reached at least {cfg.MinimumSurfacePosition:P0} by TD+{cfg.DeadlineSecondsAfterTouchdown:0.##} s.");
            return 1;
        }

        if (preview && LatestTime(snapshot) < obs.MainGearTouchdownTimeSeconds + cfg.DeadlineSecondsAfterTouchdown)
        {
            AddPending(criteria, id, name,
                $"Waiting until TD+{cfg.DeadlineSecondsAfterTouchdown:0.##} s for both spoiler surfaces.");
            return 1;
        }

        AddFailed(criteria, id, name, elapsed, "s after touchdown", cfg.MultiplierOnFail, scoreTarget,
            $"Both spoiler surfaces did not reach {cfg.MinimumSurfacePosition:P0} inside the inclusive touchdown window. {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendNoseGearImpact(
        NoseGearImpactGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        string scoreTarget,
        FreeGateEvaluationContext context)
    {
        const string id = "nose_gear_impact";
        const string name = "Nose-gear impact";
        if (AppendNotApplicableIfNeeded(context, id, name, criteria))
            return 1;
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview, cfg.ModerateMultiplier, context,
                out var monitoringMultiplier))
            return monitoringMultiplier;

        var analysis = obs.NoseGearImpact;
        if (analysis is null)
            return PendingOrUnavailable(id, name,
                "Nose-gear impact analysis has not completed.", criteria, incomplete, preview, cfg.ModerateMultiplier, context);
        if (!analysis.CoverageSufficient)
            return PendingOrUnavailable(id, name,
                analysis.DegradedReason ?? "Nose-gear contact or aircraft-G coverage is unavailable.",
                criteria, incomplete, preview, cfg.ModerateMultiplier, context);
        if (analysis.WorstEvent is not { } impact)
            return PendingOrUnavailable(id, name,
                "A verified nose-gear touchdown was not observed.", criteria, incomplete, preview, cfg.ModerateMultiplier, context);

        var afterMain = obs.MainGearTouchdownTimeSeconds is { } main
            ? impact.ContactTimeSeconds - main
            : (double?)null;
        var baseline = impact.MedianPreContactG is { } pre ? $"{pre:0.00} G" : "unavailable";
        var compression = impact.CompressionCorroborated
            ? $"Suspension compression corroborated contact point(s) {string.Join(", ", impact.CorrelatedContactPointIndices)}" +
              (impact.CompressionRise is { } rise ? $" with a {rise:P0} rise." : ".")
            : "Contact-point compression could not be correlated; the ranked aircraft-G fallback was used.";
        var timing = afterMain is { } seconds
            ? $"Nose contact was main TD+{seconds:0.00} s. "
            : "";
        var measured = $"{timing}Baseline {baseline}; robust peak {impact.RobustPeakG:0.00} G; ΔG {impact.DeltaG:0.00}. {compression}";

        if (impact.Severity == NoseGearImpactSeverity.Pass)
        {
            AddPassed(criteria, id, name, impact.DeltaG, "ΔG",
                $"{measured} The nose gear was lowered without a penalized impact.");
            return 1;
        }

        var severity = impact.Severity == NoseGearImpactSeverity.Severe ? "Severe" : "Moderate";
        AddFailed(criteria, id, $"{name} — {severity.ToLowerInvariant()}", impact.DeltaG, "ΔG",
            impact.AppliedMultiplier, scoreTarget,
            $"{severity} nose-gear impact. {measured} {cfg.PenaltyDescription}");
        return impact.AppliedMultiplier;
    }

    private static double AppendBrakes(
        ManualBrakingGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        string scoreTarget,
        FreeGateEvaluationContext context)
    {
        const string id = "manual_braking";
        const string name = "Manual braking after nose touchdown";
        if (AppendNotApplicableIfNeeded(context, id, name, criteria))
            return 1;
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview, cfg.MultiplierOnFail, context,
                out var monitoringMultiplier))
            return monitoringMultiplier;
        if (!obs.NoseGearContactCoverageAvailable)
            return PendingOrUnavailable(id, name, "Nose-gear contact mapping is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (!obs.ManualBrakeTelemetryCoverageAvailable)
            return PendingOrUnavailable(id, name, "Independent manual brake-pedal telemetry is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (obs.NoseGearTouchdownTimeSeconds is null)
            return PendingOrUnavailable(id, name, "A verified nose-gear touchdown was not observed.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);

        var elapsed = obs.FirstSimultaneousBrakingTimeSeconds - obs.NoseGearTouchdownTimeSeconds;
        var failed = obs.EarlyOrAirborneBrakeViolation
                     || elapsed is null
                     || elapsed > cfg.DeadlineSecondsAfterNoseTouchdown + 1e-9;
        if (!failed)
        {
            AddPassed(criteria, id, name, elapsed, "s after nose touchdown",
                $"Both pedals exceeded {cfg.PedalPressThreshold:P0} within {cfg.DeadlineSecondsAfterNoseTouchdown:0.##} s and no pedal was pressed with the nose gear airborne.");
            return 1;
        }

        if (preview && !obs.EarlyOrAirborneBrakeViolation && elapsed is null)
        {
            AddPending(criteria, id, name,
                $"Waiting for both manual brake pedals by nose TD+{cfg.DeadlineSecondsAfterNoseTouchdown:0.##} s.");
            return 1;
        }

        var reason = obs.EarlyOrAirborneBrakeViolation
            ? "A manual brake pedal was pressed while the nose gear was airborne."
            : $"Both pedals were not applied by nose TD+{cfg.DeadlineSecondsAfterNoseTouchdown:0.##} s.";
        AddFailed(criteria, id, name, elapsed, "s after nose touchdown", cfg.MultiplierOnFail, scoreTarget,
            $"{reason} {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendAutomation(
        AutomationGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        string scoreTarget,
        FreeGateEvaluationContext context)
    {
        const string id = "automation";
        const string name = "Automation disconnected by radio altitude";
        if (AppendNotApplicableIfNeeded(context, id, name, criteria))
            return 1;
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview, cfg.MultiplierOnFail, context,
                out var monitoringMultiplier))
            return monitoringMultiplier;
        if (!obs.RadioHeightCoverageAvailable)
            return PendingOrUnavailable(id, name, "Radio-altitude telemetry is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (!obs.HeadingAltitudeThresholdObserved || !obs.HeadingAltitudeAutomationCoverageAvailable)
            return PendingOrUnavailable(id, name,
                $"Heading/altitude-hold state was not covered at or below {cfg.HeadingAltitudeOffRadioHeightFeet:0} ft RA.",
                criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (!obs.FullAutomationThresholdObserved || !obs.FullAutomationCoverageAvailable)
            return PendingOrUnavailable(id, name,
                $"AP/AP1/AP2/autothrust state was not covered at or below {cfg.AllAutomationOffRadioHeightFeet:0} ft RA.",
                criteria, incomplete, preview, cfg.MultiplierOnFail, context);

        if (!obs.AutomationViolation)
        {
            AddPassed(criteria, id, name, 0, "violations",
                $"Heading and altitude hold were off by {cfg.HeadingAltitudeOffRadioHeightFeet:0} ft RA; AP master/AP1/AP2 and active/armed autothrust were off by {cfg.AllAutomationOffRadioHeightFeet:0} ft RA. Flight directors are allowed.");
            return 1;
        }

        AddFailed(criteria, id, name, obs.FirstAutomationViolationRadioHeightFeet, "ft RA",
            cfg.MultiplierOnFail, scoreTarget,
            $"First violation: {obs.FirstAutomationViolation} at {obs.FirstAutomationViolationRadioHeightFeet:0} ft RA. {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendPause(
        PauseUsageGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        string scoreTarget,
        FreeGateEvaluationContext context)
    {
        const string id = "pause_usage";
        const string name = "No pause before touchdown";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview, cfg.MultiplierOnFail, context,
                out var monitoringMultiplier))
            return monitoringMultiplier;
        if (!obs.PauseCoverageAvailable)
            return PendingOrUnavailable(id, name, "Pause_EX1 state coverage is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (!obs.PauseViolation)
        {
            AddPassed(criteria, id, name, 0, "pause events",
                "No normal pause or Active Pause occurred after the controlled start hold and before main-gear touchdown.");
            return 1;
        }

        AddFailed(criteria, id, name, 1, "violation", cfg.MultiplierOnFail, scoreTarget,
            $"A normal pause or Active Pause occurred before touchdown. {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendSimulationRate(
        SimulationRateGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        string scoreTarget,
        FreeGateEvaluationContext context)
    {
        const string id = "simulation_rate";
        const string name = "Simulation rate not reduced";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview, cfg.MultiplierOnFail, context,
                out var monitoringMultiplier))
            return monitoringMultiplier;
        if (!obs.SimulationRateCoverageAvailable)
            return PendingOrUnavailable(id, name, "Simulation-rate telemetry is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (!obs.ReducedSimulationRateViolation)
        {
            AddPassed(criteria, id, name, obs.MinimumSimulationRate, "x",
                $"Simulation rate never fell below {cfg.MinimumAllowedRate:0.###}x before touchdown.");
            return 1;
        }

        AddFailed(criteria, id, name, obs.MinimumSimulationRate, "x", cfg.MultiplierOnFail, scoreTarget,
            $"Simulation rate fell below {cfg.MinimumAllowedRate:0.###}x before touchdown. {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendRollout(
        RolloutGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        string scoreTarget,
        FreeGateEvaluationContext context)
    {
        const string id = "rollout_distance";
        const string name = "Rollout remaining runway";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview, cfg.MultiplierOnFail, context,
                out var monitoringMultiplier))
            return monitoringMultiplier;
        if (obs.MainGearTouchdownTimeSeconds is null)
            return PendingOrUnavailable(id, name, "Accepted main-gear touchdown timing is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (!obs.RolloutDistanceEvaluated)
        {
            return PendingOrUnavailable(id, name,
                "Groundspeed has not yet fallen below the settle threshold for remaining-runway evaluation.",
                criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        }

        if (obs.RemainingRunwayMetersAtSettleSpeed is not { } remaining
            || obs.RequiredRemainingRunwayMeters is not { } required)
            return PendingOrUnavailable(id, name,
                "Remaining runway could not be measured at the settle groundspeed threshold.",
                criteria, incomplete, preview, cfg.MultiplierOnFail, context);

        var lengthNote = obs.RunwayLengthMeters is { } length
            ? $"Runway length {length:0} m; required remaining max(400 m, 15% of length) = {required:0} m. "
            : $"Required remaining {required:0} m. ";
        var speedNote = obs.GroundSpeedKtsAtRolloutCheck is { } gs
            ? $"Measured at {gs:0.#} kt groundspeed. "
            : "";

        if (!obs.RolloutEndOfRunwayViolation)
        {
            AddPassed(criteria, id, name, remaining, "m remaining",
                $"{lengthNote}{speedNote}Remaining runway was {remaining:0} m (≥ {required:0} m).");
            return 1;
        }

        AddFailed(criteria, id, name, remaining, "m remaining", cfg.MultiplierOnFail, scoreTarget,
            $"{lengthNote}{speedNote}Remaining runway was only {remaining:0} m (< {required:0} m). {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendReverseThrust(
        ReverseThrustGateConfig cfg,
        LandingSnapshot snapshot,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        string scoreTarget,
        FreeGateEvaluationContext context)
    {
        const string id = "reverse_thrust";
        const string name = "Reverse-thrust procedure";
        if (AppendNotApplicableIfNeeded(context, id, name, criteria))
            return 1;
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview, cfg.MultiplierOnFail, context,
                out var monitoringMultiplier))
            return monitoringMultiplier;
        if (obs.MainGearTouchdownTimeSeconds is not { } touchdownTime)
            return PendingOrUnavailable(id, name, "Accepted main-gear touchdown timing is unavailable.", criteria, incomplete, preview, cfg.MultiplierOnFail, context);
        if (!obs.ReverseThrustTelemetryCoverageAvailable
            || !obs.OperatingEnginesCapturedAtTouchdown)
            return PendingOrUnavailable(id, name,
                "Per-engine count, combustion, reverse engagement, nozzle, and throttle telemetry is unavailable.",
                criteria, incomplete, preview, cfg.MultiplierOnFail, context);

        var policy = ReverseThrustPolicies.Normalize(cfg.Policy);
        var operating = obs.OperatingEngineIndicesAtTouchdown;
        var operatingLabel = operating.Count == 0 ? "none" : string.Join(", ", operating);
        var selectedElapsed = obs.FirstReverseSelectionTimeSecondsByEngine
            .ToDictionary(pair => pair.Key, pair => pair.Value - touchdownTime);
        var latestRequiredSelection = operating
            .Where(selectedElapsed.ContainsKey)
            .Select(index => selectedElapsed[index])
            .DefaultIfEmpty()
            .Max();

        if (preview
            && policy == ReverseThrustPolicies.Required
            && !obs.ReverseApplicationWaivedByLowSpeed
            && operating.Any(index => !selectedElapsed.TryGetValue(index, out var elapsed)
                                      || elapsed > cfg.DeadlineSecondsAfterTouchdown + 1e-9)
            && LatestTime(snapshot) < touchdownTime + cfg.DeadlineSecondsAfterTouchdown)
        {
            AddPending(criteria, id, name,
                $"Waiting for operating engines [{operatingLabel}] to select reverse by TD+{cfg.DeadlineSecondsAfterTouchdown:0.##} s.");
            return 1;
        }

        if (!obs.ReverseThrustStowEvaluated || !obs.ReverseThrustStowCoverageAvailable)
            return PendingOrUnavailable(id, name,
                $"Complete per-engine reverse state was not observed at or below {cfg.StowGroundSpeedKts:0.##} kt groundspeed.",
                criteria, incomplete, preview, cfg.MultiplierOnFail, context);

        var failures = new List<string>();
        if (obs.AirborneReverseViolation && policy != ReverseThrustPolicies.Prohibited)
            failures.Add("Reverse was selected before accepted main-gear touchdown");

        switch (policy)
        {
            case ReverseThrustPolicies.Required:
                if (!obs.ReverseApplicationWaivedByLowSpeed)
                {
                    var missingOrLate = operating.Where(index =>
                            !selectedElapsed.TryGetValue(index, out var elapsed)
                            || elapsed > cfg.DeadlineSecondsAfterTouchdown + 1e-9)
                        .ToList();
                    if (missingOrLate.Count > 0)
                        failures.Add(
                            $"engine(s) {string.Join(", ", missingOrLate)} did not select reverse inside the inclusive TD+{cfg.DeadlineSecondsAfterTouchdown:0.##} s window");
                }
                if (!obs.ReverseThrustStowedAtThreshold)
                    failures.Add(
                        $"engine(s) {string.Join(", ", obs.EnginesNotStowedAtThreshold)} were not completely stowed by {cfg.StowGroundSpeedKts:0.##} kt");
                break;

            case ReverseThrustPolicies.OptionalIdleOnly:
                if (obs.PoweredReverseViolation)
                    failures.Add(
                        $"powered reverse reached {obs.FirstPoweredReverseThrottlePercent:0.##}% throttle; only stowed or idle reverse is permitted");
                if (obs.FirstReverseSelectionTimeSecondsByEngine.Count > 0
                    && !obs.ReverseThrustStowedAtThreshold)
                    failures.Add(
                        $"engine(s) {string.Join(", ", obs.EnginesNotStowedAtThreshold)} were not completely stowed by {cfg.StowGroundSpeedKts:0.##} kt");
                break;

            case ReverseThrustPolicies.Prohibited:
                if (obs.FirstReverseSelectionTimeSecondsByEngine.Count > 0)
                    failures.Add(
                        $"reverse was selected on engine(s) {string.Join(", ", obs.FirstReverseSelectionTimeSecondsByEngine.Keys.Order())}");
                break;
        }

        var policyNote = policy switch
        {
            ReverseThrustPolicies.Required =>
                $"All engines operating at touchdown [{operatingLabel}] must select at least idle reverse by TD+{cfg.DeadlineSecondsAfterTouchdown:0.##} s and every reverser must be stowed by {cfg.StowGroundSpeedKts:0.##} kt.",
            ReverseThrustPolicies.OptionalIdleOnly =>
                $"Reverse is optional but may not use throttle below {cfg.PoweredReverseThrottleThresholdPercent:0.##}%; selected reversers must be stowed by {cfg.StowGroundSpeedKts:0.##} kt.",
            _ => "Reverse selection is prohibited after touchdown."
        };
        policyNote = $"Effective policy: {policy}. {policyNote}";
        var selectionNote = selectedElapsed.Count == 0
            ? "No post-touchdown reverse selection was detected."
            : "First selection: " + string.Join(", ", selectedElapsed.OrderBy(pair => pair.Key)
                .Select(pair => $"engine {pair.Key} at TD{pair.Value:+0.##;-0.##;+0} s")) + ".";
        policyNote += $" Engine coverage at touchdown: {obs.EngineCountAtTouchdown ?? operating.Count} installed; "
                      + $"operating engines [{operatingLabel}]. {selectionNote}";
        policyNote += obs.PoweredReverseViolation
            ? $" Powered reverse detected at {obs.FirstPoweredReverseThrottlePercent:0.##}% throttle"
              + (obs.FirstPoweredReverseTimeSeconds is { } poweredTime
                  ? $" at TD{poweredTime - touchdownTime:+0.##;-0.##;+0} s."
                  : ".")
            : " Powered reverse detected: no.";
        if (!string.IsNullOrWhiteSpace(cfg.ExceptionReason))
            policyNote += $" Exception: {cfg.ExceptionReason}";
        if (obs.ReverseApplicationWaivedByLowSpeed)
            policyNote += $" The application deadline was waived because groundspeed reached {cfg.StowGroundSpeedKts:0.##} kt first.";
        if (obs.GroundSpeedKtsAtReverseStowCheck is { } stowSpeed)
            policyNote += $" Stow was checked at {stowSpeed:0.##} kt.";

        if (failures.Count == 0)
        {
            AddPassed(criteria, id, name,
                selectedElapsed.Count == 0 ? null : latestRequiredSelection,
                "s after touchdown", policyNote);
            return 1;
        }

        AddFailed(criteria, id, name,
            selectedElapsed.Count == 0 ? null : latestRequiredSelection,
            "s after touchdown", cfg.MultiplierOnFail, scoreTarget,
            $"{string.Join("; ", failures)}. {policyNote} {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static bool RequireMonitoring(
        string id,
        string name,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        double configuredFailureMultiplier,
        FreeGateEvaluationContext context,
        out double multiplier)
    {
        if (obs.MonitoringStarted)
        {
            multiplier = 1;
            return true;
        }
        multiplier = PendingOrUnavailable(id, name,
            "The first unpaused challenge-flight sample was not observed.", criteria, incomplete, preview,
            configuredFailureMultiplier, context);
        return false;
    }

    private static double PendingOrUnavailable(
        string id,
        string name,
        string reason,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview,
        double configuredFailureMultiplier,
        FreeGateEvaluationContext context)
    {
        if (preview)
        {
            AddPending(criteria, id, name, reason);
            return 1;
        }

        if (context.IsFree)
        {
            var multiplier = context.MissingGateMultiplier(configuredFailureMultiplier);
            var capability = context.DecisionFor(id);
            criteria.Add(new CriterionScore
            {
                Id = id,
                DisplayName = name + " â€” assumed telemetry adjustment",
                Status = MetricStatus.Assumed,
                AppliedMultiplier = multiplier,
                UnavailableReason = reason,
                Note = $"Telemetry was unavailable, so Free Flight applied half of the configured gate loss: " +
                       $"multiplier {multiplier:0.###} (normal failure {configuredFailureMultiplier:0.###}). " +
                       $"{reason} {capability.Reason}"
            });
            return multiplier;
        }

        incomplete.Add($"{name}: {reason}");
        criteria.Add(new CriterionScore
        {
            Id = id,
            DisplayName = name,
            Status = MetricStatus.Unavailable,
            UnavailableReason = reason,
            Note = reason
        });
        return 1;
    }

    private static bool AppendNotApplicableIfNeeded(
        FreeGateEvaluationContext context,
        string id,
        string name,
        List<CriterionScore> criteria)
    {
        if (!context.IsFree)
            return false;
        var decision = context.DecisionFor(id);
        if (decision.Applicability != FreeFlightGateApplicability.NotApplicable)
            return false;
        criteria.Add(new CriterionScore
        {
            Id = id,
            DisplayName = name + " â€” not applicable",
            Status = MetricStatus.NotApplicable,
            AppliedMultiplier = 1,
            Note = decision.Reason
        });
        return true;
    }

    private static void AddPending(List<CriterionScore> criteria, string id, string name, string note) =>
        criteria.Add(new CriterionScore
        {
            Id = id,
            DisplayName = name,
            Status = MetricStatus.Informational,
            Note = "[PREVIEW · pending] " + note
        });

    private static void AddPassed(
        List<CriterionScore> criteria,
        string id,
        string name,
        double? raw,
        string unit,
        string note) =>
        criteria.Add(new CriterionScore
        {
            Id = id,
            DisplayName = name,
            RawValue = raw,
            Unit = unit,
            Status = MetricStatus.Informational,
            AppliedMultiplier = 1,
            Note = note + " Required baseline met; no points awarded."
        });

    private static void AddFailed(
        List<CriterionScore> criteria,
        string id,
        string name,
        double? raw,
        string unit,
        double multiplier,
        string scoreTarget,
        string note) =>
        criteria.Add(new CriterionScore
        {
            Id = id,
            DisplayName = name + " — penalty gate",
            RawValue = raw,
            Unit = unit,
            Status = MetricStatus.GateFailed,
            AppliedMultiplier = multiplier,
            Note = $"{note} {scoreTarget} × {multiplier:0.##}."
        });

    private static double LatestTime(LandingSnapshot snapshot)
    {
        var sample = snapshot.RolloutSamples.LastOrDefault() ?? snapshot.Touchdown;
        if (sample is null) return double.NegativeInfinity;
        return double.IsFinite(sample.SimulationTimeSeconds)
            ? sample.SimulationTimeSeconds
            : sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0;
    }
}

internal sealed record FreeGateEvaluationContext(
    FreeModeScoringPolicy? Policy,
    FreeFlightCapabilityContext? Capabilities)
{
    public bool IsFree => Policy is not null;

    public FreeFlightGateDecision DecisionFor(string gateId) => IsFree
        ? FreeFlightCapabilityResolver.ResolveDecision(Capabilities, gateId)
        : new FreeFlightGateDecision
        {
            Applicability = FreeFlightGateApplicability.Applicable,
            Reason = "Authored Challenge/Career gate."
        };

    public double MissingGateMultiplier(double configuredFailureMultiplier) =>
        Policy?.MissingGateMultiplier(configuredFailureMultiplier) ?? 1;
}
