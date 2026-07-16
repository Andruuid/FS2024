using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

internal static class OperationalGateEvaluator
{
    public static double Append(
        LandingEvaluationKey key,
        LandingSnapshot snapshot,
        List<CriterionScore> criteria,
        List<string> incompleteReasons,
        bool preview)
    {
        var observations = snapshot.GateObservations;
        var multiplier = 1.0;

        if (key.Gates?.SpoilerDeployment is { } spoiler)
            multiplier *= AppendSpoilers(spoiler, snapshot, observations, criteria, incompleteReasons, preview);
        if (key.Gates?.ManualBraking is { } brakes)
            multiplier *= AppendBrakes(brakes, observations, criteria, incompleteReasons, preview);
        if (key.Gates?.NoseGearImpact is { } noseImpact)
            multiplier *= AppendNoseGearImpact(noseImpact, observations, criteria, incompleteReasons, preview);
        if (key.Gates?.Automation is { } automation)
            multiplier *= AppendAutomation(automation, observations, criteria, incompleteReasons, preview);
        if (key.Gates?.PauseUsage is { } pause)
            multiplier *= AppendPause(pause, observations, criteria, incompleteReasons, preview);
        if (key.Gates?.SimulationRate is { } rate)
            multiplier *= AppendSimulationRate(rate, observations, criteria, incompleteReasons, preview);

        return multiplier;
    }

