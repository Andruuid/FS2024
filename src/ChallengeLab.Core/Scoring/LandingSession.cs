using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Tracks approach → touchdown → rollout → settled and builds a <see cref="LandingSnapshot"/>.
/// </summary>
public sealed class LandingSession
{
    private readonly ChallengeConfig _challenge;
    private readonly LandingSessionSettings _settings;
    private readonly GeoUtil _geo;
    private double? _settledSince;
    private DateTimeOffset _armedAt;
    private bool _wasAnyMainOnGround;
    private bool _wasNoseOnGround;
    private bool _touchdownCaptured;
    private int _airborneSampleCount;
    private readonly List<double> _rolloutHeadings = new();
    private double _armedTimeSeconds = double.NaN;
    private double _lastTimeSeconds = double.NaN;
    private double _touchdownTimeSeconds = double.NaN;
    private double? _preTouchdownNormalVelocityFps;
    private bool _touchdownVerticalSpeedDegraded;
    private string _touchdownVerticalSpeedSource = "instantaneous vertical speed";
    private double? _noseImpactAirborneSince;
    private bool? _previousCameraInCockpit;

    public LandingPhase Phase { get; private set; } = LandingPhase.Idle;
    public LandingSnapshot Snapshot { get; } = new();
    public bool IsArmed => Phase is not LandingPhase.Idle and not LandingPhase.Scored;
    public bool IsComplete => Phase == LandingPhase.Scored;

    public event EventHandler<LandingPhase>? PhaseChanged;
    public event EventHandler? SettledReady;

    public LandingSession(ChallengeConfig challenge, LandingSessionSettings settings)
    {
        _challenge = challenge;
        _settings = settings;
        _geo = new GeoUtil(challenge.Runway);
        // Free-flight lock already copied facility LengthMeters into challenge.Runway.
        SeedRunwayLengthObservation();
    }

    public void Arm()
    {
        Reset();
        _armedAt = DateTimeOffset.UtcNow;
        SetPhase(LandingPhase.Armed);
    }

    /// <summary>
    /// Wipe all samples and derived metrics for the current landing and re-arm from
    /// this moment. Does not change aircraft position, weather, or challenge setup.
    /// Preview returns to 100% (nothing measured yet). Safe mid-flight or after score.
    /// </summary>
    public void CleanMetrics()
    {
        if (Phase is LandingPhase.Idle)
            return;

        ClearCapturedMetrics();
        _settledSince = null;
        _touchdownCaptured = false;
        _airborneSampleCount = 0;
        _rolloutHeadings.Clear();
        _armedTimeSeconds = double.NaN;
        _lastTimeSeconds = double.NaN;
        _touchdownTimeSeconds = double.NaN;
        _preTouchdownNormalVelocityFps = null;
        _touchdownVerticalSpeedDegraded = false;
        _touchdownVerticalSpeedSource = "instantaneous vertical speed";
        _noseImpactAirborneSince = null;
        _previousCameraInCockpit = null;
        // Re-open post-arm grace and seed ground so a mid-clean on the runway
        // does not instantly re-fire touchdown on the next frame.
        _armedAt = DateTimeOffset.UtcNow;
        _wasAnyMainOnGround = true;
        _wasNoseOnGround = true;
        SetPhase(LandingPhase.Armed);
    }

    public void Reset()
    {
        Phase = LandingPhase.Idle;
        ClearCapturedMetrics();
        _settledSince = null;
        _armedAt = default;
        _wasAnyMainOnGround = false;
        _wasNoseOnGround = false;
        _touchdownCaptured = false;
        _airborneSampleCount = 0;
        _rolloutHeadings.Clear();
        _armedTimeSeconds = double.NaN;
        _lastTimeSeconds = double.NaN;
        _touchdownTimeSeconds = double.NaN;
        _preTouchdownNormalVelocityFps = null;
        _touchdownVerticalSpeedDegraded = false;
        _touchdownVerticalSpeedSource = "instantaneous vertical speed";
        _noseImpactAirborneSince = null;
        _previousCameraInCockpit = null;
    }

    private void ClearCapturedMetrics()
    {
        Snapshot.Touchdown = null;
        Snapshot.PeakGForce = 1.0;
        Snapshot.PeakAbsBankDeg = 0;
        Snapshot.MaxLateralOffsetM = 0;
        Snapshot.TouchdownLateralOffsetM = 0;
        Snapshot.TouchdownHeadingErrorDeg = 0;
        Snapshot.ApproachPathRms = 0;
        Snapshot.ApproachPathSampleCount = 0;
        Snapshot.ApproachGlideslopeMeanAbsFt = 0;
        Snapshot.ApproachVerticalVariationFtPerSec = 0;
        Snapshot.ApproachLateralWeaveIndex = 0;
        Snapshot.ApproachBankMeanAbsDeg = 0;
        Snapshot.ApproachLateralDistanceM = 0;
        Snapshot.ApproachMetricDurationSec = 0;
        Snapshot.RolloutHeadingVariance = 0;
        Snapshot.CrabAngleAtFlareDeg = 0;
        Snapshot.PostTouchdownAlignmentMeanDeg = 0;
        Snapshot.PostTouchdownAlignmentRmsDeg = 0;
        Snapshot.PostTouchdownAlignmentPeakDeg = 0;
        Snapshot.PostTouchdownAlignmentSampleCount = 0;
        Snapshot.RolloutLateralMeanM = 0;
        Snapshot.RolloutLateralPeakM = 0;
        Snapshot.RolloutWeaveIndex = 0;
        Snapshot.RolloutDistanceM = 0;
        Snapshot.RolloutPathSampleCount = 0;
        Snapshot.RolloutPathSegmentCount = 0;
        Snapshot.GearDownAtTouchdown = true;
        Snapshot.FlapsIndexAtTouchdown = 0;
        Snapshot.VerticalSpeedAtTouchdownFpm = 0;
        Snapshot.AirspeedAtTouchdownKts = 0;
        Snapshot.BankAtTouchdownDeg = 0;
        Snapshot.PitchAtTouchdownDeg = 0;
        Snapshot.VappKts = 0;
        Snapshot.TargetTouchdownIasKts = 0;
        Snapshot.TouchdownIasErrorKts = 0;
        Snapshot.ExcessSpeedOverVappKts = 0;
        Snapshot.SpeedTargetSource = "";
        Snapshot.InitialImpact = null;
        Snapshot.FloatAnalysis = null;
        Snapshot.ContactStability = null;
        Snapshot.TouchdownAnalysisComplete = false;
        Snapshot.ContactMappingDegraded = false;
        Snapshot.StallWarningOccurred = false;
        Snapshot.StallWarningCoverageAvailable = false;
        Snapshot.GateObservations.Reset();
        SeedRunwayLengthObservation();
        Snapshot.ApproachSamples.Clear();
        Snapshot.RolloutSamples.Clear();
        Snapshot.LandingEventSamples.Clear();
    }

