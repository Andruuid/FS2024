using System.Windows.Media;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace ChallengeLab.App.ViewModels;

/// <summary>Stateful presentation model for one secondary-HUD landing attempt.</summary>
public sealed class SecondaryHudViewModel : ViewModelBase
{
    private const double GraphSampleIntervalSeconds = 0.25;
    private const double GraphRetentionSeconds = 600;
    private const int GraphMaximumPoints = 2400;
    private const double EtaSmoothingSeconds = 1.5;
    private const double MinimumEtaClosingSpeedKts = 5;

    private static readonly Brush NeutralBrush = FrozenBrush(0x8B, 0x9B, 0xB8);
    private static readonly Brush GreenBrush = FrozenBrush(0x3D, 0xDC, 0x97);
    private static readonly Brush OrangeBrush = FrozenBrush(0xFF, 0xB0, 0x20);
    private static readonly Brush RedBrush = FrozenBrush(0xFF, 0x4D, 0x6A);

    private readonly List<ScoreHistoryPoint> _graphPoints = new();
    private DateTimeOffset? _collectionStartedAt;
    private DateTimeOffset? _lastGraphSampleAt;
    private DateTimeOffset? _lastClosureSampleAt;
    private double? _smoothedClosingSpeedKts;
    private string _targetLabel = "No runway target";
    private string _phaseLabel = "IDLE";
    private string _monitorStatus = "Waiting for an armed runway";
    private string _airspeedDisplay = "—";
    private string _airspeedDetail = "TARGET —";
    private Brush _airspeedBrush = NeutralBrush;
    private string _glideslopeDisplay = "—";
    private string _glideslopeDetail = "TARGET 3.0° ±0.2°";
    private Brush _glideslopeBrush = NeutralBrush;
    private string _descentAngleDisplay = "—";
    private string _descentAngleDetail = "TARGET —";
    private Brush _descentAngleBrush = NeutralBrush;
    private string _verticalSpeedDisplay = "—";
    private string _verticalSpeedDetail = "TARGET —";
    private Brush _verticalSpeedBrush = NeutralBrush;
    private string _etaDisplay = "--:--";
    private double _windRelativeFromAngleDeg;
    private double _windSpeedKts;
    private bool _hasWind;
    private string _windFromDisplay = "From —";
    private string _windLongitudinalDisplay = "Head/tailwind —";
    private string _windCrosswindDisplay = "Crosswind —";
    private string _windTotalDisplay = "Wind —";
    private IReadOnlyList<ScoreHistoryPoint> _graphSnapshot = Array.Empty<ScoreHistoryPoint>();
    private bool _isCollecting;
    private double _graphHorizonSeconds = 30;
    private bool _graphHorizonLocked;

    public string TargetLabel { get => _targetLabel; private set => SetProperty(ref _targetLabel, value); }
    public string PhaseLabel { get => _phaseLabel; private set => SetProperty(ref _phaseLabel, value); }
    public string MonitorStatus { get => _monitorStatus; private set => SetProperty(ref _monitorStatus, value); }
    public string AirspeedDisplay { get => _airspeedDisplay; private set => SetProperty(ref _airspeedDisplay, value); }
    public string AirspeedDetail { get => _airspeedDetail; private set => SetProperty(ref _airspeedDetail, value); }
    public Brush AirspeedBrush { get => _airspeedBrush; private set => SetProperty(ref _airspeedBrush, value); }
    public string GlideslopeDisplay { get => _glideslopeDisplay; private set => SetProperty(ref _glideslopeDisplay, value); }
    public string GlideslopeDetail { get => _glideslopeDetail; private set => SetProperty(ref _glideslopeDetail, value); }
    public Brush GlideslopeBrush { get => _glideslopeBrush; private set => SetProperty(ref _glideslopeBrush, value); }
    public string DescentAngleDisplay { get => _descentAngleDisplay; private set => SetProperty(ref _descentAngleDisplay, value); }
    public string DescentAngleDetail { get => _descentAngleDetail; private set => SetProperty(ref _descentAngleDetail, value); }
    public Brush DescentAngleBrush { get => _descentAngleBrush; private set => SetProperty(ref _descentAngleBrush, value); }
    public string VerticalSpeedDisplay { get => _verticalSpeedDisplay; private set => SetProperty(ref _verticalSpeedDisplay, value); }
    public string VerticalSpeedDetail { get => _verticalSpeedDetail; private set => SetProperty(ref _verticalSpeedDetail, value); }
    public Brush VerticalSpeedBrush { get => _verticalSpeedBrush; private set => SetProperty(ref _verticalSpeedBrush, value); }
    public string EtaDisplay { get => _etaDisplay; private set => SetProperty(ref _etaDisplay, value); }
    public double WindRelativeFromAngleDeg { get => _windRelativeFromAngleDeg; private set => SetProperty(ref _windRelativeFromAngleDeg, value); }
    public double WindSpeedKts { get => _windSpeedKts; private set => SetProperty(ref _windSpeedKts, value); }
    public bool HasWind { get => _hasWind; private set => SetProperty(ref _hasWind, value); }
    public string WindFromDisplay { get => _windFromDisplay; private set => SetProperty(ref _windFromDisplay, value); }
    public string WindLongitudinalDisplay { get => _windLongitudinalDisplay; private set => SetProperty(ref _windLongitudinalDisplay, value); }
    public string WindCrosswindDisplay { get => _windCrosswindDisplay; private set => SetProperty(ref _windCrosswindDisplay, value); }
    public string WindTotalDisplay { get => _windTotalDisplay; private set => SetProperty(ref _windTotalDisplay, value); }
    public IReadOnlyList<ScoreHistoryPoint> GraphPoints { get => _graphSnapshot; private set => SetProperty(ref _graphSnapshot, value); }
    public bool IsCollecting { get => _isCollecting; private set => SetProperty(ref _isCollecting, value); }
    public double GraphHorizonSeconds { get => _graphHorizonSeconds; private set => SetProperty(ref _graphHorizonSeconds, value); }

