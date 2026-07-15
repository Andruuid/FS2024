using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scenarios;
using ChallengeLab.Core.Scoring;
using ChallengeLab.SimConnect;
// LandingEvaluationKey lives in ChallengeLab.Core.Config

namespace ChallengeLab.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ConfigLoader _configLoader;
    private readonly HighscoreStore _highscores;
    private readonly LandingTraceStore _landingTraces;
    private readonly ScoreEngine? _scoreEngine;
    private readonly LandingEvaluationKey? _evaluationKey;
    private readonly LandingSessionSettings? _sessionSettings;
    private readonly EvaluationKeyLoadResult _evaluationKeyLoad;
    private readonly string? _evaluationKeyPath;
    private readonly ISimBridge _sim;
    private readonly DispatcherTimer _reconnectTimer;

    private ChallengeCardViewModel? _selectedChallenge;
    private string _connectionStatus = "Disconnected";
    private bool _isConnected;
    private bool _isLoading;
    private double _loadProgress;
    private string _loadStatus = "";
    private string _hudTip = "Start a challenge to begin.";
    private string _phaseLabel = "Idle";
    private string _liveStats = "—";
    private string _speedTargetInfo = "Optimal landing speed: —";
    private double? _previewScorePercent;
    private string _previewScoreDisplay = "—";
    private string _previewGrade = "";
    private string _previewCaption = "";
    private string _previewIssues = "";
    private string _previewHeading = "PREVIEW SCORE";
    private bool _previewActive;
    private DateTimeOffset _lastPreviewUtc = DateTimeOffset.MinValue;
    private TelemetrySample? _lastTelemetry;
    private ScoreResult? _lastScore;
    private LandingSession? _session;
    private ChallengeConfig? _activeChallenge;
    private bool _resultVisible;
    private int _selectedTab;
    private HighscoreEntry? _selectedHighscore;
    private LandingReportViewModel? _landingReport;
    private string _reportStatus = "";
    private string _reportBodyText = "";
    private ObservableCollection<ReportMetricViewModel> _reportMetrics = new();
    private string _windowTitle = AppBuild.WindowTitleDefault;

    public MainViewModel(ISimBridge sim, ConfigLoader? configLoader = null, HighscoreStore? highscores = null)
    {
        _sim = sim;
        _configLoader = configLoader ?? new ConfigLoader();
        _highscores = highscores ?? new HighscoreStore();
        _landingTraces = new LandingTraceStore();

        // Load phase-weighted evaluation key from repo JSON at startup (finetune without code changes).
        _evaluationKeyLoad = _configLoader.LoadEvaluationKey();
        _evaluationKey = _evaluationKeyLoad.Key;
        _evaluationKeyPath = _evaluationKeyLoad.Path;
        if (_evaluationKeyLoad.IsValid && _evaluationKey is not null)
        {
            _scoreEngine = new ScoreEngine(_evaluationKey);
            _sessionSettings = _evaluationKey.ToSessionSettings();
            ConfigurationStatus = $"Scoring configuration ready: {_evaluationKey.Id} v{_evaluationKey.Version}";
        }
        else
        {
            ConfigurationStatus = "SCORING DISABLED — " + string.Join(" | ", _evaluationKeyLoad.Errors);
        }

        Challenges = new ObservableCollection<ChallengeCardViewModel>();
        Highscores = new ObservableCollection<HighscoreEntry>();
        CriterionResults = new ObservableCollection<CriterionResultViewModel>();

        StartChallengeCommand = new RelayCommand(async () => await StartChallengeAsync(), () =>
            SelectedChallenge is { Available: true } && HasValidScoringConfiguration && !IsLoading);
        RestartCommand = new RelayCommand(async () => await RestartAsync(), () =>
            (_activeChallenge is not null || SelectedChallenge is { Available: true })
            && HasValidScoringConfiguration && !IsLoading);
        CleanMetricsCommand = new RelayCommand(CleanMetrics, CanCleanMetrics);
        GoCommand = new RelayCommand(GoFlight, CanGoFlight);
        ConnectCommand = new RelayCommand(TriggerConnect);
        DismissResultCommand = new RelayCommand(() => ResultVisible = false);
        ClearHighscoreSelectionCommand = new RelayCommand(() => SelectedHighscore = null);
        OpenMenuCommand = new RelayCommand(() =>
        {
            ResultVisible = false;
            SelectedTab = 0;
            // Toggle main window; HUD stays up (wired in MainWindow).
            RequestToggleMain?.Invoke();
        });

        _sim.StateChanged += OnSimStateChanged;
        _sim.TelemetryReceived += OnTelemetry;
        _sim.LogMessage += (_, msg) => AppendLog(msg);

        _reconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _reconnectTimer.Tick += (_, _) =>
        {
            if (_sim.State is SimConnectionState.Disconnected or SimConnectionState.SimNotRunning)
                TriggerConnect();
        };
        _reconnectTimer.Start();

        LoadCatalog();
        AppendLog($"{AppBuild.Tag} started");
        LogEvaluationKeyStatus();
        RefreshHighscores();
    }

    private void LogEvaluationKeyStatus()
    {
        if (_evaluationKey is { Phases.Count: > 0 } key)
        {
            var phaseSummary = string.Join(" + ",
                key.Phases.Select(p => $"{p.DisplayName} {p.WeightPercent:0}%"));
            var metricCount = key.Phases.Sum(p => p.Metrics.Count);
            AppendLog(
                $"Evaluation key loaded: {key.Id} v{key.Version} · {metricCount} metrics · " +
                $"{phaseSummary}");
            if (!string.IsNullOrEmpty(_evaluationKeyPath))
                AppendLog($"  path: {_evaluationKeyPath}");
            AppendLog("  Edit config/scoring/profiles/landing-evaluation-key.json to finetune (no rebuild needed if you restart the app).");
        }
        else
        {
            AppendLog(
                "SCORING DISABLED: evaluation key missing or invalid. " +
                string.Join(" | ", _evaluationKeyLoad.Errors));
            if (!string.IsNullOrEmpty(_evaluationKeyPath))
                AppendLog($"  last path tried: {_evaluationKeyPath}");
        }
    }

    public event Action? RequestConnect;
    public event Action? RequestShowHud;
    /// <summary>Show or hide the main app window (HUD stays). Menu button toggle.</summary>
    public event Action? RequestToggleMain;
    public event Action<ScoreResult>? ScoreComputed;

    public bool HasValidScoringConfiguration => _scoreEngine is not null && _sessionSettings is not null;
    public string ConfigurationStatus { get; }

    public void TriggerConnect() => RequestConnect?.Invoke();

    public ObservableCollection<ChallengeCardViewModel> Challenges { get; }
    public ObservableCollection<HighscoreEntry> Highscores { get; }
    public ObservableCollection<CriterionResultViewModel> CriterionResults { get; }

    public ICommand StartChallengeCommand { get; }
    public ICommand RestartCommand { get; }
    /// <summary>Wipe landing metrics only (no re-spawn); preview returns to 100%.</summary>
    public ICommand CleanMetricsCommand { get; }
    /// <summary>HUD Go: SET PAUSE OFF (resume flight after Start/Restart hold).</summary>
    public ICommand GoCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DismissResultCommand { get; }
    public ICommand ClearHighscoreSelectionCommand { get; }
    public ICommand OpenMenuCommand { get; }

    public HighscoreEntry? SelectedHighscore
    {
        get => _selectedHighscore;
        set
        {
            SetProperty(ref _selectedHighscore, value);
            RebuildLandingReport(value);
        }
    }

    public LandingReportViewModel? LandingReport
    {
        get => _landingReport;
        private set => SetProperty(ref _landingReport, value);
    }

    /// <summary>
    /// Flat list bound by Highscores UI. Replaced as a NEW collection each rebuild
    /// so WPF always rebinds ItemsSource (Clear/Add alone can fail to remeasure).
    /// </summary>
    public ObservableCollection<ReportMetricViewModel> ReportMetrics
    {
        get => _reportMetrics;
        private set => SetProperty(ref _reportMetrics, value);
    }

    public string ReportStatus
    {
        get => _reportStatus;
        private set => SetProperty(ref _reportStatus, value);
    }

    /// <summary>Plain-text dump of all metrics — cannot be hidden by ItemsControl layout bugs.</summary>
    public string ReportBodyText
    {
        get => _reportBodyText;
        private set => SetProperty(ref _reportBodyText, value);
    }

    public string WindowTitle
    {
        get => _windowTitle;
        private set => SetProperty(ref _windowTitle, value);
    }

    public bool HasLandingReport => LandingReport is not null;

    private void RebuildLandingReport(HighscoreEntry? value)
    {
        if (value is null)
        {
            LandingReport = null;
            ReportMetrics = new ObservableCollection<ReportMetricViewModel>();
            ReportStatus = "";
            ReportBodyText = "";
            WindowTitle = AppBuild.WindowTitleDefault;
            return;
        }

        try
        {
            // Criteria null-safety
            value.Criteria ??= new List<HighscoreCriterionDetail>();

            var report = new LandingReportViewModel(value);
            LandingReport = report;

            // Brand-new collection instance
            ReportMetrics = new ObservableCollection<ReportMetricViewModel>(report.Metrics);

            var criteriaCount = value.Criteria.Count;
            var metricCount = ReportMetrics.Count;

            ReportStatus =
                $"{AppBuild.Tag} | score {value.ScorePercent:0.0}% grade {value.Grade} | {metricCount} metrics";

            // Primary view: hierarchical Total / phases / metrics
            var sb = new StringBuilder();
            sb.Append(value.Breakdown);
            if (metricCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine("--- detail ---");
                foreach (var m in ReportMetrics)
                {
                    sb.AppendLine($"{m.DisplayName}: {m.ScoreDisplay}  ({m.RawDisplay})  {m.Verdict}");
                    if (!string.IsNullOrWhiteSpace(m.Note))
                        sb.AppendLine($"  {m.Note}");
                    sb.AppendLine();
                }
            }

            ReportBodyText = sb.ToString();
            WindowTitle = $"Challenge Lab — {AppBuild.Tag} · {value.ScorePercent:0.0}% {value.Grade}";

            AppendLog(
                $"Report opened: {value.ChallengeTitle} {value.ScorePercent:0.0}% · " +
                $"stored criteria={criteriaCount} · UI metrics={metricCount}");
        }
        catch (Exception ex)
        {
            ReportStatus = $"{AppBuild.Tag} ERROR: " + ex.Message;
            ReportBodyText = ex.ToString();
            WindowTitle = $"Challenge Lab — {AppBuild.Tag} ERROR";
            AppendLog("RebuildLandingReport ERROR: " + ex);
        }
    }

    public ChallengeCardViewModel? SelectedChallenge
    {
        get => _selectedChallenge;
        set
        {
            SetProperty(ref _selectedChallenge, value);
            (StartChallengeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        set => SetProperty(ref _connectionStatus, value);
    }

    public bool IsConnected
    {
        get => _isConnected;
        set
        {
            SetProperty(ref _isConnected, value);
            (StartChallengeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            SetProperty(ref _isLoading, value);
            (StartChallengeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public double LoadProgress
    {
        get => _loadProgress;
        set => SetProperty(ref _loadProgress, value);
    }

    public string LoadStatus
    {
        get => _loadStatus;
        set => SetProperty(ref _loadStatus, value);
    }

    public string HudTip
    {
        get => _hudTip;
        set => SetProperty(ref _hudTip, value);
    }

    public string PhaseLabel
    {
        get => _phaseLabel;
        set => SetProperty(ref _phaseLabel, value);
    }

    public string LiveStats
    {
        get => _liveStats;
        set => SetProperty(ref _liveStats, value);
    }

    /// <summary>
    /// Informational HUD line: optimal touchdown IAS (VAPP − offset) and live IAS delta.
    /// Not a scored readout by itself — scoring uses the same target at touchdown.
    /// </summary>
    public string SpeedTargetInfo
    {
        get => _speedTargetInfo;
        set => SetProperty(ref _speedTargetInfo, value);
    }

    /// <summary>Live projected overall % (missing metrics assumed 100%). Null when idle.</summary>
    public double? PreviewScorePercent
    {
        get => _previewScorePercent;
        private set => SetProperty(ref _previewScorePercent, value);
    }

    public string PreviewScoreDisplay
    {
        get => _previewScoreDisplay;
        private set => SetProperty(ref _previewScoreDisplay, value);
    }

    public string PreviewGrade
    {
        get => _previewGrade;
        private set => SetProperty(ref _previewGrade, value);
    }

    public string PreviewCaption
    {
        get => _previewCaption;
        private set => SetProperty(ref _previewCaption, value);
    }

    /// <summary>
    /// Live "why" tags for a weak preview (too high, too fast, weaving, …). Empty when clean.
    /// </summary>
    public string PreviewIssues
    {
        get => _previewIssues;
        private set => SetProperty(ref _previewIssues, value);
    }

    /// <summary>True when <see cref="PreviewIssues"/> has content (HUD visibility).</summary>
    public bool HasPreviewIssues => !string.IsNullOrWhiteSpace(PreviewIssues);

    /// <summary>HUD card title: PREVIEW SCORE while live, FINAL SCORE after settle.</summary>
    public string PreviewHeading
    {
        get => _previewHeading;
        private set => SetProperty(ref _previewHeading, value);
    }

    /// <summary>True while a challenge session is armed and preview should show on the HUD.</summary>
    public bool PreviewActive
    {
        get => _previewActive;
        private set => SetProperty(ref _previewActive, value);
    }

    public ScoreResult? LastScore
    {
        get => _lastScore;
        set
        {
            SetProperty(ref _lastScore, value);
            CriterionResults.Clear();
            if (value is not null)
            {
                // VS / firmness first, then remaining by weight
                var ordered = value.Criteria
                    .OrderByDescending(c => c.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
                                            c.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenByDescending(c => c.MaxOverallPoints)
                    .ToList();
                var first = true;
                foreach (var c in ordered)
                {
                    CriterionResults.Add(new CriterionResultViewModel(c, isPrimary: first));
                    first = false;
                }
            }
        }
    }

    public bool ResultVisible
    {
        get => _resultVisible;
        set => SetProperty(ref _resultVisible, value);
    }

    public int SelectedTab
    {
        get => _selectedTab;
        set => SetProperty(ref _selectedTab, value);
    }

    public string LogText
    {
        get => _logText;
        set => SetProperty(ref _logText, value);
    }
    private string _logText = "";

    public void AttachWindowHandle(IntPtr hwnd) => _sim.Connect(hwnd);

    public void PumpSimConnect() => _sim.ReceiveMessage();

    private void LoadCatalog()
    {
        try
        {
            var challenges = _configLoader.LoadAllChallenges();
            Challenges.Clear();
            foreach (var c in challenges.OrderByDescending(c => c.Available).ThenBy(c => c.Title))
                Challenges.Add(new ChallengeCardViewModel(c));

            SelectedChallenge = Challenges.FirstOrDefault(c => c.Available) ?? Challenges.FirstOrDefault();
            AppendLog($"Loaded {Challenges.Count} challenge(s) from {_configLoader.RootPath}");
        }
        catch (Exception ex)
        {
            AppendLog($"Catalog error: {ex.Message}");
            MessageBox.Show($"Failed to load challenge catalog:\n{ex.Message}", "Challenge Lab",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task StartChallengeAsync()
    {
        if (SelectedChallenge is null || !SelectedChallenge.Available) return;

        if (!_sim.IsConnected)
        {
            MessageBox.Show(
                "Not connected to Microsoft Flight Simulator 2024.\n\nStart the sim first, then click Connect (or wait for auto-reconnect).",
                "Challenge Lab", MessageBoxButton.OK, MessageBoxImage.Information);
            TriggerConnect();
            return;
        }

        await RunLoadAsync(SelectedChallenge.Config);
    }

    private async Task RestartAsync()
    {
        var challenge = _activeChallenge ?? SelectedChallenge?.Config;
        if (challenge is null) return;
        await RunLoadAsync(challenge);
    }

    private async Task RunLoadAsync(ChallengeConfig challenge)
    {
        if (_sessionSettings is null || _scoreEngine is null)
        {
            MessageBox.Show(
                ConfigurationStatus + "\n\nCorrect the evaluation key and restart the app.",
                "Challenge Lab — scoring unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        IsLoading = true;
        ResultVisible = false;
        LastScore = null;
        LoadProgress = 0;
        LoadStatus = "Starting…";
        PhaseLabel = "Loading";

        try
        {
            _activeChallenge = challenge;
            DetachSession();

            var stages = new[]
            {
                "Preparing…",
                "Checking aircraft…",
                "time of day…",
                "Weather…",
                "Positioning…",
                "Configuring…",
                "settle",
                "Ready"
            };

            var progress = new Progress<string>(msg =>
            {
                LoadStatus = msg;
                var idx = Array.FindIndex(stages, s => msg.Contains(s.TrimEnd('…'), StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    if (msg.Contains("aircraft", StringComparison.OrdinalIgnoreCase) || msg.Contains("Check", StringComparison.OrdinalIgnoreCase)) idx = 1;
                    else if (msg.Contains("time", StringComparison.OrdinalIgnoreCase)) idx = 2;
                    else if (msg.Contains("weather", StringComparison.OrdinalIgnoreCase)) idx = 3;
                    else if (msg.Contains("Position", StringComparison.OrdinalIgnoreCase) || msg.Contains("teleport", StringComparison.OrdinalIgnoreCase)
                             || msg.Contains("failed", StringComparison.OrdinalIgnoreCase)) idx = 4;
                    else if (msg.Contains("gear", StringComparison.OrdinalIgnoreCase) || msg.Contains("Configur", StringComparison.OrdinalIgnoreCase)
                             || msg.Contains("spoiler", StringComparison.OrdinalIgnoreCase)) idx = 5;
                    else if (msg.Contains("settle", StringComparison.OrdinalIgnoreCase) || msg.Contains("Waiting", StringComparison.OrdinalIgnoreCase)) idx = 6;
                    else if (msg.Contains("Ready", StringComparison.OrdinalIgnoreCase) || msg.Contains("PAUSED", StringComparison.OrdinalIgnoreCase)
                             || msg.Contains("armed", StringComparison.OrdinalIgnoreCase)) idx = 7;
                    else idx = 0;
                }
                LoadProgress = (idx + 1) / (double)stages.Length * 100;
            });

            // Debug artifact only — never FlightLoad mid-session for aircraft swap (MSFS CTD risk).
            var (flightPath, generated) = FltScenarioBuilder.ResolveFlightFile(
                challenge,
                rel => _configLoader.ResolveFlightPath(rel));
            AppendLog(generated
                ? $"Scenario artifact (debug) → {flightPath}"
                : $"flightFile override artifact → {flightPath}");
            AppendLog("Safe start: no mid-session FlightLoad (teleport + velocity + verify).");
            var tod = challenge.TimeOfDay;
            AppendLog(
                $"Spawn from JSON: IAS {challenge.Spawn.AirspeedKts:0} kt · " +
                $"alt {challenge.Spawn.AltitudeFeet:0} ft · hdg {challenge.Spawn.HeadingDeg:0}° · " +
                $"time {tod.Hour:00}:{tod.Minute:00} {(tod.UseZuluTime ? "Z" : "local")} · " +
                $"ac {challenge.AircraftTitles.FirstOrDefault() ?? "?"}");

            var spawnResult = await _sim.LoadScenarioAsync(challenge, flightPath, progress);

            if (!spawnResult.Success)
            {
                LoadStatus = "Spawn failed — not armed";
                PhaseLabel = "Idle";
                AppendLog(
                    $"Spawn verify FAILED — scoring not armed. {spawnResult.Message} " +
                    $"(horiz={spawnResult.HorizontalErrorM:0} m altErr={spawnResult.AltErrorFeet:0} ft " +
                    $"ias={spawnResult.AirspeedKts:0} onGround={spawnResult.ReportedOnGround})");
                MessageBox.Show(
                    "Could not place the aircraft at the challenge spawn.\n\n" +
                    spawnResult.Message +
                    "\n\nScoring was NOT armed (avoids false landings on the runway).\n" +
                    "Try Restart once more, or briefly pause/slew the sim and Restart.",
                    "Challenge Lab — spawn failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            AppendLog(
                $"Spawn verified: horiz={spawnResult.HorizontalErrorM:0} m · " +
                $"altErr={spawnResult.AltErrorFeet:0} ft · ias={spawnResult.AirspeedKts:0} kt");

            var setup = challenge.AircraftSetup;
            AppendLog(
                $"Start config: gear={(setup.GearDown ? "down" : "up")} · flaps={setup.FlapsHandleIndex} · " +
                $"spoilers={(setup.SpoilersRetracted ? "in" : "as-is")} · " +
                $"parkBrake={(setup.ParkingBrakeOn ? "on" : "off")} · " +
                $"autoUnpause={setup.Unpause}");

            _session = new LandingSession(challenge, _sessionSettings);
            _session.PhaseChanged += OnSessionPhaseChanged;
            _session.SettledReady += OnSettledReady;

            LoadProgress = 100;
            if (setup.Unpause)
            {
                LoadStatus = "Armed — fly the landing!";
                HudTip = challenge.HudTips.FirstOrDefault() ?? "Good luck.";
                SetPreviewPerfect();
            }
            else
            {
                // Stable end: airborne at spawn, SET PAUSE ON (not active pause, not yellow RTF —
                // yellow Ready-to-Fly only appears after World Map / FlightLoad, which CTDs mid free-flight).
                LoadStatus = "Ready — PAUSED in air · resume when ready";
                HudTip = "PAUSED at spawn — press GO on the HUD when ready to fly.";
                AppendLog(
                    "Stable hold: SET PAUSE at spawn. Press HUD GO to resume (or ESC → Resume).");
                SetPreviewPerfect("armed · PAUSED · press GO when ready · metrics assumed 100%");
            }

            _session.Arm();
            PhaseLabel = "Armed";
            ResultVisible = false;
            LastScore = null;
            // Seed optimal landing speed before first telemetry tick.
            UpdateSpeedTargetInfo(challenge, sample: null, liveIas: null);
            RotateTips(challenge);
            RequestShowHud?.Invoke();
            (RestartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (AircraftMismatchException acEx)
        {
            LoadStatus = "Wrong aircraft";
            PhaseLabel = "Idle";
            AppendLog($"Wrong aircraft: {acEx.ActualTitle} (need challenge aircraft — no FlightLoad).");
            MessageBox.Show(acEx.Message, "Challenge Lab — load the correct aircraft first",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            LoadStatus = "Failed";
            PhaseLabel = "Idle";
            AppendLog($"Load failed: {ex.Message}");
            MessageBox.Show($"Could not load challenge:\n{ex.Message}", "Challenge Lab",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void DetachSession()
    {
        if (_session is null) return;
        _session.PhaseChanged -= OnSessionPhaseChanged;
        _session.SettledReady -= OnSettledReady;
        _session.Reset();
        _session = null;
        ClearPreview();
        (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool CanCleanMetrics() =>
        _session is not null
        && _session.Phase is not LandingPhase.Idle
        && HasValidScoringConfiguration
        && !IsLoading;

    private bool CanGoFlight() =>
        _sim.IsConnected
        && !IsLoading
        && _session is not null
        && _session.Phase is not LandingPhase.Idle;

    /// <summary>
    /// HUD "Go": resume the sim after Start/Restart SET PAUSE hold (no ESC menu needed).
    /// </summary>
    private void GoFlight()
    {
        if (!CanGoFlight()) return;

        _sim.ResumeFlight();
        LoadStatus = "Go — flying";
        HudTip = "Go! Fly the approach.";
        AppendLog("Go: SET PAUSE OFF (resume flight).");
        (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// HUD "Clean": zero all metrics from this landing at this moment only.
    /// No spawn, weather, or aircraft change — scoring re-arms and preview → 100%.
    /// </summary>
    private void CleanMetrics()
    {
        if (_session is null || !CanCleanMetrics()) return;

        _session.CleanMetrics();
        LastScore = null;
        ResultVisible = false;
        PhaseLabel = "Armed";
        SetPreviewPerfect("cleaned · waiting for approach window · unmeasured = 100%");
        AppendLog("Clean: landing metrics wiped — re-armed from this moment (preview 100%).");
        (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnSessionPhaseChanged(object? sender, LandingPhase phase)
    {
        Application.Current.Dispatcher.Invoke(() => PhaseLabel = phase.ToString());
    }

    private DispatcherTimer? _tipTimer;
    private int _tipIndex;

    private void RotateTips(ChallengeConfig challenge)
    {
        _tipTimer?.Stop();
        if (challenge.HudTips.Count == 0) return;
        _tipIndex = 0;
        _tipTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _tipTimer.Tick += (_, _) =>
        {
            _tipIndex = (_tipIndex + 1) % challenge.HudTips.Count;
            HudTip = challenge.HudTips[_tipIndex];
        };
        _tipTimer.Start();
    }

    private void OnSettledReady(object? sender, EventArgs e)
    {
        if (_session is null || _activeChallenge is null || _scoreEngine is null) return;
        if (_session.IsComplete && LastScore is not null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var result = _scoreEngine.Evaluate(_activeChallenge, _session.Snapshot);
            LastScore = result;
            ResultVisible = true;
            ApplyFinalPreview(result);

            string? tracePath = null;
            try
            {
                // Always keep a time-series dump for offline analysis (even unranked).
                tracePath = _landingTraces.Save(result, _session.Snapshot, samplesPerSecond: 5);
            }
            catch (Exception ex)
            {
                AppendLog($"Landing trace save failed: {ex.Message}");
            }

            if (result.IsRanked)
            {
                _highscores.Add(result);
                RefreshHighscores();
                SelectedHighscore = Highscores.FirstOrDefault();
                SelectedTab = 1;
                HudTip = $"Score {result.ScorePercent:0.#}% · Grade {result.Grade} — full report on Highscores tab";
            }
            else
            {
                SelectedTab = 2;
                HudTip = "UNRANKED — required telemetry was unavailable. See Session for details.";
            }
            PhaseLabel = "Scored";
            ScoreComputed?.Invoke(result);
            RequestShowHud?.Invoke();
            AppendLog(result.IsRanked
                ? $"Scored {result.ScorePercent}% ({result.Grade}) on {result.ChallengeTitle} — {result.Criteria.Count} metrics stored"
                : $"Unranked landing on {result.ChallengeTitle}: {string.Join(" | ", result.IncompleteReasons)}");
            if (tracePath is not null)
                AppendLog($"Landing trace: {tracePath}");
        });
    }

    private void OnTelemetry(object? sender, TelemetrySample sample)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _lastTelemetry = sample;
            LiveStats =
                $"IAS {sample.AirspeedKts:0} kt  ·  GS {sample.GroundSpeedKts:0} kt  ·  " +
                $"VS {sample.VerticalSpeedFpm:0} fpm  ·  Bank {sample.BankDeg:0.0}°  ·  " +
                $"Wind {sample.WindDirectionDeg:000}/{sample.WindVelocityKts:0}kt  ·  " +
                $"{(sample.SimOnGround ? "GND" : "AIR")}";

            if (_activeChallenge is not null && _sessionSettings is not null)
                UpdateSpeedTargetInfo(_activeChallenge, sample, sample.AirspeedKts);

            _session?.Ingest(sample);
            UpdateLivePreview(force: false);
        });
    }

    private void SetPreviewPerfect(string? caption = null)
    {
        PreviewActive = true;
        PreviewHeading = "PREVIEW SCORE";
        PreviewScorePercent = 100;
        PreviewScoreDisplay = "100.0%";
        PreviewGrade = "S";
        PreviewCaption = caption ?? BuildPreviewCaption(_session);
        SetPreviewIssues("");
        _lastPreviewUtc = DateTimeOffset.UtcNow;
    }

    private void ClearPreview()
    {
        PreviewActive = false;
        PreviewHeading = "PREVIEW SCORE";
        PreviewScorePercent = null;
        PreviewScoreDisplay = "—";
        PreviewGrade = "";
        PreviewCaption = "";
        SetPreviewIssues("");
        _lastPreviewUtc = DateTimeOffset.MinValue;
    }

    private void ApplyFinalPreview(ScoreResult result)
    {
        PreviewActive = true;
        PreviewHeading = "FINAL SCORE";
        PreviewScorePercent = result.ScorePercent;
        PreviewScoreDisplay = result.ScoreDisplay;
        PreviewGrade = result.Grade;
        // Same phase breakdown as live preview (approach / TD / rollout %), not a bare "final score".
        PreviewCaption = BuildFinalCaption(result, _session);
        // Rebuild issue line from final result so the pilot still sees what hurt.
        if (_activeChallenge is not null)
            UpdatePreviewIssues(result);
        else
            SetPreviewIssues("");

        _lastPreviewUtc = DateTimeOffset.UtcNow;
    }

    private void SetPreviewIssues(string line)
    {
        PreviewIssues = line ?? "";
        RaisePropertyChanged(nameof(HasPreviewIssues));
    }

    private void UpdateLivePreview(bool force)
    {
        if (_session is null || _activeChallenge is null || _scoreEngine is null)
            return;
        if (_session.Phase is LandingPhase.Idle or LandingPhase.Scored)
            return;

        var now = DateTimeOffset.UtcNow;
        // Throttle ~8 Hz — visual-frame telemetry is faster than needed for the HUD.
        if (!force && (now - _lastPreviewUtc).TotalMilliseconds < 125)
            return;
        _lastPreviewUtc = now;

        try
        {
            // Derived metrics already refreshed inside Ingest; re-run is cheap if needed.
            if (_session.Phase is LandingPhase.Armed
                && _session.Snapshot.ApproachSamples.Count == 0
                && _session.Snapshot.Touchdown is null)
            {
                // Before airborne samples: pure 100% projection.
                SetPreviewPerfect("armed · not airborne yet · all metrics assumed 100%");
                return;
            }

            // Ensure approach/rollout derived fields are current before scoring.
            _session.RefreshDerivedMetrics();

            var preview = _scoreEngine.EvaluatePreview(_activeChallenge, _session.Snapshot);
            PreviewActive = true;
            PreviewHeading = "PREVIEW SCORE";
            PreviewScorePercent = preview.ScorePercent;
            PreviewScoreDisplay = preview.ScoreDisplay;
            PreviewGrade = preview.Grade;
            PreviewCaption = BuildPreviewCaption(_session, preview);
            UpdatePreviewIssues(preview);
        }
        catch (Exception ex)
        {
            AppendLog($"Preview score failed: {ex.Message}");
        }
    }

    private void UpdatePreviewIssues(ScoreResult preview)
    {
        if (_activeChallenge is null || _session is null)
        {
            SetPreviewIssues("");
            return;
        }

        double? vapp = null;
        double? targetTd = null;
        if (_sessionSettings is not null)
        {
            var resolved = SpeedTargetCalculator.Resolve(_activeChallenge, _sessionSettings, _lastTelemetry);
            vapp = resolved.VappKts;
            targetTd = resolved.TargetTouchdownIasKts;
        }

        // Prefer snapshot targets once touchdown metrics exist.
        if (_session.Snapshot.VappKts > 50)
            vapp = _session.Snapshot.VappKts;
        if (_session.Snapshot.TargetTouchdownIasKts > 50)
            targetTd = _session.Snapshot.TargetTouchdownIasKts;

        var issues = LiveApproachIssueBuilder.Build(
            _activeChallenge,
            _session.Snapshot,
            preview,
            _lastTelemetry,
            vapp,
            targetTd,
            _sessionSettings?.ApproachPathMaxDistNm ?? 4.5);
        SetPreviewIssues(LiveApproachIssueBuilder.FormatLine(issues));
    }

    /// <summary>
    /// Honest HUD status: 100% often means "TD/rollout still assumed", not "perfect flying".
    /// </summary>
    private string BuildPreviewCaption(LandingSession? session, ScoreResult? preview = null)
    {
        if (session is null)
            return "unmeasured metrics assumed 100%";

        if (session.Phase is LandingPhase.Armed
            && session.Snapshot.ApproachSamples.Count == 0
            && session.Snapshot.Touchdown is null)
            return "armed · not airborne yet · all metrics assumed 100%";

        var snap = session.Snapshot;
        var maxNm = _sessionSettings?.ApproachPathMaxDistNm ?? 4.5;
        var measuringApproach = snap.ApproachPathSampleCount >= 2
                                && snap.ApproachMetricDurationSec >= 0.5;
        var hasTouchdown = snap.Touchdown is not null;
        var rolloutLive = snap.RolloutPathSegmentCount >= 2 || snap.PostTouchdownAlignmentSampleCount >= 2;

        var (approachPct, tdPct, rollPct) = ReadPhasePercents(preview);

        static string PhaseBit(string name, double? pct, bool measured) =>
            measured && pct is not null
                ? $"{name} {pct:0.#}%"
                : $"{name} assumed 100%";

        if (!measuringApproach && !hasTouchdown)
        {
            return $"outside approach window (>{maxNm:0.#} NM) · not measuring yet · overall assumed 100%";
        }

        if (measuringApproach && !hasTouchdown)
        {
            return $"{PhaseBit("approach", approachPct, true)} · TD & rollout assumed 100%";
        }

        if (hasTouchdown && !rolloutLive)
        {
            return $"{PhaseBit("approach", approachPct, measuringApproach)} · " +
                   $"{PhaseBit("TD", tdPct, true)} · rollout assumed 100%";
        }

        if (hasTouchdown)
        {
            return $"{PhaseBit("approach", approachPct, measuringApproach)} · " +
                   $"{PhaseBit("TD", tdPct, true)} · " +
                   $"{PhaseBit("rollout", rollPct, true)}";
        }

        return "live projection · unmeasured metrics assumed 100%";
    }

    /// <summary>
    /// Final HUD caption mirrors preview layout: approach X% · TD Y% · rollout Z%.
    /// </summary>
    private string BuildFinalCaption(ScoreResult result, LandingSession? session)
    {
        var (approachPct, tdPct, rollPct) = ReadPhasePercents(result);

        var snap = session?.Snapshot;
        var measuringApproach = snap is not null
                                && snap.ApproachPathSampleCount >= 2
                                && snap.ApproachMetricDurationSec >= 0.5;
        var hasTouchdown = snap?.Touchdown is not null;
        var rolloutLive = snap is not null
                          && (snap.RolloutPathSegmentCount >= 2
                              || snap.PostTouchdownAlignmentSampleCount >= 2);

        // Prefer phase scores from the result; fall back only if a phase was never measured.
        static string FinalBit(string name, double? pct, bool measured) =>
            pct is not null
                ? $"{name} {pct:0.#}%"
                : measured
                    ? $"{name} —"
                    : $"{name} n/a";

        var approachBit = FinalBit("approach", approachPct, measuringApproach || approachPct is not null);
        var tdBit = FinalBit("TD", tdPct, hasTouchdown || tdPct is not null);
        var rollBit = FinalBit("rollout", rollPct, rolloutLive || rollPct is not null);

        var core = $"{approachBit} · {tdBit} · {rollBit}";
        if (!result.IsRanked)
            return $"{core} · unranked";
        return core;
    }

    private static (double? Approach, double? Touchdown, double? Rollout) ReadPhasePercents(ScoreResult? score)
    {
        if (score?.PhaseScores is null)
            return (null, null, null);

        double? Pick(string id) =>
            score.PhaseScores
                .FirstOrDefault(p => p.PhaseId.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?.ScorePercent;

        return (Pick("approach"), Pick("touchdown"), Pick("rollout"));
    }

    private void UpdateSpeedTargetInfo(ChallengeConfig challenge, TelemetrySample? sample, double? liveIas)
    {
        if (_sessionSettings is null)
        {
            SpeedTargetInfo = "Optimal landing speed: —";
            return;
        }

        var (vapp, targetTd, source) = SpeedTargetCalculator.Resolve(challenge, _sessionSettings, sample);
        // Informational only — same formula used at scoring time (VAPP − offset, default −5 kt).
        if (liveIas is null)
        {
            SpeedTargetInfo =
                $"Optimal landing speed: {targetTd:0} kt  ·  VAPP {vapp:0} kt  ({source})";
            return;
        }

        var delta = liveIas.Value - targetTd;
        var sign = delta >= 0 ? "+" : "";
        SpeedTargetInfo =
            $"Optimal landing speed: {targetTd:0} kt  ·  VAPP {vapp:0}  ·  " +
            $"IAS now {liveIas:0} ({sign}{delta:0} kt)  ·  {source}";
    }

    private void OnSimStateChanged(object? sender, SimConnectionState state)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsConnected = state == SimConnectionState.Connected;
            ConnectionStatus = _sim.StatusMessage ?? state.ToString();
        });
    }

    private void RefreshHighscores()
    {
        var selectedId = SelectedHighscore?.Id;
        Highscores.Clear();
        foreach (var e in _highscores.Entries)
            Highscores.Add(e);

        // Keep selection on same entry after refresh when possible
        if (selectedId is Guid id && id != Guid.Empty)
        {
            var match = Highscores.FirstOrDefault(h => h.Id == id);
            if (match is not null && !ReferenceEquals(SelectedHighscore, match))
                SelectedHighscore = match;
        }
    }

    private void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        LogText = $"[{stamp}] {line}\n" + LogText;
        if (LogText.Length > 8000)
            LogText = LogText[..8000];
    }

    public void Dispose()
    {
        _reconnectTimer.Stop();
        _tipTimer?.Stop();
        DetachSession();
        _sim.StateChanged -= OnSimStateChanged;
        _sim.TelemetryReceived -= OnTelemetry;
        _sim.Dispose();
    }
}