    /// <summary>
    /// Always latch runway length from the challenge identity (not only when the
    /// rollout settle gate fires) so highscore reports can show it.
    /// </summary>
    private void SeedRunwayLengthObservation()
    {
        var length = _challenge.Runway.LengthM;
        if (double.IsFinite(length) && length > 0)
            Snapshot.GateObservations.RunwayLengthMeters = length;
    }

    public void Ingest(TelemetrySample sample)
    {
        if (Phase is LandingPhase.Idle or LandingPhase.Scored)
            return;

        var timeSeconds = ResolveTimeSeconds(sample);
        Snapshot.StallWarningOccurred |= sample.StallWarningActive;
        Snapshot.StallWarningCoverageAvailable |= sample.StallWarningAvailable;
        var logical = ToLandingSample(sample, timeSeconds);
        Snapshot.LandingEventSamples.Add(logical);
        var anyMainOnGround = logical.MainGearContactsAvailable
            ? logical.LeftMainOnGround || logical.RightMainOnGround
            : sample.SimOnGround;

        UpdateOperationalGates(sample, logical, timeSeconds, anyMainOnGround);

        var lateral = _geo.LateralOffsetMeters(sample.Latitude, sample.Longitude);
        Snapshot.MaxLateralOffsetM = Math.Max(Snapshot.MaxLateralOffsetM, Math.Abs(lateral));
        Snapshot.PeakGForce = Math.Max(Snapshot.PeakGForce, sample.GForce);
        Snapshot.PeakAbsBankDeg = Math.Max(Snapshot.PeakAbsBankDeg, Math.Abs(sample.BankDeg));

        var agl = sample.RadioHeightFeet > 0 ? sample.RadioHeightFeet : sample.AglFeet;
        var inPostArmGrace = timeSeconds - _armedTimeSeconds < _settings.PostArmIgnoreSeconds;

        // Count airborne samples after arm (including during grace) for the touchdown gate.
        if (!anyMainOnGround && agl >= _settings.MinAirborneAglFeet)
            _airborneSampleCount++;

        if (Phase is LandingPhase.Armed or LandingPhase.Approach or LandingPhase.Flare)
        {
            if (!anyMainOnGround)
            {
                if (Phase == LandingPhase.Armed)
                    SetPhase(LandingPhase.Approach);

                Snapshot.ApproachSamples.Add(sample);

                if (agl <= _settings.FlareAglFeet && Phase != LandingPhase.Flare)
                {
                    // Kept for diagnostics only — not scored (crab is wind-dependent).
                    Snapshot.CrabAngleAtFlareDeg = NormalizeHeading(sample.HeadingTrueDeg - _challenge.Runway.HeadingTrueDeg);
                    SetPhase(LandingPhase.Flare);
                }
            }
        }

        if (!_touchdownCaptured && !anyMainOnGround && sample.TouchdownNormalVelocityFps is { } pre
            && double.IsFinite(pre))
            _preTouchdownNormalVelocityFps = pre;

        // Touchdown: post-arm grace seeds ground state; require airborne history before a rising edge.
        if (!_touchdownCaptured && !inPostArmGrace && anyMainOnGround && !_wasAnyMainOnGround)
        {
            var airborneOk = !_settings.RequireAirborneBeforeTouchdown
                             || _airborneSampleCount >= _settings.MinAirborneSamples;
            if (airborneOk)
            {
                CaptureTouchdown(sample, logical, lateral);
                SetPhase(LandingPhase.Touchdown);
                SetPhase(LandingPhase.Rollout);
            }
        }

        if (_touchdownCaptured)
        {
            Snapshot.RolloutSamples.Add(sample);
            TryCaptureOfficialTouchdownVelocity(sample, timeSeconds);

            if (sample.SimOnGround)
            {
                _rolloutHeadings.Add(sample.HeadingTrueDeg);

                if (sample.GroundSpeedKts < _settings.SettledGroundSpeedKts)
                {
                    _settledSince ??= timeSeconds;
                    if (timeSeconds - _settledSince.Value >= _settings.SettledHoldSeconds)
                    {
                        if (OperationalGateWindowsComplete(timeSeconds))
                        {
                            FinalizeSnapshot();
                            SetPhase(LandingPhase.Settled);
                            SettledReady?.Invoke(this, EventArgs.Empty);
                            SetPhase(LandingPhase.Scored);
                        }
                    }
                }
                else
                {
                    _settledSince = null;
                }
            }
            else
            {
                // The settle hold is continuous ground time; an airborne bounce reopens it.
                _settledSince = null;
            }
        }

        // Always seed ground state so restart-on-runway does not treat the first frame as TD.
        _wasAnyMainOnGround = anyMainOnGround;
        _wasNoseOnGround = logical.NoseOnGround;

        // Keep derived metrics fresh for live preview scoring (cheap enough at sample rate).
        if (Phase is LandingPhase.Approach or LandingPhase.Flare or LandingPhase.Rollout
            or LandingPhase.Touchdown)
            RefreshDerivedMetrics();
    }

    /// <summary>
    /// Recompute approach / TD-window / rollout derived fields from samples collected so far.
    /// Safe to call mid-flight for HUD preview; final settle uses the same path.
    /// </summary>
    public void RefreshDerivedMetrics(bool finalizing = false)
    {
        ComputeApproachPathMetrics();

        if (_rolloutHeadings.Count > 1)
        {
            var mean = _rolloutHeadings.Average();
            Snapshot.RolloutHeadingVariance = _rolloutHeadings.Average(h =>
            {
                var d = NormalizeHeading(h - mean);
                return d * d;
            });
        }

        if (_touchdownCaptured)
        {
            ComputeTouchdownEventMetrics(finalizing);
            ComputePostTouchdownAlignmentMetrics();
            ComputeRolloutPathIntegralMetrics();
        }
    }

