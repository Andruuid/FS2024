using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.ViewModels;

/// <summary>
/// Keeps presentation guidance meaningful through flare and touchdown without changing the
/// side-effect-free landing-monitor calculations used by scoring and offline analysis.
/// </summary>
internal sealed class LandingGuidanceHold
{
    private string? _runwayKey;
    private double? _heldPathAngleDeg;
    private LandingMonitorStatus _heldPathStatus;
    private double? _lastAirborneDescentAngleDeg;
    private LandingMonitorStatus _lastAirborneDescentStatus;
    private bool _pathFrozen;

    public LandingMonitorReading Update(
        TelemetrySample sample,
        RunwayConfig? runway,
        LandingMonitorReading raw,
        double flareAglFeet)
    {
        ArgumentNullException.ThrowIfNull(sample);
        ArgumentNullException.ThrowIfNull(raw);

        if (runway is null)
        {
            Reset();
            return raw;
        }

        var key = RunwayKey(runway);
        if (!string.Equals(_runwayKey, key, StringComparison.Ordinal))
        {
            Reset();
            _runwayKey = key;
        }

        if (!sample.SimOnGround)
        {
            if (!_pathFrozen && raw.GlideslopeDeg is { } pathAngle)
            {
                _heldPathAngleDeg = pathAngle;
                _heldPathStatus = raw.GlideslopeStatus;
            }

            if (raw.DescentAngleDeg is { } descentAngle)
            {
                _lastAirborneDescentAngleDeg = descentAngle;
                _lastAirborneDescentStatus = raw.DescentAngleStatus;
            }

            var agl = sample.RadioHeightFeet > 0
                ? sample.RadioHeightFeet
                : sample.AglFeet;
            if (double.IsFinite(agl)
                && agl <= Math.Max(1, flareAglFeet)
                && _heldPathAngleDeg is not null)
            {
                _pathFrozen = true;
            }
        }

        // The geometric path angle becomes singular at the unflared 1,000 ft aim point.
        // Treat loss of the raw value as entry into the hold instead of target loss.
        if (raw.GlideslopeDeg is null && _heldPathAngleDeg is not null)
            _pathFrozen = true;

        var pathAngleToShow = _pathFrozen
            ? _heldPathAngleDeg
            : raw.GlideslopeDeg;
        var pathStatusToShow = _pathFrozen && _heldPathAngleDeg is not null
            ? _heldPathStatus
            : raw.GlideslopeStatus;
        var descentAngleToShow = raw.DescentAngleDeg ?? _lastAirborneDescentAngleDeg;
        var descentStatusToShow = raw.DescentAngleDeg is not null
            ? raw.DescentAngleStatus
            : _lastAirborneDescentAngleDeg is not null
                ? _lastAirborneDescentStatus
                : raw.DescentAngleStatus;

        return raw with
        {
            GlideslopeDeg = pathAngleToShow,
            GlideslopeStatus = pathStatusToShow,
            DescentAngleDeg = descentAngleToShow,
            DescentAngleStatus = descentStatusToShow,
        };
    }

    public void Reset()
    {
        _runwayKey = null;
        _heldPathAngleDeg = null;
        _heldPathStatus = LandingMonitorStatus.Neutral;
        _lastAirborneDescentAngleDeg = null;
        _lastAirborneDescentStatus = LandingMonitorStatus.Neutral;
        _pathFrozen = false;
    }

    private static string RunwayKey(RunwayConfig runway) =>
        $"{runway.AirportIcao.Trim().ToUpperInvariant()}|" +
        $"{runway.RunwayId.Trim().ToUpperInvariant()}|" +
        $"{runway.ThresholdLatitude:R}|{runway.ThresholdLongitude:R}|{runway.HeadingTrueDeg:R}";
}