    public void Update(
        TelemetrySample sample,
        ChallengeConfig? challenge,
        LandingSessionSettings? settings,
        double? targetAirspeedKts,
        double? projectedScorePercent,
        LandingPhase phase,
        bool isConnected)
    {
        PhaseLabel = phase.ToString().ToUpperInvariant();
        TargetLabel = challenge is null
            ? "No runway target"
            : $"{challenge.Runway.AirportIcao}  ·  RWY {challenge.Runway.RunwayId}";

        var reading = LandingMonitorCalculator.Calculate(
            sample,
            challenge?.Runway,
            targetAirspeedKts,
            settings?.ApproachPathMinDistNm ?? .2,
            settings?.ApproachPathMaxDistNm ?? 4.5);
        ApplyIndicators(reading, challenge?.Runway);
        ApplyWind(sample, isConnected);

        if (!isConnected)
        {
            MonitorStatus = "Waiting for simulator connection";
            return;
        }

        if (challenge is null || settings is null)
        {
            MonitorStatus = "Waiting for an armed runway";
            return;
        }

        if (_collectionStartedAt is null && reading.IsInsideCollectionWindow)
        {
            _collectionStartedAt = sample.Timestamp;
            IsCollecting = true;
            MonitorStatus = "Collecting landing data";
        }

        if (_collectionStartedAt is null)
        {
            EtaDisplay = "--:--";
            MonitorStatus = reading.ApproachDistanceNm is > 0
                ? $"Collection starts at {settings.ApproachPathMaxDistNm:0.0} NM"
                : "Waiting for the next approach";
            return;
        }

        UpdateEta(sample.Timestamp, reading.ApproachDistanceNm, reading.ClosingSpeedKts, sample.SimOnGround);

        if (!sample.SimOnGround && phase is not LandingPhase.Scored)
            AddGraphPoint(sample.Timestamp, projectedScorePercent);
    }

    public void CompleteAttempt(double? finalScorePercent, DateTimeOffset timestamp)
    {
        PhaseLabel = "SCORED";
        MonitorStatus = "Landing scored";
        if (_collectionStartedAt is null)
            return;

        EtaDisplay = "00:00";
        AddGraphPoint(timestamp, finalScorePercent, force: true);
    }

    public void UpdatePhase(LandingPhase phase) => PhaseLabel = phase.ToString().ToUpperInvariant();

    public void SetDisconnected()
    {
        ResetAttempt();
        MonitorStatus = "Waiting for simulator connection";
    }

    public void ResetAttempt()
    {
        _collectionStartedAt = null;
        _lastGraphSampleAt = null;
        _lastClosureSampleAt = null;
        _smoothedClosingSpeedKts = null;
        _graphHorizonLocked = false;
        GraphHorizonSeconds = 30;
        _graphPoints.Clear();
        GraphPoints = Array.Empty<ScoreHistoryPoint>();
        TargetLabel = "No runway target";
        PhaseLabel = "IDLE";
        MonitorStatus = "Waiting for an armed runway";
        IsCollecting = false;
        AirspeedDisplay = "—";
        AirspeedDetail = "TARGET —";
        AirspeedBrush = NeutralBrush;
        GlideslopeDisplay = "—";
        GlideslopeDetail = "TARGET 3.0° ±0.2°";
        GlideslopeBrush = NeutralBrush;
        DescentAngleDisplay = "—";
        DescentAngleDetail = "TARGET —";
        DescentAngleBrush = NeutralBrush;
        VerticalSpeedDisplay = "—";
        VerticalSpeedDetail = "TARGET —";
        VerticalSpeedBrush = NeutralBrush;
        EtaDisplay = "--:--";
        ResetWind();
    }