    private void CaptureTouchdown(TelemetrySample sample, LandingTelemetrySample logical, double lateral)
    {
        _touchdownCaptured = true;
        _touchdownTimeSeconds = logical.TimeSeconds;
        Snapshot.GateObservations.MainGearTouchdownTimeSeconds = logical.TimeSeconds;
        Snapshot.ContactMappingDegraded = !logical.MainGearContactsAvailable;
        Snapshot.Touchdown = sample;
        Snapshot.VerticalSpeedAtTouchdownFpm = sample.VerticalSpeedFpm;
        _touchdownVerticalSpeedDegraded = true;
        TryCaptureOfficialTouchdownVelocity(sample, logical.TimeSeconds);
        Snapshot.AirspeedAtTouchdownKts = sample.AirspeedKts;
        Snapshot.BankAtTouchdownDeg = sample.BankDeg;
        Snapshot.PitchAtTouchdownDeg = sample.PitchDeg;
        Snapshot.FlapsIndexAtTouchdown = sample.FlapsHandleIndex;
        Snapshot.GearDownAtTouchdown = sample.GearHandlePosition > 0.5;
        Snapshot.TouchdownLateralOffsetM = lateral;
        Snapshot.TouchdownHeadingErrorDeg = NormalizeHeading(sample.HeadingTrueDeg - _challenge.Runway.HeadingTrueDeg);
        Snapshot.PeakGForce = Math.Max(Snapshot.PeakGForce, sample.GForce);

        var (vapp, targetTd, source) = SpeedTargetCalculator.Resolve(_challenge, _settings, sample);
        Snapshot.VappKts = vapp;
        Snapshot.TargetTouchdownIasKts = targetTd;
        Snapshot.TouchdownIasErrorKts = sample.AirspeedKts - targetTd;
        Snapshot.ExcessSpeedOverVappKts = Math.Max(0, sample.AirspeedKts - vapp);
        Snapshot.SpeedTargetSource = source;

        // The touchdown frame itself is inside the inclusive spoiler window.
        UpdateSpoilerDeploymentGate(sample, logical.TimeSeconds);
        UpdateReverseThrustGate(
            sample,
            logical.TimeSeconds,
            anyMainOnGround: true,
            acceptedTouchdownSample: true);
        UpdateNoseGearImpactObservation(logical, logical.TimeSeconds);
    }

    private void UpdateOperationalGates(
        TelemetrySample sample,
        LandingTelemetrySample logical,
        double timeSeconds,
        bool anyMainOnGround)
    {
        var gates = _settings.OperationalGates;
        if (!gates.Enabled)
            return;

        var observations = Snapshot.GateObservations;
        if (!observations.MonitoringStarted)
        {
            var unpaused = gates.PauseUsage is null
                           || sample.PauseStateAvailable
                           && !sample.NormalPauseActive
                           && !sample.ActivePauseActive;
            if (!unpaused)
                return;

            observations.MonitoringStarted = true;
            observations.MonitoringStartTimeSeconds = timeSeconds;
            observations.MonitoringStartPauseGeneration = sample.PauseStateAvailable
                ? sample.PauseGeneration
                : null;
        }

        UpdateManualBrakingGate(sample, logical, timeSeconds);
        UpdateReverseThrustGate(sample, timeSeconds, anyMainOnGround);

        // General pause/rate/cockpit monitoring and the approach automation gate stop at accepted main touchdown.
        if (!_touchdownCaptured)
        {
            UpdatePauseGate(sample);
            UpdateSimulationRateGate(sample);
            UpdateCockpitViewGate(sample);
            UpdateAutomationGate(sample);
        }

        if (_touchdownCaptured)
        {
            UpdateSpoilerDeploymentGate(sample, timeSeconds);
            UpdateNoseGearImpactObservation(logical, timeSeconds);
            UpdateRolloutDistanceGate(sample);
        }
    }

