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
        Snapshot.ApproachSamples.Clear();
        Snapshot.RolloutSamples.Clear();
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
                // Simple path quality: deviation from 3° glideslope altitude vs distance to threshold
                var distNm = _geo.DistanceToThresholdNm(sample.Latitude, sample.Longitude);
                var expectedAlt = _challenge.Runway.ElevationFeet + distNm * 318.0; // ~3 deg
                _approachAltErrors.Add(sample.AltitudeFeet - expectedAlt);

                if (agl <= _profile.FlareAglFeet && Phase != LandingPhase.Flare)
                {
                    Snapshot.CrabAngleAtFlareDeg = NormalizeHeading(sample.HeadingTrueDeg - _challenge.Runway.HeadingTrueDeg);
                    SetPhase(LandingPhase.Flare);
                }
            }
        }

        // Touchdown edge
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

            var gsKmh = sample.GroundSpeedKmh;
            if (gsKmh < _profile.SettledGroundSpeedKmh)
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
        // Cross-track: right positive relative to runway course
        return eastM * Math.Cos(h) - northM * Math.Sin(h);
    }

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