    private void ApplyIndicators(LandingMonitorReading reading, RunwayConfig? runway)
    {
        AirspeedDisplay = reading.AirspeedKts is { } ias ? $"{ias:0} KT" : "—";
        AirspeedDetail = reading.TargetAirspeedKts is { } target && reading.AirspeedDeltaKts is { } delta
            ? $"TARGET {target:0}  ·  {(delta >= 0 ? "+" : "")}{delta:0} KT"
            : "TARGET —";
        AirspeedBrush = StatusBrush(reading.AirspeedStatus);

        var targetGs = RunwayPathGeometry.SanitizeGlideslopeDeg(
            runway?.GlideslopeDeg ?? RunwayPathGeometry.DefaultGlideslopeDeg);
        var half = LandingMonitorCalculator.GlideslopeGreenHalfBandDeg;
        GlideslopeDisplay = reading.GlideslopeDeg is { } angle ? $"{angle:0.0}°" : "—";
        GlideslopeDetail = reading.GlideslopeDeg is { } pathAngle
            ? $"{PathPositionLabel(pathAngle, targetGs)} · TARGET {targetGs:0.0}° ±{half:0.0}°"
            : $"TARGET {targetGs:0.0}° ±{half:0.0}°";
        GlideslopeBrush = StatusBrush(reading.GlideslopeStatus);

        DescentAngleDisplay = reading.DescentAngleDeg is { } descentAngle
            ? $"{descentAngle:0.0}°"
            : "—";
        DescentAngleDetail = reading.DescentAngleDeg is { } currentAngle
            ? $"{DescentAngleLabel(currentAngle, targetGs)} · TARGET {targetGs:0.0}° ±{LandingMonitorCalculator.DescentAngleGreenHalfBandDeg:0.0}°"
            : $"TARGET {targetGs:0.0}°";
        DescentAngleBrush = StatusBrush(reading.DescentAngleStatus);

        VerticalSpeedDisplay = reading.VerticalSpeedFpm is { } verticalSpeed
            ? $"{(Math.Abs(verticalSpeed) < .5 ? 0 : verticalSpeed):0} FPM"
            : "—";
        VerticalSpeedDetail = reading.TargetVerticalSpeedFpm is { } targetVerticalSpeed
            ? $"TARGET {targetVerticalSpeed:0} FPM · {targetGs:0.0}°"
            : "TARGET —";
        VerticalSpeedBrush = StatusBrush(reading.DescentAngleStatus);
    }

    private void ApplyWind(TelemetrySample sample, bool isConnected)
    {
        if (!isConnected
            || !double.IsFinite(sample.WindDirectionDeg)
            || !double.IsFinite(sample.WindVelocityKts)
            || !double.IsFinite(sample.HeadingTrueDeg)
            || sample.WindVelocityKts < 0)
        {
            ResetWind();
            return;
        }

        var direction = NormalizeDirection(sample.WindDirectionDeg);
        var speed = sample.WindVelocityKts;
        var relativeFrom = NormalizeSignedAngle(direction - NormalizeDirection(sample.HeadingTrueDeg));
        var relativeRadians = relativeFrom * Math.PI / 180.0;
        var longitudinal = speed * Math.Cos(relativeRadians);
        var crosswind = speed * Math.Sin(relativeRadians);

        WindRelativeFromAngleDeg = relativeFrom;
        WindSpeedKts = speed;
        HasWind = speed >= 0.5;
        WindFromDisplay = HasWind ? $"From {FormatDirection(direction)}°" : "Calm";
        WindLongitudinalDisplay = longitudinal >= -0.05
            ? $"Headwind {Math.Abs(longitudinal):0.0} kt"
            : $"Tailwind {Math.Abs(longitudinal):0.0} kt";
        WindCrosswindDisplay = Math.Abs(crosswind) < 0.05
            ? "Crosswind 0.0 kt"
            : $"Crosswind {(crosswind > 0 ? "R" : "L")} {Math.Abs(crosswind):0.0} kt";
        WindTotalDisplay = $"Wind {speed:0.0} kt";
    }

    private void ResetWind()
    {
        WindRelativeFromAngleDeg = 0;
        WindSpeedKts = 0;
        HasWind = false;
        WindFromDisplay = "From —";
        WindLongitudinalDisplay = "Head/tailwind —";
        WindCrosswindDisplay = "Crosswind —";
        WindTotalDisplay = "Wind —";
    }