    private void UpdateReverseThrustGate(
        TelemetrySample sample,
        double timeSeconds,
        bool anyMainOnGround,
        bool acceptedTouchdownSample = false)
    {
        var gate = _settings.OperationalGates.ReverseThrust;
        if (gate is null || !ShouldMonitorGate(FreeFlightGateIds.ReverseThrust))
            return;

        var observations = Snapshot.GateObservations;
        var hasCoverage = TryReadReverseThrustTelemetry(sample, out var engineCount);
        if (!_touchdownCaptured)
        {
            if (!hasCoverage || anyMainOnGround)
                return;

            observations.ReverseThrustTelemetryCoverageAvailable = true;
            if (Enumerable.Range(1, engineCount).Any(index => IsReverseSelected(sample, index, gate)))
            {
                observations.AirborneReverseViolation = true;
                observations.FirstAirborneReverseTimeSeconds ??= timeSeconds;
            }
            return;
        }

        if (acceptedTouchdownSample)
        {
            observations.ReverseThrustTelemetryCoverageAvailable = hasCoverage;
            if (hasCoverage)
            {
                observations.OperatingEnginesCapturedAtTouchdown = true;
                observations.EngineCountAtTouchdown = engineCount;
                observations.OperatingEngineIndicesAtTouchdown = Enumerable.Range(1, engineCount)
                    .Where(index => sample.EngineCombustionByIndex![index])
                    .ToList();
            }
        }
        else if (!hasCoverage)
            observations.ReverseThrustTelemetryCoverageAvailable = false;

        if (!acceptedTouchdownSample
            && hasCoverage
            && observations.EngineCountAtTouchdown is { } touchdownEngineCount
            && engineCount != touchdownEngineCount)
        {
            observations.ReverseThrustTelemetryCoverageAvailable = false;
            hasCoverage = false;
        }

        if (hasCoverage)
        {
            foreach (var engineIndex in Enumerable.Range(1, engineCount))
            {
                if (IsReverseSelected(sample, engineIndex, gate))
                    observations.FirstReverseSelectionTimeSecondsByEngine.TryAdd(engineIndex, timeSeconds);

                var throttle = sample.ThrottleLeverPositionPercentByIndex![engineIndex];
                if (throttle < gate.PoweredReverseThrottleThresholdPercent)
                {
                    observations.PoweredReverseViolation = true;
                    if (observations.FirstPoweredReverseTimeSeconds is null)
                    {
                        observations.FirstPoweredReverseTimeSeconds = timeSeconds;
                        observations.FirstPoweredReverseThrottlePercent = throttle;
                    }
                }
            }
        }

        if (!double.IsFinite(sample.GroundSpeedKts)
            || sample.GroundSpeedKts > gate.StowGroundSpeedKts + 1e-9)
            return;

        var firstStowSample = !observations.ReverseThrustStowEvaluated;
        if (firstStowSample)
        {
            observations.ReverseThrustStowEvaluated = true;
            observations.GroundSpeedKtsAtReverseStowCheck = sample.GroundSpeedKts;

            if (gate.Policy.Equals(ReverseThrustPolicies.Required, StringComparison.OrdinalIgnoreCase)
                && timeSeconds + 1e-9 < _touchdownTimeSeconds + gate.DeadlineSecondsAfterTouchdown
                && !AllOperatingEnginesSelected(observations))
                observations.ReverseApplicationWaivedByLowSpeed = true;
        }

        if (!hasCoverage)
        {
            observations.ReverseThrustStowCoverageAvailable = false;
            return;
        }
        if (!firstStowSample && !observations.ReverseThrustStowCoverageAvailable)
            return;

        if (firstStowSample)
            observations.ReverseThrustStowCoverageAvailable = true;
        var notStowed = Enumerable.Range(1, engineCount)
            .Where(index => !IsReverseStowed(sample, index, gate))
            .ToList();
        if (notStowed.Count == 0)
        {
            if (firstStowSample)
                observations.ReverseThrustStowedAtThreshold = true;
            return;
        }

        observations.ReverseThrustStowedAtThreshold = false;
        observations.EnginesNotStowedAtThreshold = observations.EnginesNotStowedAtThreshold
            .Concat(notStowed)
            .Distinct()
            .Order()
            .ToList();
    }

    private static bool TryReadReverseThrustTelemetry(TelemetrySample sample, out int engineCount)
    {
        engineCount = sample.EngineCount ?? 0;
        if (engineCount is < 1 or > 4
            || sample.EngineCombustionByIndex is null
            || sample.ReverseThrustEngagedByIndex is null
            || sample.ReverseNozzlePositionByIndex is null
            || sample.ThrottleLeverPositionPercentByIndex is null)
            return false;

        for (var index = 1; index <= engineCount; index++)
        {
            if (!sample.EngineCombustionByIndex.ContainsKey(index)
                || !sample.ReverseThrustEngagedByIndex.ContainsKey(index)
                || !sample.ReverseNozzlePositionByIndex.TryGetValue(index, out var nozzle)
                || !double.IsFinite(nozzle)
                || !sample.ThrottleLeverPositionPercentByIndex.TryGetValue(index, out var throttle)
                || !double.IsFinite(throttle))
                return false;
        }
        return true;
    }

    private static bool IsReverseSelected(
        TelemetrySample sample,
        int engineIndex,
        ReverseThrustGateConfig gate) =>
        sample.ReverseThrustEngagedByIndex![engineIndex]
        || sample.ReverseNozzlePositionByIndex![engineIndex] >= gate.MinimumNozzlePosition
        || sample.ThrottleLeverPositionPercentByIndex![engineIndex]
        < gate.PoweredReverseThrottleThresholdPercent;

    private static bool IsReverseStowed(
        TelemetrySample sample,
        int engineIndex,
        ReverseThrustGateConfig gate) =>
        !sample.ReverseThrustEngagedByIndex![engineIndex]
        && sample.ReverseNozzlePositionByIndex![engineIndex] < gate.MinimumNozzlePosition
        && sample.ThrottleLeverPositionPercentByIndex![engineIndex]
        >= gate.PoweredReverseThrottleThresholdPercent;

    private static bool AllOperatingEnginesSelected(LandingGateObservations observations) =>
        observations.OperatingEnginesCapturedAtTouchdown
        && observations.OperatingEngineIndicesAtTouchdown.All(
            observations.FirstReverseSelectionTimeSecondsByEngine.ContainsKey);

    private void UpdateRolloutDistanceGate(TelemetrySample sample)
    {
        var gate = _settings.OperationalGates.Rollout;
        if (gate is null || !ShouldMonitorGate(FreeFlightGateIds.RolloutDistance) || !_touchdownCaptured)
            return;

        var observations = Snapshot.GateObservations;
        if (observations.RolloutDistanceEvaluated)
            return;
        if (!sample.SimOnGround)
            return;
        if (!double.IsFinite(sample.GroundSpeedKts)
            || sample.GroundSpeedKts >= _settings.SettledGroundSpeedKts)
            return;

        var length = _challenge.Runway.LengthM;
        if (!double.IsFinite(length) || length <= 0)
            return;

        if (!double.IsFinite(sample.Latitude) || !double.IsFinite(sample.Longitude))
            return;

        var remaining = _geo.RemainingRunwayMeters(sample.Latitude, sample.Longitude);
        var required = RolloutGateConfig.RequiredRemainingAt50Knots(length);

        observations.RolloutDistanceEvaluated = true;
        observations.GroundSpeedKtsAtRolloutCheck = sample.GroundSpeedKts;
        observations.RunwayLengthMeters = length;
        observations.RemainingRunwayMetersAtSettleSpeed = remaining;
        observations.RequiredRemainingRunwayMeters = required;
        observations.RolloutEndOfRunwayViolation = remaining + 1e-9 < required;
    }

    private void UpdatePauseGate(TelemetrySample sample)
    {
        if (_settings.OperationalGates.PauseUsage is null)
            return;

        var observations = Snapshot.GateObservations;
        if (!sample.PauseStateAvailable)
            return;

        observations.PauseCoverageAvailable = true;
        if (sample.NormalPauseActive || sample.ActivePauseActive
            || observations.MonitoringStartPauseGeneration is { } baseline
            && sample.PauseGeneration > baseline)
            observations.PauseViolation = true;
    }

