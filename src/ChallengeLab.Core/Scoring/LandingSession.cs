using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Tracks approach → touchdown → rollout → settled and builds a <see cref="LandingSnapshot"/>.
/// </summary>
public sealed class LandingSession
{
    private readonly ChallengeConfig _challenge;
    private readonly ScoringProfileConfig _profile;
    private readonly GeoUtil _geo;
    private DateTimeOffset? _settledSince;
    private bool _wasOnGround;
    private bool _touchdownCaptured;
    private readonly List<double> _approachAltErrors = new();
    private readonly List<double> _rolloutHeadings = new();
    private readonly List<TelemetrySample> _history = new();

    public LandingPhase Phase { get; private set; } = LandingPhase.Idle;
    public LandingSnapshot Snapshot { get; } = new();
    public bool IsArmed => Phase is not LandingPhase.Idle and not LandingPhase.Scored;
    public bool IsComplete => Phase == LandingPhase.Scored;

    public event EventHandler<LandingPhase>? PhaseChanged;
    public event EventHandler? SettledReady;

    public LandingSession(ChallengeConfig challenge, ScoringProfileConfig profile)
    {
        _challenge = challenge;
        _profile = profile;
        _geo = new GeoUtil(challenge.Runway);
    }

    public void Arm()
    {
        Reset();
        SetPhase(LandingPhase.Armed);
    }

    public void Reset()
    {
        Phase = LandingPhase.Idle;
        Snapshot.Touchdown = null;
        Snapshot.PeakGForce = 1.0;
        Snapshot.PeakAbsBankDeg = 0;
        Snapshot.MaxLateralOffsetM = 0;
        Snapshot.TouchdownLateralOffsetM = 0;
        Snapshot.TouchdownHeadingErrorDeg = 0;
        Snapshot.ApproachPathRms = 0;
        Snapshot.RolloutHeadingVariance = 0;
        Snapshot.CrabAngleAtFlareDeg = 0;
        Snapshot.GroundTrackErrorMeanDeg = 0;
        Snapshot.GroundTrackErrorRmsDeg = 0;
        Snapshot.GroundTrackErrorPeakDeg = 0;
        Snapshot.GroundTrackSampleCount = 0;
        Snapshot.PostTouchdownAlignmentMeanDeg = 0;
        Snapshot.PostTouchdownAlignmentRmsDeg = 0;
        Snapshot.PostTouchdownAlignmentPeakDeg = 0;
        Snapshot.PostTouchdownAlignmentSampleCount = 0;
        Snapshot.RolloutLateralMeanM = 0;
        Snapshot.RolloutLateralPeakM = 0;
        Snapshot.RolloutWeaveIndex = 0;
        Snapshot.RolloutDistanceM = 0;
        Snapshot.RolloutPathSampleCount = 0;
        Snapshot.ApproachSamples.Clear();
        Snapshot.RolloutSamples.Clear();
        _history.Clear();
        _settledSince = null;
        _wasOnGround = false;
        _touchdownCaptured = false;
        _approachAltErrors.Clear();
        _rolloutHeadings.Clear();
    }