    private static double AppendSpoilers(
        SpoilerDeploymentGateConfig cfg,
        LandingSnapshot snapshot,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview)
    {
        const string id = "spoiler_deployment";
        const string name = "Ground spoilers deployed";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview))
            return 1;
        if (obs.MainGearTouchdownTimeSeconds is null)
            return PendingOrUnavailable(id, name, "Accepted main-gear touchdown timing is unavailable.", criteria, incomplete, preview);
        if (!obs.SpoilerTelemetryCoverageAvailable)
            return PendingOrUnavailable(id, name, "Independent left/right spoiler-surface telemetry is unavailable.", criteria, incomplete, preview);

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

        AddFailed(criteria, id, name, elapsed, "s after touchdown", cfg.MultiplierOnFail,
            $"Both spoiler surfaces did not reach {cfg.MinimumSurfacePosition:P0} inside the inclusive touchdown window. {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendNoseGearImpact(
        NoseGearImpactGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview)
    {
        const string id = "nose_gear_impact";
        const string name = "Nose-gear impact";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview))
            return 1;

        var analysis = obs.NoseGearImpact;
        if (analysis is null)
            return PendingOrUnavailable(id, name,
                "Nose-gear impact analysis has not completed.", criteria, incomplete, preview);
        if (!analysis.CoverageSufficient)
            return PendingOrUnavailable(id, name,
                analysis.DegradedReason ?? "Nose-gear contact or aircraft-G coverage is unavailable.",
                criteria, incomplete, preview);
        if (analysis.WorstEvent is not { } impact)
            return PendingOrUnavailable(id, name,
                "A verified nose-gear touchdown was not observed.", criteria, incomplete, preview);

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
            impact.AppliedMultiplier,
            $"{severity} nose-gear impact. {measured} {cfg.PenaltyDescription}");
        return impact.AppliedMultiplier;
    }

    private static double AppendBrakes(
        ManualBrakingGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview)
    {
        const string id = "manual_braking";
        const string name = "Manual braking after nose touchdown";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview))
            return 1;
        if (!obs.NoseGearContactCoverageAvailable)
            return PendingOrUnavailable(id, name, "Nose-gear contact mapping is unavailable.", criteria, incomplete, preview);
        if (!obs.ManualBrakeTelemetryCoverageAvailable)
            return PendingOrUnavailable(id, name, "Independent manual brake-pedal telemetry is unavailable.", criteria, incomplete, preview);
        if (obs.NoseGearTouchdownTimeSeconds is null)
            return PendingOrUnavailable(id, name, "A verified nose-gear touchdown was not observed.", criteria, incomplete, preview);

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
        AddFailed(criteria, id, name, elapsed, "s after nose touchdown", cfg.MultiplierOnFail,
            $"{reason} {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendAutomation(
        AutomationGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview)
    {
        const string id = "automation";
        const string name = "Automation disconnected by radio altitude";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview))
            return 1;
        if (!obs.RadioHeightCoverageAvailable)
            return PendingOrUnavailable(id, name, "Radio-altitude telemetry is unavailable.", criteria, incomplete, preview);
        if (!obs.HeadingAltitudeThresholdObserved || !obs.HeadingAltitudeAutomationCoverageAvailable)
            return PendingOrUnavailable(id, name,
                $"Heading/altitude-hold state was not covered at or below {cfg.HeadingAltitudeOffRadioHeightFeet:0} ft RA.",
                criteria, incomplete, preview);
        if (!obs.FullAutomationThresholdObserved || !obs.FullAutomationCoverageAvailable)
            return PendingOrUnavailable(id, name,
                $"AP/AP1/AP2/autothrust state was not covered at or below {cfg.AllAutomationOffRadioHeightFeet:0} ft RA.",
                criteria, incomplete, preview);

        if (!obs.AutomationViolation)
        {
            AddPassed(criteria, id, name, 0, "violations",
                $"Heading and altitude hold were off by {cfg.HeadingAltitudeOffRadioHeightFeet:0} ft RA; AP master/AP1/AP2 and active/armed autothrust were off by {cfg.AllAutomationOffRadioHeightFeet:0} ft RA. Flight directors are allowed.");
            return 1;
        }

        AddFailed(criteria, id, name, obs.FirstAutomationViolationRadioHeightFeet, "ft RA",
            cfg.MultiplierOnFail,
            $"First violation: {obs.FirstAutomationViolation} at {obs.FirstAutomationViolationRadioHeightFeet:0} ft RA. {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendPause(
        PauseUsageGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview)
    {
        const string id = "pause_usage";
        const string name = "No pause before touchdown";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview))
            return 1;
        if (!obs.PauseCoverageAvailable)
            return PendingOrUnavailable(id, name, "Pause_EX1 state coverage is unavailable.", criteria, incomplete, preview);
        if (!obs.PauseViolation)
        {
            AddPassed(criteria, id, name, 0, "pause events",
                "No normal pause or Active Pause occurred after the controlled start hold and before main-gear touchdown.");
            return 1;
        }

        AddFailed(criteria, id, name, 1, "violation", cfg.MultiplierOnFail,
            $"A normal pause or Active Pause occurred before touchdown. {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static double AppendSimulationRate(
        SimulationRateGateConfig cfg,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview)
    {
        const string id = "simulation_rate";
        const string name = "Simulation rate not reduced";
        if (!RequireMonitoring(id, name, obs, criteria, incomplete, preview))
            return 1;
        if (!obs.SimulationRateCoverageAvailable)
            return PendingOrUnavailable(id, name, "Simulation-rate telemetry is unavailable.", criteria, incomplete, preview);
        if (!obs.ReducedSimulationRateViolation)
        {
            AddPassed(criteria, id, name, obs.MinimumSimulationRate, "x",
                $"Simulation rate never fell below {cfg.MinimumAllowedRate:0.###}x before touchdown.");
            return 1;
        }

        AddFailed(criteria, id, name, obs.MinimumSimulationRate, "x", cfg.MultiplierOnFail,
            $"Simulation rate fell below {cfg.MinimumAllowedRate:0.###}x before touchdown. {cfg.PenaltyDescription}");
        return cfg.MultiplierOnFail;
    }

    private static bool RequireMonitoring(
        string id,
        string name,
        LandingGateObservations obs,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview)
    {
        if (obs.MonitoringStarted)
            return true;
        PendingOrUnavailable(id, name,
            "The first unpaused challenge-flight sample was not observed.", criteria, incomplete, preview);
        return false;
    }

    private static double PendingOrUnavailable(
        string id,
        string name,
        string reason,
        List<CriterionScore> criteria,
        List<string> incomplete,
        bool preview)
    {
        if (preview)
        {
            AddPending(criteria, id, name, reason);
            return 1;
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
            Note = note + " Required baseline met; no points awarded."
        });

    private static void AddFailed(
        List<CriterionScore> criteria,
        string id,
        string name,
        double? raw,
        string unit,
        double multiplier,
        string note) =>
        criteria.Add(new CriterionScore
        {
            Id = id,
            DisplayName = name + " — penalty gate",
            RawValue = raw,
            Unit = unit,
            Status = MetricStatus.GateFailed,
            Note = $"{note} Ranked overall score × {multiplier:0.##}."
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