    private void UpdateSimulationRateGate(TelemetrySample sample)
    {
        var gate = _settings.OperationalGates.SimulationRate;
        if (gate is null || sample.SimulationRate is not { } rate || !double.IsFinite(rate))
            return;

        var observations = Snapshot.GateObservations;
        observations.SimulationRateCoverageAvailable = true;
        observations.MinimumSimulationRate = observations.MinimumSimulationRate is { } current
            ? Math.Min(current, rate)
            : rate;
        if (rate < gate.MinimumAllowedRate)
            observations.ReducedSimulationRateViolation = true;
    }

    private void UpdateCockpitViewGate(TelemetrySample sample)
    {
        if (_settings.OperationalGates.CockpitView is null
            || sample.CameraState is not { } cameraState)
            return;

        var observations = Snapshot.GateObservations;
        observations.CameraStateCoverageAvailable = true;
        observations.LastCameraState = cameraState;

        var inCockpit = CameraStates.IsCockpit(cameraState);
        if (_previousCameraInCockpit == true && !inCockpit)
            observations.CockpitViewExitCount++;
        _previousCameraInCockpit = inCockpit;
    }

    private void UpdateAutomationGate(TelemetrySample sample)
    {
        var gate = _settings.OperationalGates.Automation;
        if (gate is null || !ShouldMonitorGate(FreeFlightGateIds.Automation)
            || !sample.RadioHeightAvailable || !double.IsFinite(sample.RadioHeightFeet))
            return;

        var observations = Snapshot.GateObservations;
        observations.RadioHeightCoverageAvailable = true;
        var radioHeight = Math.Max(0, sample.RadioHeightFeet);

        if (radioHeight <= gate.HeadingAltitudeOffRadioHeightFeet
            && sample.AutopilotHeadingHoldActive is { } heading
            && sample.AutopilotAltitudeHoldActive is { } altitude)
        {
            observations.HeadingAltitudeAutomationCoverageAvailable = true;
            observations.HeadingAltitudeThresholdObserved = true;
            if (heading || altitude)
            {
                var state = heading && altitude
                    ? "heading hold and altitude hold"
                    : heading ? "heading hold" : "altitude hold";
                LatchAutomationViolation(state, radioHeight);
            }
        }

        if (radioHeight <= gate.AllAutomationOffRadioHeightFeet
            && sample.AutopilotMasterActive is { } master
            && sample.AutopilotChannel1Active is { } ap1
            && sample.AutopilotChannel2Active is { } ap2
            && sample.AutothrustActive is { } autothrustActive
            && sample.AutothrustArmed is { } autothrustArmed)
        {
            observations.FullAutomationCoverageAvailable = true;
            observations.FullAutomationThresholdObserved = true;
            var active = new List<string>(5);
            if (master) active.Add("AP master");
            if (ap1) active.Add("AP1");
            if (ap2) active.Add("AP2");
            if (autothrustActive) active.Add("autothrust active");
            if (autothrustArmed) active.Add("autothrust armed");
            if (active.Count > 0)
                LatchAutomationViolation(string.Join(", ", active), radioHeight);
        }
    }

    private void LatchAutomationViolation(string state, double radioHeight)
    {
        var observations = Snapshot.GateObservations;
        observations.AutomationViolation = true;
        if (observations.FirstAutomationViolation is not null)
            return;
        observations.FirstAutomationViolation = state;
        observations.FirstAutomationViolationRadioHeightFeet = radioHeight;
    }

    private void UpdateManualBrakingGate(
        TelemetrySample sample,
        LandingTelemetrySample logical,
        double timeSeconds)
    {
        var gate = _settings.OperationalGates.ManualBraking;
        if (gate is null || !ShouldMonitorGate(FreeFlightGateIds.ManualBraking))
            return;

        var observations = Snapshot.GateObservations;
        observations.NoseGearContactCoverageAvailable |= logical.NoseGearContactAvailable;

        if (logical.NoseGearContactAvailable
            && logical.NoseOnGround
            && !_wasNoseOnGround
            && observations.NoseGearTouchdownTimeSeconds is null)
            observations.NoseGearTouchdownTimeSeconds = timeSeconds;

        // Pedal path: both toe brakes after nose TD; pedals must stay released while nose airborne.
        if (sample.ManualBrakeLeftPosition is { } left
            && double.IsFinite(left)
            && sample.ManualBrakeRightPosition is { } right
            && double.IsFinite(right))
        {
            observations.ManualBrakeTelemetryCoverageAvailable = true;
            var leftPressed = left > gate.PedalPressThreshold;
            var rightPressed = right > gate.PedalPressThreshold;

            if ((leftPressed || rightPressed)
                && logical.NoseGearContactAvailable
                && !logical.NoseOnGround)
                observations.EarlyOrAirborneBrakeViolation = true;

            if (leftPressed && rightPressed
                && logical.NoseOnGround
                && observations.NoseGearTouchdownTimeSeconds is not null
                && observations.FirstSimultaneousBrakingTimeSeconds is null)
                observations.FirstSimultaneousBrakingTimeSeconds = timeSeconds;
        }

        // Autobrake path: active after verified nose-gear touchdown also satisfies the gate.
        // Autobrake pressure while the nose is still airborne is normal and is not a violation.
        if (sample.AutoBrakesActive is { } autoActive)
        {
            observations.AutoBrakeTelemetryCoverageAvailable = true;
            if (autoActive
                && observations.NoseGearTouchdownTimeSeconds is not null
                && observations.FirstAutoBrakeActiveTimeSeconds is null)
                observations.FirstAutoBrakeActiveTimeSeconds = timeSeconds;
        }
    }

    private void UpdateSpoilerDeploymentGate(TelemetrySample sample, double timeSeconds)
    {
        var gate = _settings.OperationalGates.SpoilerDeployment;
        if (gate is null || !ShouldMonitorGate(FreeFlightGateIds.Spoilers) || !_touchdownCaptured)
            return;

        var observations = Snapshot.GateObservations;
        if (sample.SpoilersLeftPosition is not { } left
            || !double.IsFinite(left)
            || sample.SpoilersRightPosition is not { } right
            || !double.IsFinite(right))
            return;

        observations.SpoilerTelemetryCoverageAvailable = true;
        if (left >= gate.MinimumSurfacePosition
            && right >= gate.MinimumSurfacePosition
            && observations.FirstSpoilerDeploymentTimeSeconds is null)
            observations.FirstSpoilerDeploymentTimeSeconds = timeSeconds;
    }