    public void Ingest(TelemetrySample sample)
    {
        if (Phase is LandingPhase.Idle or LandingPhase.Scored)
            return;

        _history.Add(sample);
        PruneHistory(sample.Timestamp);

        var lateral = _geo.LateralOffsetMeters(sample.Latitude, sample.Longitude);
        Snapshot.MaxLateralOffsetM = Math.Max(Snapshot.MaxLateralOffsetM, Math.Abs(lateral));
        Snapshot.PeakGForce = Math.Max(Snapshot.PeakGForce, sample.GForce);
        Snapshot.PeakAbsBankDeg = Math.Max(Snapshot.PeakAbsBankDeg, Math.Abs(sample.BankDeg));

        var agl = sample.RadioHeightFeet > 0 ? sample.RadioHeightFeet : sample.AglFeet;

        if (Phase is LandingPhase.Armed or LandingPhase.Approach or LandingPhase.Flare)
        {
            if (!sample.SimOnGround)
            {
                if (Phase == LandingPhase.Armed)
                    SetPhase(LandingPhase.Approach);

                Snapshot.ApproachSamples.Add(sample);
                var distNm = _geo.DistanceToThresholdNm(sample.Latitude, sample.Longitude);
                var expectedAlt = _challenge.Runway.ElevationFeet + distNm * 318.0; // ~3 deg
                _approachAltErrors.Add(sample.AltitudeFeet - expectedAlt);

                if (agl <= _profile.FlareAglFeet && Phase != LandingPhase.Flare)
                {
                    // Kept for diagnostics only — not scored (crab is wind-dependent).
                    Snapshot.CrabAngleAtFlareDeg = NormalizeHeading(sample.HeadingTrueDeg - _challenge.Runway.HeadingTrueDeg);
                    SetPhase(LandingPhase.Flare);
                }
            }
        }

        if (!_touchdownCaptured && sample.SimOnGround && !_wasOnGround)
        {
            CaptureTouchdown(sample, lateral);
            SetPhase(LandingPhase.Touchdown);
            SetPhase(LandingPhase.Rollout);
        }

        if (_touchdownCaptured && sample.SimOnGround)
        {
            Snapshot.RolloutSamples.Add(sample);
            _rolloutHeadings.Add(sample.HeadingTrueDeg);

            if (sample.GroundSpeedKts < _profile.GetSettledGroundSpeedKts())
            {
                _settledSince ??= sample.Timestamp;
                var hold = TimeSpan.FromSeconds(_profile.SettledHoldSeconds);
                if (sample.Timestamp - _settledSince >= hold)
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

        _wasOnGround = sample.SimOnGround;
    }

    private void CaptureTouchdown(TelemetrySample sample, double lateral)
    {
        _touchdownCaptured = true;
        Snapshot.Touchdown = sample;
        Snapshot.VerticalSpeedAtTouchdownFpm = sample.VerticalSpeedFpm;
        Snapshot.AirspeedAtTouchdownKts = sample.AirspeedKts;
        Snapshot.BankAtTouchdownDeg = sample.BankDeg;
        Snapshot.PitchAtTouchdownDeg = sample.PitchDeg;
        Snapshot.FlapsIndexAtTouchdown = sample.FlapsHandleIndex;
        Snapshot.GearDownAtTouchdown = sample.GearHandlePosition > 0.5;
        Snapshot.TouchdownLateralOffsetM = lateral;
        Snapshot.TouchdownHeadingErrorDeg = NormalizeHeading(sample.HeadingTrueDeg - _challenge.Runway.HeadingTrueDeg);
        Snapshot.PeakGForce = Math.Max(Snapshot.PeakGForce, sample.GForce);

        var (vapp, targetTd, source) = SpeedTargetCalculator.Resolve(_challenge, _profile, sample);
        Snapshot.VappKts = vapp;
        Snapshot.TargetTouchdownIasKts = targetTd;
        Snapshot.TouchdownIasErrorKts = sample.AirspeedKts - targetTd;
        Snapshot.ExcessSpeedOverVappKts = Math.Max(0, sample.AirspeedKts - vapp);
        Snapshot.SpeedTargetSource = source;
    }

    private void FinalizeSnapshot()
    {
        if (_approachAltErrors.Count > 0)
        {
            var meanSq = _approachAltErrors.Average(e => e * e);
            Snapshot.ApproachPathRms = Math.Sqrt(meanSq);
        }

        if (_rolloutHeadings.Count > 1)
        {
            var mean = _rolloutHeadings.Average();
            Snapshot.RolloutHeadingVariance = _rolloutHeadings.Average(h =>
            {
                var d = NormalizeHeading(h - mean);
                return d * d;
            });
        }

        ComputeGroundTrackWindowMetrics();
        ComputePostTouchdownAlignmentMetrics();
        ComputeRolloutPathIntegralMetrics();
    }

    /// <summary>
    /// From TD+delay until end of rollout history: fuselage should be aligned with runway
    /// (de-crabbed, rudder tracking). Measures heading error, not wind crab on final.
    /// </summary>
    private void ComputePostTouchdownAlignmentMetrics()
    {
        if (Snapshot.Touchdown is null || _history.Count == 0)
            return;

        var td = Snapshot.Touchdown.Timestamp;
        var delay = TimeSpan.FromSeconds(_profile.PostTouchdownAlignmentDelaySeconds > 0
            ? _profile.PostTouchdownAlignmentDelaySeconds
            : 2.0);
        var settleKts = _profile.GetSettledGroundSpeedKts();
        var runway = _challenge.Runway.HeadingTrueDeg;

        // Samples from TD+2s while still above settle speed (or all after TD+2s on ground)
        var samples = _history
            .Where(s => s.SimOnGround && s.Timestamp >= td + delay)
            .OrderBy(s => s.Timestamp)
            .ToList();

        // Prefer samples until GS first reaches settle threshold (if it does)
        var untilSettle = new List<TelemetrySample>();
        foreach (var s in samples)
        {
            untilSettle.Add(s);
            if (s.GroundSpeedKts < settleKts)
                break;
        }

        if (untilSettle.Count == 0)
            untilSettle = samples;

        if (untilSettle.Count == 0)
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
        if (Snapshot.Touchdown is null || _history.Count < 2)
            return;

        var td = Snapshot.Touchdown.Timestamp;
        var onGround = _history
            .Where(s => s.SimOnGround && s.Timestamp >= td)
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (onGround.Count < 2)
            return;

        double integralAbsD = 0; // ∫|d| ds  (m²)
        double alongTrack = 0;   // S (m)
        double totalVariation = 0; // Σ|Δd| (m)
        double peak = 0;
        var prevD = _geo.LateralOffsetMeters(onGround[0].Latitude, onGround[0].Longitude);
        peak = Math.Abs(prevD);

        for (var i = 1; i < onGround.Count; i++)
        {
            var prev = onGround[i - 1];
            var cur = onGround[i];
            var ds = GeoUtil.HaversineMetersPublic(prev.Latitude, prev.Longitude, cur.Latitude, cur.Longitude);
            if (ds < 0.05)
                continue;

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

        if (alongTrack < 1.0)
            return;

        Snapshot.RolloutDistanceM = alongTrack;
        Snapshot.RolloutLateralMeanM = integralAbsD / alongTrack;
        Snapshot.RolloutLateralPeakM = peak;
        Snapshot.RolloutWeaveIndex = totalVariation / alongTrack;
        Snapshot.RolloutPathSampleCount = onGround.Count;
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
        if (Snapshot.Touchdown is null || _history.Count < 2)
            return;

        var td = Snapshot.Touchdown.Timestamp;
        var before = TimeSpan.FromSeconds(_profile.GroundTrackWindowBeforeSeconds > 0
            ? _profile.GroundTrackWindowBeforeSeconds
            : 3);
        var after = TimeSpan.FromSeconds(_profile.GroundTrackWindowAfterSeconds > 0
            ? _profile.GroundTrackWindowAfterSeconds
            : 3);

        var windowStart = td - before;
        var windowEnd = td + after;

        var window = _history
            .Where(s => s.Timestamp >= windowStart && s.Timestamp <= windowEnd)
            .OrderBy(s => s.Timestamp)
            .ToList();

        if (window.Count < 2)
            return;

        var runway = _challenge.Runway.HeadingTrueDeg;
        var errors = new List<double>();

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
        }

        if (errors.Count == 0)
            return;

        Snapshot.GroundTrackSampleCount = errors.Count;
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

    private void PruneHistory(DateTimeOffset now)
    {
        var keep = TimeSpan.FromSeconds(30);
        while (_history.Count > 0 && now - _history[0].Timestamp > keep)
            _history.RemoveAt(0);
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