    private void UpdateEta(
        DateTimeOffset timestamp,
        double? distanceNm,
        double? rawClosingSpeedKts,
        bool onGround)
    {
        if (onGround || distanceNm is null || distanceNm <= 0)
        {
            EtaDisplay = "00:00";
            return;
        }

        if (rawClosingSpeedKts is null || rawClosingSpeedKts <= MinimumEtaClosingSpeedKts)
        {
            EtaDisplay = "--:--";
            _lastClosureSampleAt = timestamp;
            return;
        }

        if (_smoothedClosingSpeedKts is null || _lastClosureSampleAt is null)
        {
            _smoothedClosingSpeedKts = rawClosingSpeedKts;
        }
        else
        {
            var seconds = Math.Max(0, (timestamp - _lastClosureSampleAt.Value).TotalSeconds);
            var alpha = 1.0 - Math.Exp(-seconds / EtaSmoothingSeconds);
            _smoothedClosingSpeedKts += alpha * (rawClosingSpeedKts.Value - _smoothedClosingSpeedKts.Value);
        }

        _lastClosureSampleAt = timestamp;
        if (_smoothedClosingSpeedKts <= MinimumEtaClosingSpeedKts)
        {
            EtaDisplay = "--:--";
            return;
        }

        var etaSeconds = distanceNm.Value / _smoothedClosingSpeedKts.Value * 3600.0;
        if (!_graphHorizonLocked && _collectionStartedAt is { } collectionStart)
        {
            var elapsedSeconds = Math.Max(0, (timestamp - collectionStart).TotalSeconds);
            GraphHorizonSeconds = Math.Max(30, elapsedSeconds + etaSeconds + 10);
            _graphHorizonLocked = true;
        }
        EtaDisplay = FormatEta(etaSeconds);
    }

    private void AddGraphPoint(DateTimeOffset timestamp, double? scorePercent, bool force = false)
    {
        if (_collectionStartedAt is null || scorePercent is null || !double.IsFinite(scorePercent.Value))
            return;
        if (!force && _lastGraphSampleAt is { } last
            && (timestamp - last).TotalSeconds < GraphSampleIntervalSeconds)
            return;

        var elapsed = Math.Max(0, (timestamp - _collectionStartedAt.Value).TotalSeconds);
        var point = new ScoreHistoryPoint(elapsed, Math.Clamp(scorePercent.Value, 0, 100));
        if (force && _graphPoints.Count > 0 && Math.Abs(_graphPoints[^1].ElapsedSeconds - elapsed) < .001)
            _graphPoints[^1] = point;
        else
            _graphPoints.Add(point);
        _lastGraphSampleAt = timestamp;

        while (_graphPoints.Count > 0
               && (elapsed - _graphPoints[0].ElapsedSeconds > GraphRetentionSeconds
                   || _graphPoints.Count > GraphMaximumPoints))
            _graphPoints.RemoveAt(0);

        GraphPoints = _graphPoints.ToArray();
    }

    private static string FormatEta(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            return "--:--";
        var rounded = Math.Min(5999, (int)Math.Round(seconds));
        return $"{rounded / 60:00}:{rounded % 60:00}";
    }

    private static double NormalizeDirection(double degrees)
    {
        var normalized = degrees % 360.0;
        return normalized < 0 ? normalized + 360.0 : normalized;
    }

    private static double NormalizeSignedAngle(double degrees)
    {
        var normalized = NormalizeDirection(degrees);
        return normalized > 180.0 ? normalized - 360.0 : normalized;
    }

    private static int FormatDirection(double direction) =>
        (int)Math.Round(NormalizeDirection(direction), MidpointRounding.AwayFromZero) % 360;

    private static string PathPositionLabel(double measuredDeg, double targetDeg)
    {
        var error = measuredDeg - targetDeg;
        if (error < -LandingMonitorCalculator.GlideslopeGreenHalfBandDeg)
            return "LOW";
        if (error > LandingMonitorCalculator.GlideslopeGreenHalfBandDeg)
            return "HIGH";
        return "ON PATH";
    }

    private static string DescentAngleLabel(double measuredDeg, double targetDeg)
    {
        var error = measuredDeg - targetDeg;
        if (error < -LandingMonitorCalculator.DescentAngleGreenHalfBandDeg)
            return "TOO SHALLOW";
        if (error > LandingMonitorCalculator.DescentAngleGreenHalfBandDeg)
            return "TOO STEEP";
        return "ON ANGLE";
    }

    private static Brush StatusBrush(LandingMonitorStatus status) => status switch
    {
        LandingMonitorStatus.Green => GreenBrush,
        LandingMonitorStatus.Orange => OrangeBrush,
        LandingMonitorStatus.Red => RedBrush,
        _ => NeutralBrush
    };

    private static Brush FrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }
}