    private void UpdateNoseGearImpactObservation(
        LandingTelemetrySample logical,
        double timeSeconds)
    {
        var gate = _settings.OperationalGates.NoseGearImpact;
        if (gate is null || !ShouldMonitorGate(FreeFlightGateIds.NoseGearImpact) || !_touchdownCaptured)
            return;

        var observations = Snapshot.GateObservations;
        observations.NoseGearContactCoverageAvailable |= logical.NoseGearContactAvailable;
        if (!logical.NoseGearContactAvailable)
            return;

        if (!logical.NoseOnGround)
        {
            if (_wasNoseOnGround || _noseImpactAirborneSince is null)
                _noseImpactAirborneSince = timeSeconds;
            return;
        }

        if (_wasNoseOnGround)
        {
            _noseImpactAirborneSince = null;
            return;
        }

        var initial = observations.LastNoseGearImpactContactTimeSeconds is null;
        var airborneLongEnough = _noseImpactAirborneSince is { } airborneSince
                                 && timeSeconds - airborneSince + 1e-9
                                 >= gate.RecontactDebounceSeconds;
        if (initial || airborneLongEnough)
        {
            observations.NoseGearTouchdownTimeSeconds ??= timeSeconds;
            observations.LastNoseGearImpactContactTimeSeconds = timeSeconds;
        }
        _noseImpactAirborneSince = null;
    }

    private bool OperationalGateWindowsComplete(double timeSeconds)
    {
        var gates = _settings.OperationalGates;
        if (!gates.Enabled || !_touchdownCaptured)
            return true;

        var observations = Snapshot.GateObservations;
        if (ShouldMonitorGate(FreeFlightGateIds.Spoilers)
            && gates.SpoilerDeployment is { } spoiler
            && timeSeconds < _touchdownTimeSeconds + spoiler.DeadlineSecondsAfterTouchdown)
            return false;

        if (ShouldMonitorGate(FreeFlightGateIds.ManualBraking)
            && gates.ManualBraking is { } brakes)
        {
            var brakeWindowStart = observations.NoseGearTouchdownTimeSeconds ?? _touchdownTimeSeconds;
            if (timeSeconds < brakeWindowStart + brakes.DeadlineSecondsAfterNoseTouchdown)
                return false;
        }

        if (ShouldMonitorGate(FreeFlightGateIds.NoseGearImpact)
            && gates.NoseGearImpact is { } noseImpact)
        {
            var impactWindowStart = observations.LastNoseGearImpactContactTimeSeconds
                                    ?? observations.NoseGearTouchdownTimeSeconds
                                    ?? _touchdownTimeSeconds;
            if (timeSeconds < impactWindowStart + noseImpact.PostContactWindowSeconds)
                return false;
        }

        if (ShouldMonitorGate(FreeFlightGateIds.ReverseThrust)
            && gates.ReverseThrust is { } reverse
            && reverse.Policy.Equals(ReverseThrustPolicies.Required, StringComparison.OrdinalIgnoreCase)
            && !observations.ReverseApplicationWaivedByLowSpeed
            && !AllOperatingEnginesSelected(observations)
            && timeSeconds < _touchdownTimeSeconds + reverse.DeadlineSecondsAfterTouchdown)
            return false;

        return true;
    }

    private void FinalizeSnapshot() => RefreshDerivedMetrics(finalizing: true);

    private void ComputeTouchdownEventMetrics(bool finalizing)
    {
        if (!double.IsFinite(_touchdownTimeSeconds)) return;
        var events = Snapshot.LandingEventSamples;
        var bounceIntervals = TouchdownAnalysisCalculator.DetectBounceIntervals(
            events,
            _touchdownTimeSeconds,
            _touchdownTimeSeconds + _settings.BounceWindowSeconds,
            _settings.BounceMinAirborneSeconds);
        var firstBounceStart = bounceIntervals.Count == 0 ? (double?)null : bounceIntervals[0].Start;
        var latest = events.Count == 0 ? _touchdownTimeSeconds : events[^1].TimeSeconds;
        var impactComplete = finalizing
                             || latest >= _touchdownTimeSeconds + _settings.ImpactWindowSeconds
                             || firstBounceStart is not null;
        if (impactComplete)
        {
            Snapshot.InitialImpact = TouchdownAnalysisCalculator.AnalyzeImpact(
                events,
                _touchdownTimeSeconds,
                Snapshot.VerticalSpeedAtTouchdownFpm,
                _touchdownVerticalSpeedSource,
                _settings,
                firstBounceStart,
                _touchdownVerticalSpeedDegraded || Snapshot.ContactMappingDegraded,
                _touchdownVerticalSpeedDegraded
                    ? "Official touchdown-normal velocity was unavailable; instantaneous vertical speed was used."
                    : Snapshot.ContactMappingDegraded
                        ? "Independent main-gear contact mapping was unavailable."
                        : null);
        }

        Snapshot.FloatAnalysis ??= TouchdownAnalysisCalculator.AnalyzeFloat(
            events,
            _touchdownTimeSeconds,
            _settings.FlareAglFeet,
            _settings.FlareEntryVerticalSpeedFpm,
            _settings.FlareMinSustainSeconds);

        var bounceComplete = finalizing || latest >= _touchdownTimeSeconds + _settings.BounceWindowSeconds;
        if (bounceComplete)
        {
            Snapshot.ContactStability = TouchdownAnalysisCalculator.AnalyzeContactStability(
                events, _touchdownTimeSeconds, _settings, windowComplete: true);
            Snapshot.TouchdownAnalysisComplete = true;
        }

        if (ShouldMonitorGate(FreeFlightGateIds.NoseGearImpact)
            && _settings.OperationalGates.NoseGearImpact is { } noseImpact)
            Snapshot.GateObservations.NoseGearImpact = NoseGearImpactCalculator.Analyze(
                events, _touchdownTimeSeconds, noseImpact);
    }

