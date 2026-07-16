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
    private bool _touchdownCaptured;
    private int _airborneSampleCount;
    private readonly List<double> _rolloutHeadings = new();
    private double _armedTimeSeconds = double.NaN;
    private double _lastTimeSeconds = double.NaN;
    private double _touchdownTimeSeconds = double.NaN;
    private double? _preTouchdownNormalVelocityFps;
    private bool _touchdownVerticalSpeedDegraded;
    private string _touchdownVerticalSpeedSource = "instantaneous vertical speed";

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
        // Re-open post-arm grace and seed ground so a mid-clean on the runway
        // does not instantly re-fire touchdown on the next frame.
        _armedAt = DateTimeOffset.UtcNow;
        _wasAnyMainOnGround = true;
        SetPhase(LandingPhase.Armed);
    }

    public void Reset()
    {
        Phase = LandingPhase.Idle;
        ClearCapturedMetrics();
        _settledSince = null;
        _armedAt = default;
        _wasAnyMainOnGround = false;
        _touchdownCaptured = false;
        _airborneSampleCount = 0;
        _rolloutHeadings.Clear();
        _armedTimeSeconds = double.NaN;
        _lastTimeSeconds = double.NaN;
        _touchdownTimeSeconds = double.NaN;
        _preTouchdownNormalVelocityFps = null;
        _touchdownVerticalSpeedDegraded = false;
        _touchdownVerticalSpeedSource = "instantaneous vertical speed";
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
        Snapshot.ApproachLateralDistanceM = 0;
        Snapshot.ApproachMetricDurationSec = 0;
        Snapshot.RolloutHeadingVariance = 0;
        Snapshot.CrabAngleAtFlareDeg = 0;
        Snapshot.GroundTrackErrorMeanDeg = 0;
        Snapshot.GroundTrackErrorRmsDeg = 0;
        Snapshot.GroundTrackErrorPeakDeg = 0;
        Snapshot.GroundTrackSampleCount = 0;
        Snapshot.GroundTrackBeforeSegmentCount = 0;
        Snapshot.GroundTrackAfterSegmentCount = 0;
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
        Snapshot.ApproachSamples.Clear();
        Snapshot.RolloutSamples.Clear();
        Snapshot.LandingEventSamples.Clear();
    }

    public void Ingest(TelemetrySample sample)
    {
        if (Phase is LandingPhase.Idle or LandingPhase.Scored)
            return;

        var timeSeconds = ResolveTimeSeconds(sample);
        Snapshot.StallWarningOccurred |= sample.StallWarningActive;
        var logical = ToLandingSample(sample, timeSeconds);
        Snapshot.LandingEventSamples.Add(logical);
        var anyMainOnGround = logical.MainGearContactsAvailable
            ? logical.LeftMainOnGround || logical.RightMainOnGround
            : sample.SimOnGround;

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
                        FinalizeSnapshot();
                        SetPhase(LandingPhase.Settled);
                        SettledReady?.Invoke(this, EventArgs.Empty);
                        SetPhase(LandingPhase.Scored);
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
            ComputeGroundTrackWindowMetrics();
            ComputePostTouchdownAlignmentMetrics();
            ComputeRolloutPathIntegralMetrics();
        }
    }

    private void CaptureTouchdown(TelemetrySample sample, LandingTelemetrySample logical, double lateral)
    {
        _touchdownCaptured = true;
        _touchdownTimeSeconds = logical.TimeSeconds;
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
        var nose = available && contacts!.TryGetValue(mapping.NoseGearIndex, out var noseValue) && noseValue;
        var agl = sample.RadioHeightFeet > 0 ? sample.RadioHeightFeet : sample.AglFeet;
        return new LandingTelemetrySample(
            timeSeconds, agl, sample.GroundSpeedKts, sample.VerticalSpeedFpm, sample.GForce,
            left, right, nose, available);
    }

    /// <summary>
    /// Short-final approach metrics (same distance window as config):
    /// 1) time-weighted mean absolute altitude error vs the nominal 3° path,
    /// 2) total vertical path variation per second,
    /// 3) total lateral path variation per metre flown.
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

    /// <summary>
    /// Score path of the CG over the ground vs runway centerline course:
    /// samples from (touchdown − before) through (touchdown + after).
    /// Uses ground track (motion direction), not fuselage crab heading.
    /// </summary>
    private void ComputeGroundTrackWindowMetrics()
    {
        if (Snapshot.Touchdown is null)
            return;

        var td = _touchdownTimeSeconds;
        var windowStart = td - _settings.GroundTrackWindowBeforeSeconds;
        var windowEnd = td + _settings.GroundTrackWindowAfterSeconds;

        var window = Snapshot.ApproachSamples
            .Concat(Snapshot.RolloutSamples)
            .Where(s => SampleTimeSeconds(s) >= windowStart && SampleTimeSeconds(s) <= windowEnd)
            .OrderBy(SampleTimeSeconds)
            .ToList();

        if (window.Count < 2)
            return;

        var runway = _challenge.Runway.HeadingTrueDeg;
        var errors = new List<double>();
        var beforeSegments = 0;
        var afterSegments = 0;

        for (var i = 1; i < window.Count; i++)
        {
            var prev = window[i - 1];
            var cur = window[i];
            var track = ResolveGroundTrackDeg(prev, cur);
            if (track is null)
                continue;

            var err = Math.Abs(NormalizeHeading(track.Value - runway));
            // Wrap: 350° error to runway 0 is 10°, already handled by NormalizeHeading abs
            if (err > 90)
                err = 180 - err; // treat reciprocal as same path axis for runway alignment quality
            errors.Add(err);

            var midpoint = 0.5 * (SampleTimeSeconds(prev) + SampleTimeSeconds(cur));
            if (midpoint < td) beforeSegments++;
            else afterSegments++;
        }

        if (errors.Count == 0)
            return;

        Snapshot.GroundTrackSampleCount = errors.Count;
        Snapshot.GroundTrackBeforeSegmentCount = beforeSegments;
        Snapshot.GroundTrackAfterSegmentCount = afterSegments;
        Snapshot.GroundTrackErrorMeanDeg = errors.Average();
        Snapshot.GroundTrackErrorRmsDeg = Math.Sqrt(errors.Average(e => e * e));
        Snapshot.GroundTrackErrorPeakDeg = errors.Max();
    }

    /// <summary>
    /// Prefer reported ground track; else derive from lat/lon motion of the CG.
    /// </summary>
    private static double? ResolveGroundTrackDeg(TelemetrySample prev, TelemetrySample cur)
    {
        // Prefer sim/GPS ground track when the aircraft is clearly moving
        if (cur.GroundSpeedKts >= 8)
            return NormalizeTrack(cur.GroundTrackTrueDeg);

        var dist = GeoUtil.HaversineMetersPublic(prev.Latitude, prev.Longitude, cur.Latitude, cur.Longitude);
        if (dist >= 1.5)
            return GeoUtil.BearingDegrees(prev.Latitude, prev.Longitude, cur.Latitude, cur.Longitude);

        if (cur.GroundSpeedKts >= 5)
            return NormalizeTrack(cur.GroundTrackTrueDeg);

        return null;
    }

    private static double NormalizeTrack(double deg)
    {
        deg %= 360.0;
        if (deg < 0) deg += 360.0;
        return deg;
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
