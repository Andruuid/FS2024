using System.Windows.Media;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace ChallengeLab.App.ViewModels;

public sealed record LandingMonitorGraphPoint(double ElapsedSeconds, double ScorePercent);

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

    private readonly List<LandingMonitorGraphPoint> _graphPoints = new();
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
    private string _glideslopeDetail = "GREEN · TARGET 3.0° ±0.2°";
    private Brush _glideslopeBrush = NeutralBrush;
    private string _verticalSpeedDisplay = "—";
    private string _verticalSpeedDetail = "GREEN −700–0 FPM";
    private Brush _verticalSpeedBrush = NeutralBrush;
    private double _progressPercent;
    private double _airplaneOffset;
    private string _progressDisplay = "STANDBY";
    private string _etaDisplay = "--:--";
    private string _distanceDisplay = "DIST —";
    private IReadOnlyList<LandingMonitorGraphPoint> _graphSnapshot = Array.Empty<LandingMonitorGraphPoint>();
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
    public string VerticalSpeedDisplay { get => _verticalSpeedDisplay; private set => SetProperty(ref _verticalSpeedDisplay, value); }
    public string VerticalSpeedDetail { get => _verticalSpeedDetail; private set => SetProperty(ref _verticalSpeedDetail, value); }
    public Brush VerticalSpeedBrush { get => _verticalSpeedBrush; private set => SetProperty(ref _verticalSpeedBrush, value); }
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, value); }
    public double AirplaneOffset { get => _airplaneOffset; private set => SetProperty(ref _airplaneOffset, value); }
    public string ProgressDisplay { get => _progressDisplay; private set => SetProperty(ref _progressDisplay, value); }
    public string EtaDisplay { get => _etaDisplay; private set => SetProperty(ref _etaDisplay, value); }
    public string DistanceDisplay { get => _distanceDisplay; private set => SetProperty(ref _distanceDisplay, value); }
    public IReadOnlyList<LandingMonitorGraphPoint> GraphPoints { get => _graphSnapshot; private set => SetProperty(ref _graphSnapshot, value); }
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
            settings?.ApproachPathMaxDistNm ?? 4.5,
            settings?.FlareAglFeet ?? 50);
        ApplyIndicators(reading, challenge?.Runway);

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
            ProgressPercent = 0;
            AirplaneOffset = 0;
            ProgressDisplay = "STANDBY";
            EtaDisplay = "--:--";
            MonitorStatus = reading.ApproachDistanceNm is > 0
                ? $"Collection starts at {settings.ApproachPathMaxDistNm:0.0} NM"
                : "Waiting for the next approach";
            return;
        }

        var progress = sample.SimOnGround ? 100 : reading.ProgressPercent;
        SetProgress(progress);
        DistanceDisplay = reading.ApproachDistanceNm is { } distance
            ? $"DIST {Math.Max(0, distance):0.00} NM"
            : "DIST —";
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

        SetProgress(100);
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
        GraphPoints = Array.Empty<LandingMonitorGraphPoint>();
        TargetLabel = "No runway target";
        PhaseLabel = "IDLE";
        MonitorStatus = "Waiting for an armed runway";
        IsCollecting = false;
        AirspeedDisplay = "—";
        AirspeedDetail = "TARGET —";
        AirspeedBrush = NeutralBrush;
        GlideslopeDisplay = "—";
        GlideslopeDetail = "GREEN · TARGET 3.0° ±0.2°";
        GlideslopeBrush = NeutralBrush;
        VerticalSpeedDisplay = "—";
        VerticalSpeedBrush = NeutralBrush;
        DistanceDisplay = "DIST —";
        EtaDisplay = "--:--";
        SetProgress(0, "STANDBY");
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
        GlideslopeDetail = $"GREEN · TARGET {targetGs:0.0}° ±{half:0.0}°";
        GlideslopeBrush = StatusBrush(reading.GlideslopeStatus);

        VerticalSpeedDisplay = reading.VerticalSpeedFpm is { } verticalSpeed
            ? $"{(Math.Abs(verticalSpeed) < .5 ? 0 : verticalSpeed):0} FPM"
            : "—";
        VerticalSpeedBrush = StatusBrush(reading.VerticalSpeedStatus);
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
        var point = new LandingMonitorGraphPoint(elapsed, Math.Clamp(scorePercent.Value, 0, 100));
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

    private void SetProgress(double progress, string? display = null)
    {
        ProgressPercent = Math.Clamp(progress, 0, 100);
        AirplaneOffset = ProgressPercent / 100.0 * 304.0;
        ProgressDisplay = display ?? $"{ProgressPercent:0}%";
    }

    private static string FormatEta(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
            return "--:--";
        var rounded = Math.Min(5999, (int)Math.Round(seconds));
        return $"{rounded / 60:00}:{rounded % 60:00}";
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