    private bool ShouldMonitorGate(string gateId)
    {
        if (_challenge.ModeEnum != ChallengeMode.FreeFlight)
            return true;
        return FreeFlightCapabilityResolver.ResolveDecision(
                _challenge.FreeFlightCapabilities, gateId).Applicability
            != FreeFlightGateApplicability.NotApplicable;
    }

    private void TryCaptureOfficialTouchdownVelocity(TelemetrySample sample, double timeSeconds)
    {
        if (!_touchdownCaptured && !double.IsFinite(_touchdownTimeSeconds)) return;
        if (!_touchdownVerticalSpeedDegraded) return;
        if (timeSeconds > _touchdownTimeSeconds + Math.Min(0.25, _settings.ImpactWindowSeconds)) return;
        if (sample.TouchdownNormalVelocityFps is not { } value || !double.IsFinite(value)
            || Math.Abs(value) < 0.001)
            return;
        if (_preTouchdownNormalVelocityFps is { } previous && Math.Abs(value - previous) < 0.0001)
            return;

        Snapshot.VerticalSpeedAtTouchdownFpm = -Math.Abs(value * 60.0);
        _touchdownVerticalSpeedSource = "PLANE TOUCHDOWN NORMAL VELOCITY";
        _touchdownVerticalSpeedDegraded = false;
    }

    private double ResolveTimeSeconds(TelemetrySample sample)
    {
        var candidate = double.IsFinite(sample.SimulationTimeSeconds)
            ? sample.SimulationTimeSeconds
            : sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0;
        if (!double.IsFinite(_armedTimeSeconds))
        {
            var wallSinceArm = Math.Max(0, (sample.Timestamp - _armedAt).TotalSeconds);
            _armedTimeSeconds = candidate - wallSinceArm;
        }
        if (double.IsFinite(_lastTimeSeconds) && candidate < _lastTimeSeconds)
            candidate = _lastTimeSeconds;
        _lastTimeSeconds = candidate;
        return candidate;
    }

    private LandingTelemetrySample ToLandingSample(TelemetrySample sample, double timeSeconds)
    {
        var mapping = _settings.ContactMapping;
        var contacts = sample.GearOnGroundByIndex;
        var conventional = sample.IsGearWheels && !sample.IsTailDragger;
        var aircraftTypeUnknown = !sample.IsGearWheels && !sample.IsGearFloats && !sample.IsTailDragger;
        var available = contacts is not null
                        && contacts.ContainsKey(mapping.LeftMainGearIndex)
                        && contacts.ContainsKey(mapping.RightMainGearIndex)
                        && (conventional || aircraftTypeUnknown || mapping.IsAircraftSpecific);
        var left = available && contacts![mapping.LeftMainGearIndex];
        var right = available && contacts![mapping.RightMainGearIndex];
        var noseAvailable = available && contacts!.ContainsKey(mapping.NoseGearIndex);
        var nose = noseAvailable && contacts![mapping.NoseGearIndex];
        var agl = sample.RadioHeightFeet > 0 ? sample.RadioHeightFeet : sample.AglFeet;
        return new LandingTelemetrySample(
            timeSeconds, agl, sample.GroundSpeedKts, sample.VerticalSpeedFpm, sample.GForce,
            left, right, nose, available, noseAvailable,
            sample.GForceAvailable,
            sample.ContactPointOnGroundByIndex,
            sample.ContactPointCompressionByIndex,
            sample.ContactPointTelemetryAvailable);
    }

    /// <summary>
    /// Short-final approach metrics (same distance window as config):
    /// 1) time-weighted mean absolute altitude error vs the nominal glideslope path,
    /// 2) total vertical path variation per second,
    /// 3) total lateral path variation per metre flown,
    /// 4) time-weighted mean absolute bank.
    /// The configured inner distance boundary ends collection before the flare.
    /// Raw visual-frame telemetry is stabilized by <see cref="ApproachMetricCalculator"/>.
    /// </summary>
    private void ComputeApproachPathMetrics()
    {
        var result = ApproachMetricCalculator.Calculate(
            Snapshot.ApproachSamples,
            _challenge.Runway,
            _settings.ApproachPathMinDistNm,
            _settings.ApproachPathMaxDistNm);

        // Always assign every field so live preview cannot retain stale values.
        Snapshot.ApproachPathSampleCount = result.RawSampleCount;
        Snapshot.ApproachPathRms = result.RootMeanSquareErrorFeet;
        Snapshot.ApproachGlideslopeMeanAbsFt = result.MeanAbsoluteErrorFeet;
        Snapshot.ApproachVerticalVariationFtPerSec =
            result.VerticalExcessVariationFeetPerSecond;
        Snapshot.ApproachLateralWeaveIndex = result.LateralExcessVariationIndex;
        Snapshot.ApproachBankMeanAbsDeg = result.MeanAbsoluteBankDeg;
        Snapshot.ApproachLateralDistanceM = result.GroundDistanceMeters;
        Snapshot.ApproachMetricDurationSec = result.DurationSeconds;
    }

    /// <summary>
    /// From TD+delay until end of rollout history: fuselage should be aligned with runway
    /// (de-crabbed, rudder tracking). Measures heading error, not wind crab on final.
    /// </summary>
    private void ComputePostTouchdownAlignmentMetrics()
    {
        if (Snapshot.Touchdown is null || Snapshot.RolloutSamples.Count == 0)
            return;

        var td = _touchdownTimeSeconds;
        var delay = _settings.PostTouchdownAlignmentDelaySeconds;
        var settleKts = _settings.SettledGroundSpeedKts;
        var runway = _challenge.Runway.HeadingTrueDeg;

        // Samples from TD+2s while still above settle speed (or all after TD+2s on ground)
        var samples = Snapshot.RolloutSamples
            .Where(s => s.SimOnGround && SampleTimeSeconds(s) >= td + delay)
            .OrderBy(SampleTimeSeconds)
            .ToList();

        // Prefer samples until GS first reaches settle threshold (if it does)
        var untilSettle = new List<TelemetrySample>();
        foreach (var s in samples)
        {
            untilSettle.Add(s);
            if (s.GroundSpeedKts < settleKts)
                break;
        }

        if (untilSettle.Count < 2)
            return;

        var errors = untilSettle
            .Select(s => Math.Abs(NormalizeHeading(s.HeadingTrueDeg - runway)))
            .ToList();

        Snapshot.PostTouchdownAlignmentSampleCount = errors.Count;
        Snapshot.PostTouchdownAlignmentMeanDeg = errors.Average();
        Snapshot.PostTouchdownAlignmentRmsDeg = Math.Sqrt(errors.Average(e => e * e));
        Snapshot.PostTouchdownAlignmentPeakDeg = errors.Max();
    }

    /// <summary>
    /// After touchdown: integrate |centerline offset| over distance traveled and measure weave.
    /// Steady rundown ≫ left/right wandering for the same mean offset.
    /// </summary>
    private void ComputeRolloutPathIntegralMetrics()
    {
        if (Snapshot.Touchdown is null || Snapshot.RolloutSamples.Count < 2)
            return;

        var td = _touchdownTimeSeconds;
        var onGround = Snapshot.RolloutSamples
            .Where(s => s.SimOnGround && SampleTimeSeconds(s) >= td)
            .OrderBy(SampleTimeSeconds)
            .ToList();

        if (onGround.Count < 2)
            return;

        double integralAbsD = 0; // ∫|d| ds  (m²)
        double alongTrack = 0;   // S (m)
        double totalVariation = 0; // Σ|Δd| (m)
        double peak = 0;
        var movementSegments = 0;
        var prevD = _geo.LateralOffsetMeters(onGround[0].Latitude, onGround[0].Longitude);
        peak = Math.Abs(prevD);

        for (var i = 1; i < onGround.Count; i++)
        {
            var prev = onGround[i - 1];
            var cur = onGround[i];
            var ds = GeoUtil.HaversineMetersPublic(prev.Latitude, prev.Longitude, cur.Latitude, cur.Longitude);
            if (ds < 0.05)
                continue;

            movementSegments++;

            var d = _geo.LateralOffsetMeters(cur.Latitude, cur.Longitude);
            var absPrev = Math.Abs(prevD);
            var absCur = Math.Abs(d);
            // Trapezoid rule for ∫|d| ds
            integralAbsD += 0.5 * (absPrev + absCur) * ds;
            alongTrack += ds;
            totalVariation += Math.Abs(d - prevD);
            peak = Math.Max(peak, absCur);
            prevD = d;
        }

        Snapshot.RolloutPathSampleCount = onGround.Count;
        Snapshot.RolloutPathSegmentCount = movementSegments;
        Snapshot.RolloutDistanceM = alongTrack;
        if (alongTrack < 1.0)
            return;

        Snapshot.RolloutLateralMeanM = integralAbsD / alongTrack;
        Snapshot.RolloutLateralPeakM = peak;
        Snapshot.RolloutWeaveIndex = totalVariation / alongTrack;
        // Keep max lateral for whole landing at least as large as rollout peak
        Snapshot.MaxLateralOffsetM = Math.Max(Snapshot.MaxLateralOffsetM, peak);
    }

    private void SetPhase(LandingPhase phase)
    {
        if (Phase == phase) return;
        Phase = phase;
        PhaseChanged?.Invoke(this, phase);
    }

    private static double NormalizeHeading(double deg)
    {
        while (deg > 180) deg -= 360;
        while (deg < -180) deg += 360;
        return deg;
    }

    private static double SampleTimeSeconds(TelemetrySample sample) =>
        double.IsFinite(sample.SimulationTimeSeconds)
            ? sample.SimulationTimeSeconds
            : sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0;
}

public sealed class GeoUtil
{
    private readonly RunwayConfig _runway;

    public GeoUtil(RunwayConfig runway) => _runway = runway;

    public double DistanceToThresholdNm(double lat, double lon)
    {
        var m = HaversineMeters(lat, lon, _runway.ThresholdLatitude, _runway.ThresholdLongitude);
        return m / 1852.0;
    }

    /// <summary>Positive = right of centerline when facing runway heading.</summary>
    public double LateralOffsetMeters(double lat, double lon)
    {
        var (northM, eastM) = LocalMeters(lat, lon, _runway.ThresholdLatitude, _runway.ThresholdLongitude);
        var h = _runway.HeadingTrueDeg * Math.PI / 180.0;
        return eastM * Math.Cos(h) - northM * Math.Sin(h);
    }

    /// <summary>
    /// Along-track distance from the landing threshold in the runway-heading direction (metres).
    /// Positive = past the threshold toward the far end.
    /// </summary>
    public double DistanceAlongRunwayMeters(double lat, double lon)
    {
        var (northM, eastM) = LocalMeters(lat, lon, _runway.ThresholdLatitude, _runway.ThresholdLongitude);
        var h = _runway.HeadingTrueDeg * Math.PI / 180.0;
        return northM * Math.Cos(h) + eastM * Math.Sin(h);
    }

    /// <summary>Remaining paved distance to the far end of the runway (metres).</summary>
    public double RemainingRunwayMeters(double lat, double lon)
        => _runway.LengthM - DistanceAlongRunwayMeters(lat, lon);

    public static double BearingDegrees(double lat1, double lon1, double lat2, double lon2)
    {
        var φ1 = lat1 * Math.PI / 180.0;
        var φ2 = lat2 * Math.PI / 180.0;
        var Δλ = (lon2 - lon1) * Math.PI / 180.0;
        var y = Math.Sin(Δλ) * Math.Cos(φ2);
        var x = Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ);
        var θ = Math.Atan2(y, x) * 180.0 / Math.PI;
        return (θ + 360.0) % 360.0;
    }

    public static double HaversineMetersPublic(double lat1, double lon1, double lat2, double lon2)
        => HaversineMeters(lat1, lon1, lat2, lon2);

    private static (double northM, double eastM) LocalMeters(double lat, double lon, double refLat, double refLon)
    {
        const double r = 6371000.0;
        var dLat = (lat - refLat) * Math.PI / 180.0;
        var dLon = (lon - refLon) * Math.PI / 180.0;
        var north = dLat * r;
        var east = dLon * r * Math.Cos(refLat * Math.PI / 180.0);
        return (north, east);
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371000.0;
        var p1 = lat1 * Math.PI / 180.0;
        var p2 = lat2 * Math.PI / 180.0;
        var dp = (lat2 - lat1) * Math.PI / 180.0;
        var dl = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) +
                Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }
}
