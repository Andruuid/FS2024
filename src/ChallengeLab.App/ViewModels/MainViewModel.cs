using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using ChallengeLab.Core.Career;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scenarios;
using ChallengeLab.Core.Scoring;
using ChallengeLab.SimConnect;
// LandingEvaluationKey lives in ChallengeLab.Core.Config

namespace ChallengeLab.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    public const int CareerTabIndex = 0;
    public const int ChallengesTabIndex = 1;
    public const int HighscoresTabIndex = 2;
    public const int SessionTabIndex = 3;
    public const int TestingTabIndex = 4;

    private readonly ConfigLoader _configLoader;
    private readonly HighscoreStore _highscores;
    private readonly LandingTraceStore _landingTraces;
    private readonly FlightTapeStore _flightTapes;
    private readonly FlightTapeRecorder _flightTapeRecorder = new();
    private readonly CareerProgressStore _careerStore;
    private readonly IRandomIndexProvider? _careerRandom;
    private readonly ScoreEngine? _scoreEngine;
    private readonly LandingEvaluationKey? _evaluationKey;
    private readonly LandingSessionSettings? _sessionSettings;
    private readonly EvaluationKeyLoadResult _evaluationKeyLoad;
    private readonly string? _evaluationKeyPath;
    private readonly ScoreEngine? _freeScoreEngine;
    private readonly LandingSessionSettings? _freeSessionSettings;
    private readonly EvaluationKeyLoadResult _freeEvaluationKeyLoad;
    private readonly string? _freeEvaluationKeyPath;
    private readonly ISimBridge _sim;
    private ScoreEngine? _activeScoreEngine;
    private LandingSessionSettings? _activeSessionSettings;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly DispatcherTimer _freeInferenceTimer;
    private readonly FreeFlightRunwayInference _freeInference = new();
    private readonly List<ChallengeConfig> _allChallenges = new();
    private readonly HashSet<string> _careerRewardIds = new(StringComparer.OrdinalIgnoreCase);
    private CareerProgressionService? _career;
    private string _careerConfigurationStatus = "Career configuration has not loaded.";
    private LandingAttemptOrigin _attemptOrigin = LandingAttemptOrigin.DefaultChallenge;
    private int? _careerAttemptStageNumber;
    private string? _careerAttemptRankId;
    private string? _careerAttemptRankTitle;
    private CareerOutcome? _activeCareerOutcome;

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
    private bool _showTouchdownImpactSummary;
    private string _hudPeakGDisplay = "—";
    private string _hudTouchdownVsDisplay = "—";
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
    private int _selectedResultTab;
    private FlightTapeListItem? _selectedFlightTape;
    private string _testingStatus = "Select a recorded flight tape and evaluate offline.";
    private bool _isEvaluatingFlightTape;
    private LandingReportViewModel? _landingReport;
    private string _reportStatus = "";
    private string _reportBodyText = "";
    private ObservableCollection<ReportMetricViewModel> _reportMetrics = new();
    private string _windowTitle = AppBuild.WindowTitleDefault;
    private HudOperatingMode _hudOperatingMode = HudOperatingMode.Normal;
    private string _freeAirportStatus = "Detecting airport and runway...";
    private CancellationTokenSource? _freeFlightCts;
    private bool _freeInferenceBusy;
    private string _lastFreeInferenceError = "";
    private bool _isSecondaryHudVisible;

    // Post-spawn GO gate: wait until IAS + surfaces match challenge before enabling GO.
    private bool _isSpawnPreparing;
    private bool _isSpawnReady;
    private string _spawnReadinessText = "";
    private string _spawnReadinessDetail = "";
    private DispatcherTimer? _spawnReadinessTimer;
    private ChallengeConfig? _spawnReadinessChallenge;
    private DateTimeOffset _spawnReadinessStartedUtc;
    private DateTimeOffset _lastSpawnReadinessLogUtc;
    private DateTimeOffset _lastSpawnConfigPulseUtc = DateTimeOffset.MinValue;

    public MainViewModel(
        ISimBridge sim,
        ConfigLoader? configLoader = null,
        HighscoreStore? highscores = null,
        CareerProgressStore? careerStore = null,
        IRandomIndexProvider? careerRandom = null)
    {
        _sim = sim;
        _configLoader = configLoader ?? new ConfigLoader();
        _highscores = highscores ?? new HighscoreStore();
        _landingTraces = new LandingTraceStore();
        _flightTapes = new FlightTapeStore();
        _careerStore = careerStore ?? new CareerProgressStore();
        _careerRandom = careerRandom;

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

        try
        {
            var catalog = _configLoader.LoadCatalog();
            _freeEvaluationKeyLoad = string.IsNullOrWhiteSpace(catalog.FreeFlightEvaluationKey)
                ? EvaluationKeyLoadResult.Failure(null, "catalog.json must define freeFlightEvaluationKey.")
                : _configLoader.LoadEvaluationKey(catalog.FreeFlightEvaluationKey);
        }
        catch (Exception ex)
        {
            _freeEvaluationKeyLoad = EvaluationKeyLoadResult.Failure(null, ex.Message);
        }

        _freeEvaluationKeyPath = _freeEvaluationKeyLoad.Path;
        if (_freeEvaluationKeyLoad.Key is { } freeKey && _freeEvaluationKeyLoad.IsValid)
        {
            _freeScoreEngine = new ScoreEngine(freeKey);
            _freeSessionSettings = freeKey.ToSessionSettings();
        }

        Challenges = new ObservableCollection<ChallengeCardViewModel>();
        Highscores = new ObservableCollection<HighscoreEntry>();
        FlightTapes = new ObservableCollection<FlightTapeListItem>();
        CriterionResults = new ObservableCollection<CriterionResultViewModel>();
        CareerRewards = new ObservableCollection<CareerRewardSlotViewModel>();
        SecondaryHud = new SecondaryHudViewModel();

        StartChallengeCommand = new RelayCommand(async () => await StartChallengeAsync(), () =>
            IsNormalMode && SelectedChallenge is { Available: true } && HasValidScoringConfiguration && !IsLoading);
        AcceptCareerAssignmentCommand = new RelayCommand(AcceptCareerAssignment, () =>
            IsCareerAvailable && !CareerIsComplete && !CareerHasAssignment && !IsLoading);
        StartCareerAssignmentCommand = new RelayCommand(async () => await StartCareerAssignmentAsync(), () =>
            IsCareerAvailable && CareerHasAssignment && HasValidScoringConfiguration && !IsLoading);
        RestartCommand = new RelayCommand(async () => await RestartAsync(), () =>
            IsNormalMode && (_activeChallenge is not null || SelectedChallenge is { Available: true })
            && HasValidScoringConfiguration && !IsLoading);
        CleanMetricsCommand = new RelayCommand(CleanMetrics, CanCleanMetrics);
        NormalModeCommand = new RelayCommand(async () => await SetHudOperatingModeAsync(HudOperatingMode.Normal), () => !IsLoading);
        FreeModeCommand = new RelayCommand(async () => await SetHudOperatingModeAsync(HudOperatingMode.Free), () => !IsLoading);
        GoCommand = new RelayCommand(GoFlight, CanGoFlight);
        ConnectCommand = new RelayCommand(TriggerConnect);
        DismissResultCommand = new RelayCommand(() => ResultVisible = false);
        ClearHighscoreSelectionCommand = new RelayCommand(() => SelectedHighscore = null);
        ToggleSecondaryHudCommand = new RelayCommand(() => RequestToggleSecondaryHud?.Invoke());
        RefreshFlightTapesCommand = new RelayCommand(RefreshFlightTapes);
        EvaluateSelectedFlightTapeCommand = new RelayCommand(
            EvaluateSelectedFlightTape,
            () => SelectedFlightTape is not null && HasValidScoringConfiguration && !IsEvaluatingFlightTape);
        BrowseFlightTapeCommand = new RelayCommand(
            BrowseAndEvaluateFlightTape,
            () => HasValidScoringConfiguration && !IsEvaluatingFlightTape);
        OpenFlightsFolderCommand = new RelayCommand(OpenFlightsFolder);
        OpenMenuCommand = new RelayCommand(() =>
        {
            ResultVisible = false;
            SelectedTab = HighscoresTabIndex;
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

        _freeInferenceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _freeInferenceTimer.Tick += async (_, _) => await RunFreeInferenceAsync();

        LoadCatalog();
        AppendLog($"{AppBuild.Tag} started");
        LogEvaluationKeyStatus();
        LogFreeEvaluationKeyStatus();
        RefreshHighscores();
        RefreshFlightTapes();
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

    private void LogFreeEvaluationKeyStatus()
    {
        if (_freeEvaluationKeyLoad.Key is { Phases.Count: > 0 } key)
        {
            AppendLog($"Free-flight evaluation key loaded: {key.Id} v{key.Version}");
            if (!string.IsNullOrEmpty(_freeEvaluationKeyPath))
                AppendLog($"  path: {_freeEvaluationKeyPath}");
            return;
        }

        AppendLog(
            "FREE MODE SCORING DISABLED: " +
            string.Join(" | ", _freeEvaluationKeyLoad.Errors));
    }

    public event Action? RequestConnect;
    public event Action? RequestShowHud;
    public event Action? RequestToggleSecondaryHud;
    /// <summary>Show or hide the main app window (HUD stays). Menu button toggle.</summary>
    public event Action? RequestToggleMain;
    public event Action<ScoreResult>? ScoreComputed;

    public SecondaryHudViewModel SecondaryHud { get; }

    public bool IsSecondaryHudVisible
    {
        get => _isSecondaryHudVisible;
        private set => SetProperty(ref _isSecondaryHudVisible, value);
    }

    public void SetSecondaryHudVisible(bool visible) => IsSecondaryHudVisible = visible;

    public bool HasValidScoringConfiguration => _scoreEngine is not null && _sessionSettings is not null;
    public bool HasValidFreeScoringConfiguration =>
        _freeScoreEngine is not null && _freeSessionSettings is not null;
    public string ConfigurationStatus { get; }

    private ScoreEngine? CurrentScoreEngine =>
        _activeScoreEngine ?? (IsFreeMode ? _freeScoreEngine : _scoreEngine);
    private LandingSessionSettings? CurrentSessionSettings =>
        _activeSessionSettings ?? (IsFreeMode ? _freeSessionSettings : _sessionSettings);

    public HudOperatingMode OperatingMode
    {
        get => _hudOperatingMode;
        private set
        {
            if (_hudOperatingMode == value) return;
            SetProperty(ref _hudOperatingMode, value);
            RaisePropertyChanged(nameof(IsNormalMode));
            RaisePropertyChanged(nameof(IsFreeMode));
            RaiseModeCommandStates();
        }
    }

    public bool IsNormalMode => OperatingMode == HudOperatingMode.Normal;
    public bool IsFreeMode => OperatingMode == HudOperatingMode.Free;

    public string FreeAirportStatus
    {
        get => _freeAirportStatus;
        private set => SetProperty(ref _freeAirportStatus, value);
    }

    public void TriggerConnect() => RequestConnect?.Invoke();

    private void RaiseModeCommandStates()
    {
        (StartChallengeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (AcceptCareerAssignmentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartCareerAssignmentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (RestartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (NormalModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (FreeModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    public ObservableCollection<ChallengeCardViewModel> Challenges { get; }
    public ObservableCollection<HighscoreEntry> Highscores { get; }
    public ObservableCollection<FlightTapeListItem> FlightTapes { get; }
    public ObservableCollection<CriterionResultViewModel> CriterionResults { get; }
    public ObservableCollection<CareerRewardSlotViewModel> CareerRewards { get; }

    public ICommand StartChallengeCommand { get; }
    public ICommand AcceptCareerAssignmentCommand { get; }
    public ICommand StartCareerAssignmentCommand { get; }
    public ICommand RestartCommand { get; }
    /// <summary>Wipe landing metrics only (no re-spawn); preview returns to 100%.</summary>
    public ICommand CleanMetricsCommand { get; }
    public ICommand NormalModeCommand { get; }
    public ICommand FreeModeCommand { get; }
    /// <summary>HUD Go: SET PAUSE OFF (resume flight after Start/Restart hold).</summary>
    public ICommand GoCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DismissResultCommand { get; }
    public ICommand ClearHighscoreSelectionCommand { get; }
    public ICommand ToggleSecondaryHudCommand { get; }
    public ICommand OpenMenuCommand { get; }
    public ICommand RefreshFlightTapesCommand { get; }
    public ICommand EvaluateSelectedFlightTapeCommand { get; }
    public ICommand BrowseFlightTapeCommand { get; }
    public ICommand OpenFlightsFolderCommand { get; }

    public string FlightsFolderPath => _flightTapes.DirectoryPath;

    public bool IsCareerAvailable => _career is not null;
    public string CareerConfigurationStatus
    {
        get => _careerConfigurationStatus;
        private set => SetProperty(ref _careerConfigurationStatus, value);
    }

    public bool CareerIsComplete => _career?.IsComplete == true;
    public bool CareerHasAssignment => _career?.AcceptedAssignment is not null;
    public bool CareerNeedsAssignment => IsCareerAvailable && !CareerIsComplete && !CareerHasAssignment;
    public int CareerCompletedStageCount => _career?.State.CompletedStageCount ?? 0;
    public int CareerTotalStageCount => _career?.TotalStageCount ?? 5;
    public double CareerProgressPercent => _career?.ProgressPercent ?? 0;
    public string CareerProgressText => $"{CareerCompletedStageCount} / {CareerTotalStageCount} PROMOTIONS";
    public string CareerCurrentRankTitle => CareerIsComplete
        ? "Command Captain"
        : _career?.CurrentRank?.Title ?? "Career unavailable";
    public string CareerPassRequirement =>
        $"Ranked final score ≥ {_career?.Config.PassScorePercent ?? 80:0.0}%";
    public string CareerLastOutcomeText => _career?.State.LastResult?.Message ?? "No career attempt recorded yet.";
    public string CareerAttemptCountText => _career is null
        ? ""
        : $"{_career.State.AttemptCount} classified attempt{(_career.State.AttemptCount == 1 ? "" : "s")} recorded";

    // These properties deliberately return no mission data before acceptance.
    public string CareerAssignmentTitle => _career?.AcceptedAssignment?.Title ?? "";
    public string CareerAssignmentSubtitle => _career?.AcceptedAssignment?.Subtitle ?? "";
    public string CareerAssignmentDescription => _career?.AcceptedAssignment?.Description ?? "";
    public string CareerAssignmentAirportRunway => _career?.AcceptedAssignment is { } challenge
        ? $"{challenge.Runway.AirportIcao} · RUNWAY {challenge.Runway.RunwayId}"
        : "";
    public string CareerAssignmentWeather => _career?.AcceptedAssignment is { } challenge
        ? FormatCareerWeather(challenge)
        : "";
    public string CareerAssignmentAcceptedAt => _career?.State.AcceptedAtUtc is { } acceptedAt
        ? $"Accepted {acceptedAt.ToLocalTime():g} · assignment locked until passed"
        : "";

    public bool IsCareerAttemptActive => _attemptOrigin == LandingAttemptOrigin.CareerAssignment;
    public string CareerHudStatus
    {
        get
        {
            if (!IsCareerAttemptActive) return "";
            if (_activeCareerOutcome is not null) return _activeCareerOutcome.Message;
            var rank = _careerAttemptRankTitle ?? "Career";
            var target = _career?.Config.PassScorePercent ?? 80;
            return $"{rank.ToUpperInvariant()} · CLASSIFIED ASSIGNMENT · TARGET ≥ {target:0.0}%";
        }
    }

    public HighscoreEntry? SelectedHighscore
    {
        get => _selectedHighscore;
        set
        {
            SelectedResultTab = 0;
            SetProperty(ref _selectedHighscore, value);
            RebuildLandingReport(value);
        }
    }

    public int SelectedResultTab
    {
        get => _selectedResultTab;
        set => SetProperty(ref _selectedResultTab, value);
    }

    public FlightTapeListItem? SelectedFlightTape
    {
        get => _selectedFlightTape;
        set
        {
            if (ReferenceEquals(_selectedFlightTape, value)) return;
            SetProperty(ref _selectedFlightTape, value);
            (EvaluateSelectedFlightTapeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            TestingStatus = value is null
                ? "Select a recorded flight tape and evaluate offline."
                : $"Selected: {value.DisplayName}";
        }
    }

    public string TestingStatus
    {
        get => _testingStatus;
        private set => SetProperty(ref _testingStatus, value);
    }

    public bool IsEvaluatingFlightTape
    {
        get => _isEvaluatingFlightTape;
        private set
        {
            if (_isEvaluatingFlightTape == value) return;
            SetProperty(ref _isEvaluatingFlightTape, value);
            (EvaluateSelectedFlightTapeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (BrowseFlightTapeCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

            // Historical free-flight rows only latched length after rollout settle; catalog
            // challenges can still recover length from config for the report header.
            if (value.RunwayLengthMeters is null or <= 0)
            {
                var fromGates = value.Diagnostics?.OperationalGates?.RunwayLengthMeters;
                if (fromGates is > 0)
                    value.RunwayLengthMeters = fromGates;
                else
                {
                    var catalogMatch = _allChallenges.FirstOrDefault(c =>
                        string.Equals(c.Id, value.ChallengeId, StringComparison.OrdinalIgnoreCase));
                    if (catalogMatch is not null
                        && double.IsFinite(catalogMatch.Runway.LengthM)
                        && catalogMatch.Runway.LengthM > 0)
                        value.RunwayLengthMeters = catalogMatch.Runway.LengthM;
                }
            }

            var report = new LandingReportViewModel(value);
            LandingReport = report;

            // Brand-new collection instance
            ReportMetrics = new ObservableCollection<ReportMetricViewModel>(report.DetailMetrics);

            var criteriaCount = value.Criteria.Count;
            var metricCount = ReportMetrics.Count;

            ReportStatus =
                $"{value.ScorePercent:0.0}% final score | {report.MetricCount} metrics" +
                (report.HasPenalties ? $" | {report.Penalties.Count} penalties" : "") +
                (string.IsNullOrWhiteSpace(value.CareerDisplay) ? "" : $" | {value.CareerDisplay}");

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
            (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            SetProperty(ref _isLoading, value);
            (StartChallengeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (AcceptCareerAssignmentCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (StartCareerAssignmentCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RestartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (NormalModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (FreeModeCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

    /// <summary>True while waiting for spawn IAS/gear/flaps/spoilers after Start/Restart.</summary>
    public bool IsSpawnPreparing
    {
        get => _isSpawnPreparing;
        private set
        {
            if (_isSpawnPreparing == value) return;
            SetProperty(ref _isSpawnPreparing, value);
            RaisePropertyChanged(nameof(ShowSpawnReadiness));
            (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>True when spawn config matches and GO may be pressed (paused-hold path).</summary>
    public bool IsSpawnReady
    {
        get => _isSpawnReady;
        private set
        {
            if (_isSpawnReady == value) return;
            SetProperty(ref _isSpawnReady, value);
            RaisePropertyChanged(nameof(ShowSpawnReadiness));
            (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>HUD strip visible while preparing or ready (before GO / while held).</summary>
    public bool ShowSpawnReadiness =>
        !string.IsNullOrEmpty(SpawnReadinessText) && (IsSpawnPreparing || IsSpawnReady);

    /// <summary>"PREPARING…" or "READY".</summary>
    public string SpawnReadinessText
    {
        get => _spawnReadinessText;
        private set
        {
            SetProperty(ref _spawnReadinessText, value);
            RaisePropertyChanged(nameof(ShowSpawnReadiness));
        }
    }

    /// <summary>Live checklist: IAS / gear / flaps / spoilers.</summary>
    public string SpawnReadinessDetail
    {
        get => _spawnReadinessDetail;
        private set => SetProperty(ref _spawnReadinessDetail, value);
    }

    public string LiveStats
    {
        get => _liveStats;
        set => SetProperty(ref _liveStats, value);
    }

    /// <summary>
    /// After settle: replace live IAS/GS/VS strip with Peak G + touchdown VS cards.
    /// </summary>
    public bool ShowTouchdownImpactSummary
    {
        get => _showTouchdownImpactSummary;
        private set => SetProperty(ref _showTouchdownImpactSummary, value);
    }

    /// <summary>HUD card value, e.g. <c>1.49 G</c>.</summary>
    public string HudPeakGDisplay
    {
        get => _hudPeakGDisplay;
        private set => SetProperty(ref _hudPeakGDisplay, value);
    }

    /// <summary>HUD card value, e.g. <c>-363 FPM</c>.</summary>
    public string HudTouchdownVsDisplay
    {
        get => _hudTouchdownVsDisplay;
        private set => SetProperty(ref _hudTouchdownVsDisplay, value);
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
                // Composite touchdown impact first, then remaining by weight.
                var ordered = value.Criteria
                    .OrderByDescending(c => c.Id == "touchdown_impact" ? 1 : 0)
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
            var catalog = _configLoader.LoadCatalog();
            var challenges = _configLoader.LoadAllChallenges(catalog);
            _allChallenges.Clear();
            _allChallenges.AddRange(challenges);
            _careerRewardIds.Clear();
            if (catalog.Career is not null)
            {
                foreach (var rank in catalog.Career.Ranks)
                    if (!string.IsNullOrWhiteSpace(rank.RewardChallengeId))
                        _careerRewardIds.Add(rank.RewardChallengeId);
            }

            var careerValidation = CareerConfigValidationResult.Validate(catalog.Career, challenges);
            if (careerValidation.IsValid && careerValidation.Config is not null)
            {
                try
                {
                    _career = new CareerProgressionService(
                        careerValidation.Config,
                        challenges,
                        _careerStore,
                        _careerRandom);
                    CareerConfigurationStatus = "Career ready · classified promotion ladder loaded.";
                    if (!string.IsNullOrWhiteSpace(_career.RecoveryMessage))
                    {
                        CareerConfigurationStatus += " " + _career.RecoveryMessage;
                        AppendLog(_career.RecoveryMessage);
                    }
                }
                catch (Exception ex)
                {
                    _career = null;
                    CareerConfigurationStatus = "CAREER DISABLED — " + ex.Message;
                }
            }
            else
            {
                _career = null;
                CareerConfigurationStatus = "CAREER DISABLED — " + string.Join(" | ", careerValidation.Errors);
            }

            RefreshCareerPresentation(refreshChallenges: true);
            AppendLog($"Loaded {_allChallenges.Count} challenge(s) from {_configLoader.RootPath}");
            AppendLog(CareerConfigurationStatus);
        }
        catch (Exception ex)
        {
            _career = null;
            CareerConfigurationStatus = "CAREER DISABLED — catalog could not load: " + ex.Message;
            AppendLog($"Catalog error: {ex.Message}");
            MessageBox.Show($"Failed to load challenge catalog:\n{ex.Message}", "Challenge Lab",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void RefreshChallengeCards()
    {
        var selectedId = SelectedChallenge?.Id;
        Challenges.Clear();
        foreach (var challenge in _allChallenges
                     .Where(c => !_careerRewardIds.Contains(c.Id)
                                 || (_career?.UnlockedRanks.Any(r =>
                                     string.Equals(r.RewardChallengeId, c.Id, StringComparison.OrdinalIgnoreCase)) == true))
                     .OrderByDescending(c => c.Available)
                     .ThenBy(c => c.Title))
        {
            Challenges.Add(new ChallengeCardViewModel(challenge));
        }

        SelectedChallenge = Challenges.FirstOrDefault(c =>
                                string.Equals(c.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                            ?? Challenges.FirstOrDefault(c => c.Available)
                            ?? Challenges.FirstOrDefault();
    }

    private void RefreshCareerPresentation(bool refreshChallenges = false)
    {
        CareerRewards.Clear();
        if (_career is not null)
        {
            for (var index = 0; index < _career.Config.Ranks.Count; index++)
            {
                CareerRewards.Add(new CareerRewardSlotViewModel(
                    index + 1,
                    _career.Config.Ranks[index],
                    _career.GetRewardChallenge(index),
                    index < _career.State.CompletedStageCount));
            }
        }

        if (refreshChallenges) RefreshChallengeCards();

        foreach (var property in new[]
                 {
                     nameof(IsCareerAvailable), nameof(CareerIsComplete), nameof(CareerHasAssignment),
                     nameof(CareerNeedsAssignment), nameof(CareerCompletedStageCount), nameof(CareerTotalStageCount),
                     nameof(CareerProgressPercent), nameof(CareerProgressText), nameof(CareerCurrentRankTitle),
                     nameof(CareerPassRequirement), nameof(CareerLastOutcomeText), nameof(CareerAttemptCountText),
                     nameof(CareerAssignmentTitle), nameof(CareerAssignmentSubtitle),
                     nameof(CareerAssignmentDescription), nameof(CareerAssignmentAirportRunway),
                     nameof(CareerAssignmentWeather), nameof(CareerAssignmentAcceptedAt),
                     nameof(CareerHudStatus)
                 })
        {
            RaisePropertyChanged(property);
        }

        (AcceptCareerAssignmentCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StartCareerAssignmentCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string FormatCareerWeather(ChallengeConfig challenge)
    {
        var weather = challenge.Weather;
        if (weather.UseLiveWeather) return "LIVE WEATHER";
        if (!string.IsNullOrWhiteSpace(weather.Metar)) return weather.Metar;
        return $"Wind {weather.WindDirectionDeg:000}/{weather.WindVelocityKts:0} kt" +
               (weather.GustKts > weather.WindVelocityKts ? $" gusting {weather.GustKts:0}" : "") +
               $" · visibility {weather.VisibilitySm} SM";
    }

    private Task SetHudOperatingModeAsync(HudOperatingMode mode)
    {
        if (OperatingMode == mode || IsLoading)
            return Task.CompletedTask;

        _tipTimer?.Stop();
        SecondaryHud.ResetAttempt();
        DetachSession();
        StopFreeInference(resetTarget: true);
        _activeChallenge = null;
        SetAttemptOrigin(LandingAttemptOrigin.DefaultChallenge);
        LastScore = null;
        ResultVisible = false;
        LoadProgress = 0;
        LoadStatus = "";
        OperatingMode = mode;

        if (mode == HudOperatingMode.Free)
        {
            PhaseLabel = "Detecting";
            HudTip = "Free mode observes this flight and detects the runway from your true ground track.";
            SpeedTargetInfo = "Optimal landing speed: —";
            FreeAirportStatus = HasValidFreeScoringConfiguration
                ? "Detecting airport and runway..."
                : "Detecting unavailable · generic scoring profile is invalid";
            _freeFlightCts = new CancellationTokenSource();
            _freeInferenceTimer.Start();
            RequestShowHud?.Invoke();
            _ = RunFreeInferenceAsync();
            AppendLog("HUD mode: Free — observing current flight; no simulator state changes.");
        }
        else
        {
            PhaseLabel = "Idle";
            HudTip = "Start a challenge to begin.";
            SpeedTargetInfo = "Optimal landing speed: —";
            FreeAirportStatus = "Detecting airport and runway...";
            AppendLog("HUD mode: Normal — select Start or Restart for a challenge.");
        }

        return Task.CompletedTask;
    }

    private void SetAttemptOrigin(
        LandingAttemptOrigin origin,
        int? careerStageNumber = null,
        string? careerRankId = null,
        string? careerRankTitle = null)
    {
        _attemptOrigin = origin;
        _careerAttemptStageNumber = origin == LandingAttemptOrigin.CareerAssignment ? careerStageNumber : null;
        _careerAttemptRankId = origin == LandingAttemptOrigin.CareerAssignment ? careerRankId : null;
        _careerAttemptRankTitle = origin == LandingAttemptOrigin.CareerAssignment ? careerRankTitle : null;
        _activeCareerOutcome = null;
        RaisePropertyChanged(nameof(IsCareerAttemptActive));
        RaisePropertyChanged(nameof(CareerHudStatus));
    }

    private void StopFreeInference(bool resetTarget)
    {
        _freeInferenceTimer.Stop();
        _freeFlightCts?.Cancel();
        _freeFlightCts?.Dispose();
        _freeFlightCts = null;
        if (resetTarget)
            _freeInference.Reset();
    }

    private async Task RunFreeInferenceAsync()
    {
        if (!IsFreeMode || !_sim.IsConnected || _lastTelemetry is null || _freeInferenceBusy)
            return;

        var cts = _freeFlightCts;
        if (cts is null || cts.IsCancellationRequested)
            return;

        _freeInferenceBusy = true;
        try
        {
            var sample = _lastTelemetry;
            var airports = await _sim.GetAirportsAsync(cts.Token);
            if (!IsCurrentFreeScan(cts)) return;

            var nearby = _freeInference.RankNearbyAirports(sample, airports);
            var nearest = nearby.FirstOrDefault();
            if (nearest is null)
            {
                FreeAirportStatus = "Detecting · airport catalog is empty";
                PhaseLabel = "Detecting";
                return;
            }

            IReadOnlyList<AirportRunwayFacility> details = Array.Empty<AirportRunwayFacility>();
            if (_freeInference.LockedTarget is null)
            {
                var detailTasks = nearby
                    .Where(a => a.DistanceNm <= _freeInference.Settings.NearbyAirportRadiusNm)
                    .Select(a => GetAirportRunwaysOrNullAsync(a.Airport, cts.Token));
                details = (await Task.WhenAll(detailTasks))
                    .Where(x => x is not null)
                    .Cast<AirportRunwayFacility>()
                    .ToList();
                if (!IsCurrentFreeScan(cts)) return;
            }

            var inference = _freeInference.Update(sample, nearby, details);
            var nearestText = $"Nearest {nearest.Airport.Icao} · {nearest.DistanceNm:0.0} NM";
            if (inference.LockedTarget is { } locked)
            {
                FreeAirportStatus =
                    $"{nearestText} · Locked {locked.Runway.Airport.Icao} RWY {locked.Runway.RunwayId}";
                if (_session is null)
                    ArmFreeFlightSession(locked, sample);
            }
            else if (inference.Candidate is { } candidate)
            {
                FreeAirportStatus =
                    $"{nearestText} · Checking {candidate.Runway.Airport.Icao} RWY {candidate.Runway.RunwayId} " +
                    $"({inference.StableSamples}/{_freeInference.Settings.StableSamplesToLock})";
                PhaseLabel = "Detecting";
            }
            else
            {
                FreeAirportStatus = $"{nearestText} · Detecting approach runway";
                PhaseLabel = "Detecting";
            }

            _lastFreeInferenceError = "";
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Expected when switching modes, clearing, or disconnecting.
        }
        catch (Exception ex)
        {
            if (!IsCurrentFreeScan(cts)) return;
            PhaseLabel = "Detecting";
            FreeAirportStatus = "Detecting · facility data temporarily unavailable";
            if (!string.Equals(_lastFreeInferenceError, ex.Message, StringComparison.Ordinal))
            {
                _lastFreeInferenceError = ex.Message;
                AppendLog($"Free detection retry: {ex.Message}");
            }
        }
        finally
        {
            _freeInferenceBusy = false;
        }
    }

    private bool IsCurrentFreeScan(CancellationTokenSource cts) =>
        IsFreeMode && ReferenceEquals(_freeFlightCts, cts) && !cts.IsCancellationRequested;

    private async Task<AirportRunwayFacility?> GetAirportRunwaysOrNullAsync(
        AirportFacility airport,
        CancellationToken ct)
    {
        try
        {
            return await _sim.GetAirportRunwaysAsync(airport, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppendLog($"Facility {airport.Icao} unavailable: {ex.Message}");
            return null;
        }
    }

    private void ArmFreeFlightSession(FreeFlightTarget target, TelemetrySample sample)
    {
        if (_freeSessionSettings is null || _freeScoreEngine is null)
        {
            PhaseLabel = "Detecting";
            HudTip = "Free scoring profile is unavailable. Check the Session log.";
            return;
        }

        var airport = target.Runway.Airport.Icao.Trim().ToUpperInvariant();
        var runway = target.Runway.RunwayId.Trim().ToUpperInvariant();
        var challenge = FreeFlightChallengeFactory.Create(target, sample);
        var effective = EffectiveEvaluationProfileBuilder.Build(_freeEvaluationKeyLoad.Key!, challenge);
        _activeScoreEngine = new ScoreEngine(effective.Key, effective.ProfileHash);
        _activeSessionSettings = effective.Key.ToSessionSettings();
        var noseImpactApplicable = FreeFlightCapabilityResolver.ResolveDecision(
            challenge.FreeFlightCapabilities,
            FreeFlightGateIds.NoseGearImpact).Applicability
            != FreeFlightGateApplicability.NotApplicable;
        _sim.SetNoseGearImpactTelemetryEnabled(
            noseImpactApplicable && _activeSessionSettings.OperationalGates.NoseGearImpact is not null);

        SecondaryHud.ResetAttempt();
        _activeChallenge = challenge;
        _session = new LandingSession(challenge, _activeSessionSettings);
        _session.PhaseChanged += OnSessionPhaseChanged;
        _session.SettledReady += OnSettledReady;
        _session.Arm();
        SetAttemptOrigin(LandingAttemptOrigin.FreeFlight);
        StartFlightTapeRecording(challenge, LandingAttemptOrigin.FreeFlight.ToString());
        PhaseLabel = "Free · Armed";
        HudTip = $"Locked {airport} RWY {runway} · scoring this landing.";
        LastScore = null;
        ResultVisible = false;
        SetPreviewPerfect("free flight · runway locked · unmeasured metrics assumed 100%");
        UpdateSpeedTargetInfo(challenge, sample, sample.AirspeedKts);
        (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        AppendLog(
            $"Free armed: {airport} RWY {runway} · {target.ThresholdDistanceNm:0.0} NM · " +
            $"track error {target.TrackErrorDeg:0.0}° · cross-track {target.CrossTrackNm:0.00} NM · " +
            $"gear gate={(challenge.RequireGearDown ? "on" : "not applicable")} Â· " +
            $"capabilities frozen ({challenge.FreeFlightCapabilities?.GateDecisions.Count ?? 0} gate decisions).");
    }

    private async Task StartChallengeAsync()
    {
        if (!IsNormalMode || SelectedChallenge is null || !SelectedChallenge.Available) return;

        if (!EnsureSimulatorConnected()) return;

        await RunLoadAsync(SelectedChallenge.Config, LandingAttemptOrigin.DefaultChallenge);
    }

    private void AcceptCareerAssignment()
    {
        if (_career is null || _career.IsComplete || _career.AcceptedAssignment is not null) return;
        try
        {
            var assignment = _career.AcceptAssignment();
            _activeCareerOutcome = null;
            RefreshCareerPresentation();
            AppendLog(
                $"Career assignment accepted for {_career.CurrentRank?.Title}: {assignment.Id}. " +
                "Assignment is locked until a ranked pass.");
        }
        catch (Exception ex)
        {
            CareerConfigurationStatus = "CAREER ERROR — " + ex.Message;
            AppendLog(CareerConfigurationStatus);
        }
    }

    private async Task StartCareerAssignmentAsync()
    {
        if (_career?.AcceptedAssignment is not { } assignment || IsLoading) return;
        if (!EnsureSimulatorConnected()) return;

        if (!IsNormalMode)
            await SetHudOperatingModeAsync(HudOperatingMode.Normal);

        var rank = _career.CurrentRank;
        await RunLoadAsync(
            assignment,
            LandingAttemptOrigin.CareerAssignment,
            _career.State.CompletedStageCount + 1,
            rank?.Id,
            rank?.Title);
    }

    private bool EnsureSimulatorConnected()
    {
        if (_sim.IsConnected) return true;
        MessageBox.Show(
            "Not connected to Microsoft Flight Simulator 2024.\n\nStart the sim first, then click Connect (or wait for auto-reconnect).",
            "Challenge Lab", MessageBoxButton.OK, MessageBoxImage.Information);
        TriggerConnect();
        return false;
    }

    private async Task RestartAsync()
    {
        if (!IsNormalMode) return;
        var challenge = _activeChallenge ?? SelectedChallenge?.Config;
        if (challenge is null) return;
        var origin = _activeChallenge is null
            ? LandingAttemptOrigin.DefaultChallenge
            : _attemptOrigin;
        await RunLoadAsync(
            challenge,
            origin,
            _careerAttemptStageNumber,
            _careerAttemptRankId,
            _careerAttemptRankTitle);
    }

    private async Task RunLoadAsync(
        ChallengeConfig challenge,
        LandingAttemptOrigin requestedOrigin,
        int? careerStageNumber = null,
        string? careerRankId = null,
        string? careerRankTitle = null)
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

        EffectiveEvaluationProfile effective;
        try
        {
            effective = EffectiveEvaluationProfileBuilder.Build(_evaluationKey!, challenge);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Challenge scoring override is invalid:\n\n" + ex.Message,
                "Challenge Lab — scoring unavailable",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            AppendLog($"Challenge scoring override invalid: {ex.Message}");
            return;
        }

        SecondaryHud.ResetAttempt();
        SetAttemptOrigin(LandingAttemptOrigin.DefaultChallenge);
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
            _activeScoreEngine = new ScoreEngine(effective.Key, effective.ProfileHash);
            _activeSessionSettings = effective.Key.ToSessionSettings();
            _sim.SetNoseGearImpactTelemetryEnabled(
                _activeSessionSettings.OperationalGates.NoseGearImpact is not null);

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
                _activeScoreEngine = null;
                _activeSessionSettings = null;
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

            _session = new LandingSession(challenge, _activeSessionSettings!);
            _session.PhaseChanged += OnSessionPhaseChanged;
            _session.SettledReady += OnSettledReady;

            LoadProgress = 100;
            if (setup.Unpause)
            {
                StopSpawnReadinessWatch(clearUi: true);
                IsSpawnReady = true;
                IsSpawnPreparing = false;
                SpawnReadinessText = "";
                SpawnReadinessDetail = "";
                LoadStatus = "Armed — fly the landing!";
                HudTip = challenge.HudTips.FirstOrDefault() ?? "Good luck.";
                SetPreviewPerfect();
            }
            else
            {
                // Stable end: airborne at spawn, SET PAUSE ON. GO stays disabled until
                // live IAS + gear/flaps/spoilers match the challenge (see StartSpawnReadinessWatch).
                LoadStatus = "Preparing aircraft…";
                HudTip = "PREPARING — waiting for spawn speed and configuration…";
                AppendLog(
                    "Stable hold: SET PAUSE at spawn. GO enabled when IAS + surfaces match challenge.");
                SetPreviewPerfect("armed · PAUSED · preparing · metrics assumed 100%");
            }

            _session.Arm();
            SetAttemptOrigin(
                requestedOrigin,
                careerStageNumber,
                careerRankId,
                careerRankTitle);
            StartFlightTapeRecording(challenge, requestedOrigin.ToString());
            PhaseLabel = "Armed";
            ResultVisible = false;
            LastScore = null;
            // Seed optimal landing speed before first telemetry tick.
            UpdateSpeedTargetInfo(challenge, sample: null, liveIas: null);
            RotateTips(challenge);
            RequestShowHud?.Invoke();

            if (!setup.Unpause)
                StartSpawnReadinessWatch(challenge);

            (RestartCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (AircraftMismatchException acEx)
        {
            _activeScoreEngine = null;
            _activeSessionSettings = null;
            StopSpawnReadinessWatch(clearUi: true);
            LoadStatus = "Wrong aircraft";
            PhaseLabel = "Idle";
            AppendLog($"Wrong aircraft: {acEx.ActualTitle} (need challenge aircraft — no FlightLoad).");
            MessageBox.Show(acEx.Message, "Challenge Lab — load the correct aircraft first",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            _activeScoreEngine = null;
            _activeSessionSettings = null;
            StopSpawnReadinessWatch(clearUi: true);
            LoadStatus = "Failed";
            PhaseLabel = "Idle";
            AppendLog($"Load failed: {ex.Message}");
            MessageBox.Show($"Could not load challenge:\n{ex.Message}", "Challenge Lab",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (_session is null)
                _sim.SetNoseGearImpactTelemetryEnabled(false);
            IsLoading = false;
            (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void DetachSession()
    {
        StopSpawnReadinessWatch(clearUi: true);
        _sim.SetNoseGearImpactTelemetryEnabled(false);
        _flightTapeRecorder.Cancel();
        if (_session is not null)
        {
            _session.PhaseChanged -= OnSessionPhaseChanged;
            _session.SettledReady -= OnSettledReady;
            _session.Reset();
            _session = null;
        }
        _activeScoreEngine = null;
        _activeSessionSettings = null;
        ClearPreview();
        (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void StartFlightTapeRecording(ChallengeConfig challenge, string attemptOrigin)
    {
        _flightTapeRecorder.Start(challenge, attemptOrigin);
        AppendLog($"Flight tape recording started ({attemptOrigin}).");
    }

    private bool CanCleanMetrics() =>
        !IsLoading && (IsFreeMode
            ? HasValidFreeScoringConfiguration
            : _session is not null
              && _session.Phase is not LandingPhase.Idle
              && HasValidScoringConfiguration);

    private bool CanGoFlight() =>
        IsNormalMode
        && _sim.IsConnected
        && !IsLoading
        && !IsSpawnPreparing
        && IsSpawnReady
        && _session is not null
        && _session.Phase is not LandingPhase.Idle;

    /// <summary>
    /// HUD "Go": resume the sim after Start/Restart SET PAUSE hold (no ESC menu needed).
    /// </summary>
    private void GoFlight()
    {
        if (!CanGoFlight()) return;

        // Fire config again as we unpause — gear/spoilers often only move once SET PAUSE is off.
        var setup = _activeChallenge?.AircraftSetup ?? _spawnReadinessChallenge?.AircraftSetup;
        if (setup is not null)
        {
            try { _sim.ConfigureAircraft(setup); }
            catch (Exception ex) { AppendLog($"Go reconfig: {ex.Message}"); }
        }

        _sim.ResumeFlight();
        if (setup is not null)
        {
            try { _sim.ConfigureAircraft(setup); }
            catch { /* best effort after resume */ }
        }

        ClearSpawnReadinessUi(flying: true);
        LoadStatus = "Go — flying";
        HudTip = "Go! Fly the approach.";
        AppendLog("Go: SET PAUSE OFF (resume flight).");
        (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>Hide PREPARING/READY strip and stop the watch (after GO or ESC-resume detection).</summary>
    private void ClearSpawnReadinessUi(bool flying)
    {
        StopSpawnReadinessWatch(clearUi: true);
        if (flying)
        {
            LoadStatus = "Go — flying";
            HudTip = "Flying the approach.";
        }
    }

    /// <summary>
    /// Poll live telemetry 2 Hz until spawn IAS + gear/flaps/spoilers match challenge JSON.
    /// GO stays disabled while preparing.
    /// </summary>
    private void StartSpawnReadinessWatch(ChallengeConfig challenge)
    {
        StopSpawnReadinessWatch(clearUi: false);

        _spawnReadinessChallenge = challenge;
        _spawnReadinessStartedUtc = DateTimeOffset.UtcNow;
        _lastSpawnReadinessLogUtc = DateTimeOffset.MinValue;
        _lastSpawnConfigPulseUtc = DateTimeOffset.MinValue;

        IsSpawnPreparing = true;
        IsSpawnReady = false;
        SpawnReadinessText = "PREPARING…";
        SpawnReadinessDetail = "waiting for telemetry…";
        LoadStatus = "Preparing aircraft…";
        HudTip = "PREPARING — GO unlocks when speed and config match the challenge.";

        _spawnReadinessTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _spawnReadinessTimer.Tick += OnSpawnReadinessTick;
        _spawnReadinessTimer.Start();

        // Immediate first probe (may already be ready).
        EvaluateSpawnReadiness();
    }

    private void StopSpawnReadinessWatch(bool clearUi)
    {
        if (_spawnReadinessTimer is not null)
        {
            _spawnReadinessTimer.Stop();
            _spawnReadinessTimer.Tick -= OnSpawnReadinessTick;
            _spawnReadinessTimer = null;
        }

        _spawnReadinessChallenge = null;

        if (!clearUi) return;

        IsSpawnPreparing = false;
        IsSpawnReady = false;
        SpawnReadinessText = "";
        SpawnReadinessDetail = "";
    }

    private void OnSpawnReadinessTick(object? sender, EventArgs e) => EvaluateSpawnReadiness();

    private void EvaluateSpawnReadiness()
    {
        var challenge = _spawnReadinessChallenge;
        if (challenge is null || !IsSpawnPreparing) return;

        var now = DateTimeOffset.UtcNow;
        var elapsed = (now - _spawnReadinessStartedUtc).TotalSeconds;

        // Re-pulse gear/flaps/spoilers while waiting — under SET PAUSE A330 often ignores
        // the first commands (gear stuck down, spoilers stuck out after a prior landing).
        if (elapsed > 0.4
            && elapsed < SpawnReadiness.HardTimeoutSeconds
            && (now - _lastSpawnConfigPulseUtc).TotalSeconds >= 1.2)
        {
            _lastSpawnConfigPulseUtc = now;
            try
            {
                _sim.ConfigureAircraft(challenge.AircraftSetup);
            }
            catch (Exception ex)
            {
                AppendLog($"Spawn prep reconfig: {ex.Message}");
            }
        }

        // If the pilot ESC-resumed (or slew) and left spawn, stop spinning forever.
        if (_lastTelemetry is not null && elapsed >= 3.0
            && HasLeftSpawnHold(challenge.Spawn, _lastTelemetry))
        {
            AppendLog($"Spawn prep: aircraft left hold — clearing PREPARING ({elapsed:0}s).");
            ClearSpawnReadinessUi(flying: true);
            (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
            return;
        }

        var result = SpawnReadiness.Evaluate(
            challenge.Spawn,
            challenge.AircraftSetup,
            _lastTelemetry,
            elapsed);

        SpawnReadinessDetail = result.Detail;

        if (!result.Ready)
        {
            SpawnReadinessText = "PREPARING…";
            // Throttle log: once per second while waiting.
            if ((now - _lastSpawnReadinessLogUtc).TotalSeconds >= 1.0)
            {
                _lastSpawnReadinessLogUtc = now;
                AppendLog($"Spawn prep ({elapsed:0}s): {result.Detail}");
                if (elapsed >= 8)
                    LoadStatus = "Still preparing — GO unlocks shortly even if gear/spoilers stick";
            }

            return;
        }

        // Ready: stop polling, enable GO.
        if (_spawnReadinessTimer is not null)
        {
            _spawnReadinessTimer.Stop();
            _spawnReadinessTimer.Tick -= OnSpawnReadinessTick;
            _spawnReadinessTimer = null;
        }

        _spawnReadinessChallenge = null;
        IsSpawnPreparing = false;
        IsSpawnReady = true;

        if (result.ForceReady)
        {
            SpawnReadinessText = "READY (timeout)";
            LoadStatus = "Ready — PAUSED · press GO (config best-effort)";
            HudTip = "READY — gear/spoilers may finish after GO. Press GO to fly.";
        }
        else if (result.SoftReady)
        {
            SpawnReadinessText = "READY (soft)";
            LoadStatus = "Ready — PAUSED · press GO (surfaces soft-ok)";
            HudTip = "READY — surfaces may still settle after GO. Press GO to fly.";
        }
        else
        {
            SpawnReadinessText = "READY";
            LoadStatus = "Ready — PAUSED in air · press GO when ready";
            HudTip = "READY — press GO on the HUD when ready to fly.";
        }

        AppendLog(
            $"Spawn ready{(result.ForceReady ? " (force)" : result.SoftReady ? " (soft)" : "")}: {result.Detail}");
        (GoCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// True when live position has moved well away from spawn (pilot resumed without GO).
    /// </summary>
    private static bool HasLeftSpawnHold(SpawnConfig spawn, TelemetrySample sample)
    {
        // ~0.25 NM horizontal or large altitude departure = no longer on the hold pin.
        const double leaveMeters = 460; // ~0.25 NM
        const double leaveAltFeet = 250;
        var horiz = GeoUtil.HaversineMetersPublic(
            spawn.Latitude, spawn.Longitude, sample.Latitude, sample.Longitude);
        var altErr = Math.Abs(sample.AltitudeFeet - spawn.AltitudeFeet);
        return horiz >= leaveMeters || altErr >= leaveAltFeet;
    }

    /// <summary>
    /// HUD "Clear": zero all metrics from this landing at this moment only.
    /// No spawn, weather, or aircraft change — scoring re-arms and preview → 100%.
    /// </summary>
    private void CleanMetrics()
    {
        if (!CanCleanMetrics()) return;
        SecondaryHud.ResetAttempt();
        _activeCareerOutcome = null;
        RaisePropertyChanged(nameof(CareerHudStatus));

        if (IsFreeMode)
        {
            DetachSession();
            _activeChallenge = null;
            LastScore = null;
            ResultVisible = false;
            StopFreeInference(resetTarget: true);
            PhaseLabel = "Detecting";
            HudTip = "Cleared · detecting again from your current position and true ground track.";
            FreeAirportStatus = "Detecting airport and runway...";
            SpeedTargetInfo = "Optimal landing speed: —";
            _freeFlightCts = new CancellationTokenSource();
            _freeInferenceTimer.Start();
            _ = RunFreeInferenceAsync();
            AppendLog("Free Clear: score/session and runway lock released; detection restarted in place.");
            (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
            return;
        }

        if (_session is null) return;

        _session.CleanMetrics();
        if (_activeChallenge is not null)
            StartFlightTapeRecording(_activeChallenge, _attemptOrigin.ToString());
        LastScore = null;
        ResultVisible = false;
        PhaseLabel = "Armed";
        SetPreviewPerfect("cleaned · waiting for approach window · unmeasured = 100%");
        AppendLog("Clear: landing metrics wiped — re-armed from this moment (preview 100%).");
        (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void OnSessionPhaseChanged(object? sender, LandingPhase phase)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            PhaseLabel = phase.ToString();
            SecondaryHud.UpdatePhase(phase);
        });
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
        var session = _session;
        var challenge = _activeChallenge;
        var scoreEngine = CurrentScoreEngine;
        if (session is null || challenge is null || scoreEngine is null) return;
        if (session.IsComplete && LastScore is not null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            _sim.SetNoseGearImpactTelemetryEnabled(false);
            var result = scoreEngine.Evaluate(challenge, session.Snapshot);
            LastScore = result;
            ResultVisible = true;
            ApplyFinalPreview(result);
            SecondaryHud.CompleteAttempt(
                result.ScorePercent,
                _lastTelemetry?.Timestamp ?? DateTimeOffset.UtcNow);

            string? tracePath = null;
            try
            {
                // Always keep a time-series dump for offline analysis (even unranked).
                tracePath = _landingTraces.Save(result, session.Snapshot, samplesPerSecond: 5);
            }
            catch (Exception ex)
            {
                AppendLog($"Landing trace save failed: {ex.Message}");
            }

            string? flightTapePath = null;
            try
            {
                var finished = _flightTapeRecorder.Finish();
                if (finished is { } tape)
                {
                    flightTapePath = _flightTapes.Save(
                        tape.Challenge,
                        tape.Samples,
                        result,
                        tape.AttemptOrigin);
                    RefreshFlightTapes();
                }
            }
            catch (Exception ex)
            {
                _flightTapeRecorder.Cancel();
                AppendLog($"Flight tape save failed: {ex.Message}");
            }

            var careerAttempt = _attemptOrigin == LandingAttemptOrigin.CareerAssignment;
            if (result.IsRanked)
            {
                _highscores.Add(
                    result,
                    careerAttempt ? _careerAttemptStageNumber : null,
                    careerAttempt ? _careerAttemptRankId : null,
                    careerAttempt ? _careerAttemptRankTitle : null,
                    SecondaryHud.GraphPoints);
                RefreshHighscores();
                SelectedHighscore = Highscores.FirstOrDefault();
            }

            CareerOutcome? careerOutcome = null;
            if (_career is not null)
            {
                try
                {
                    careerOutcome = _career.RecordAttempt(
                        _attemptOrigin,
                        result.ChallengeId,
                        result.IsRanked,
                        result.ScorePercent,
                        string.Join(" | ", result.IncompleteReasons));
                    if (careerOutcome is not null)
                    {
                        _activeCareerOutcome = careerOutcome;
                        RefreshCareerPresentation(refreshChallenges: careerOutcome.Passed);
                        AppendLog("Career: " + careerOutcome.Message);
                    }
                }
                catch (Exception ex)
                {
                    AppendLog("Career result save failed: " + ex.Message);
                    CareerConfigurationStatus = "CAREER SAVE ERROR — " + ex.Message;
                }
            }

            if (careerAttempt)
            {
                SelectedTab = CareerTabIndex;
                HudTip = careerOutcome?.Message
                         ?? "Career result did not match the currently accepted assignment; no progress was changed.";
            }
            else if (result.IsRanked)
            {
                SelectedTab = HighscoresTabIndex;
                HudTip = $"Score {result.ScorePercent:0.#}% · Grade {result.Grade} — full report on Highscores tab";
            }
            else
            {
                SelectedTab = SessionTabIndex;
                HudTip = "UNRANKED — required telemetry was unavailable. See Session for details.";
            }
            PhaseLabel = "Scored";
            ScoreComputed?.Invoke(result);
            RequestShowHud?.Invoke();
            AppendLog(result.IsRanked
                ? $"Scored {result.ScorePercent}% ({result.Grade}) on {result.ChallengeTitle} — " +
                  $"{result.EvaluationKeyId} v{result.EvaluationKeyVersion} · {result.ScoringProfileHash} · " +
                  $"{result.Criteria.Count} metrics stored"
                : $"Unranked landing on {result.ChallengeTitle}: {string.Join(" | ", result.IncompleteReasons)}");
            if (tracePath is not null)
                AppendLog($"Landing trace: {tracePath}");
            if (flightTapePath is not null)
                AppendLog($"Flight tape: {flightTapePath}");
        });
    }

    private void OnTelemetry(object? sender, TelemetrySample sample)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _lastTelemetry = sample;
            // Session tab keeps the strip; scored HUD swaps to Peak G / touchdown VS cards.
            LiveStats =
                $"IAS {sample.AirspeedKts:0} kt  ·  GS {sample.GroundSpeedKts:0} kt  ·  " +
                $"VS {sample.VerticalSpeedFpm:0} fpm  ·  Bank {sample.BankDeg:0.0}°  ·  " +
                $"Wind {sample.WindDirectionDeg:000}/{sample.WindVelocityKts:0}kt  ·  " +
                $"{(sample.SimOnGround ? "GND" : "AIR")}";

            double? targetTouchdownIas = null;
            var sessionSettings = CurrentSessionSettings;
            if (_activeChallenge is not null && sessionSettings is not null)
            {
                UpdateSpeedTargetInfo(_activeChallenge, sample, sample.AirspeedKts);
                targetTouchdownIas = SpeedTargetCalculator
                    .Resolve(_activeChallenge, sessionSettings, sample)
                    .TargetTouchdownIasKts;
            }

            if (_flightTapeRecorder.IsActive)
                _flightTapeRecorder.Add(sample);

            _session?.Ingest(sample);
            UpdateLivePreview(force: false);
            SecondaryHud.Update(
                sample,
                _activeChallenge,
                sessionSettings,
                targetTouchdownIas,
                PreviewScorePercent,
                _session?.Phase ?? LandingPhase.Idle,
                _sim.IsConnected);
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
        ClearTouchdownImpactSummary();
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
        ClearTouchdownImpactSummary();
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

        ApplyTouchdownImpactSummary(result);
        _lastPreviewUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Final HUD: Peak G + touchdown VS (replaces the live IAS/GS/VS strip after settle).
    /// </summary>
    private void ApplyTouchdownImpactSummary(ScoreResult result)
    {
        var d = result.Diagnostics;
        var peakG = d.TouchdownRobustPeakG;
        if (peakG <= 0 && _session?.Snapshot is { } snap)
            peakG = snap.InitialImpact?.RobustPeakG ?? snap.PeakGForce;

        var vs = d.TouchdownVerticalSpeedFpm;
        if (vs == 0 && _session?.Snapshot is { VerticalSpeedAtTouchdownFpm: var snapVs } && snapVs != 0)
            vs = snapVs;

        HudPeakGDisplay = double.IsFinite(peakG) && peakG > 0
            ? $"{peakG:0.00} G"
            : "—";
        HudTouchdownVsDisplay = double.IsFinite(vs) && (vs != 0 || peakG > 1.0)
            ? $"{vs:0} FPM"
            : "—";
        ShowTouchdownImpactSummary = true;
    }

    private void ClearTouchdownImpactSummary()
    {
        ShowTouchdownImpactSummary = false;
        HudPeakGDisplay = "—";
        HudTouchdownVsDisplay = "—";
    }

    private void SetPreviewIssues(string line)
    {
        PreviewIssues = line ?? "";
        RaisePropertyChanged(nameof(HasPreviewIssues));
    }

    private void UpdateLivePreview(bool force)
    {
        var scoreEngine = CurrentScoreEngine;
        if (_session is null || _activeChallenge is null || scoreEngine is null)
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

            var preview = scoreEngine.EvaluatePreview(_activeChallenge, _session.Snapshot);
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
        var sessionSettings = CurrentSessionSettings;
        if (sessionSettings is not null)
        {
            var resolved = SpeedTargetCalculator.Resolve(_activeChallenge, sessionSettings, _lastTelemetry);
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
            sessionSettings?.ApproachPathMaxDistNm ?? 4.5);
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
        var maxNm = CurrentSessionSettings?.ApproachPathMaxDistNm ?? 4.5;
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
        var sessionSettings = CurrentSessionSettings;
        if (sessionSettings is null)
        {
            SpeedTargetInfo = "Optimal landing speed: —";
            return;
        }

        var (vapp, targetTd, source) = SpeedTargetCalculator.Resolve(challenge, sessionSettings, sample);
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

            if (state == SimConnectionState.Connected)
            {
                RequestShowHud?.Invoke();
                if (IsFreeMode)
                {
                    StopFreeInference(resetTarget: true);
                    DetachSession();
                    _activeChallenge = null;
                    PhaseLabel = "Detecting";
                    FreeAirportStatus = "Detecting airport and runway...";
                    _freeFlightCts = new CancellationTokenSource();
                    _freeInferenceTimer.Start();
                    _ = RunFreeInferenceAsync();
                }
            }
            else
            {
                SecondaryHud.SetDisconnected();
                if (!IsFreeMode) return;
                StopFreeInference(resetTarget: true);
                DetachSession();
                _activeChallenge = null;
                PhaseLabel = "Detecting";
                FreeAirportStatus = "Detecting · waiting for simulator connection";
            }
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

    private void RefreshFlightTapes()
    {
        var selectedPath = SelectedFlightTape?.Path;
        FlightTapes.Clear();
        foreach (var item in _flightTapes.List())
            FlightTapes.Add(item);

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            var match = FlightTapes.FirstOrDefault(t =>
                string.Equals(t.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                SelectedFlightTape = match;
        }

        TestingStatus = FlightTapes.Count == 0
            ? $"No flight tapes yet. Land and settle (GS below score threshold) — files appear in {FlightsFolderPath}"
            : $"{FlightTapes.Count} flight tape(s) in folder. Select one and evaluate.";
    }

    private void OpenFlightsFolder()
    {
        try
        {
            Directory.CreateDirectory(FlightsFolderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = FlightsFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Open flights folder failed: {ex.Message}");
            MessageBox.Show(
                $"Could not open flights folder:\n{ex.Message}",
                "Challenge Lab — Testing",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void BrowseAndEvaluateFlightTape()
    {
        if (!HasValidScoringConfiguration)
        {
            TestingStatus = "Scoring configuration is invalid — cannot evaluate.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open flight tape",
            Filter = "Flight tapes (*.json)|*.json|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(FlightsFolderPath)
                ? FlightsFolderPath
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;
        EvaluateFlightTapePath(dialog.FileName);
    }

    private void EvaluateSelectedFlightTape()
    {
        if (SelectedFlightTape is null) return;
        EvaluateFlightTapePath(SelectedFlightTape.Path);
    }

    private void EvaluateFlightTapePath(string path)
    {
        if (!HasValidScoringConfiguration || _evaluationKey is null)
        {
            TestingStatus = "Scoring configuration is invalid — cannot evaluate.";
            return;
        }

        IsEvaluatingFlightTape = true;
        TestingStatus = "Evaluating…";
        try
        {
            var tape = _flightTapes.Load(path);
            // Challenge tapes use the challenge key; free-flight tapes use free key when present.
            var baseKey = ResolveKeyForTape(tape);
            var replay = FlightTapeReplayer.Replay(tape, baseKey);
            var result = replay.Result;

            // Show result on Session-style summary without writing highscores/career.
            LastScore = result;
            ResultVisible = true;
            ApplyFinalPreview(result);

            var reportEntry = HighscoreEntryFromScoreResult(result);
            SelectedHighscore = null;
            RebuildLandingReport(reportEntry);

            var original = tape.OriginalScorePercent is null
                ? "no original score stored"
                : $"original live {tape.OriginalScorePercent:0.0}% {tape.OriginalGrade}";
            TestingStatus =
                $"Replay {result.ScoreDisplay} {result.Grade} · {result.Criteria.Count} metrics · " +
                $"{tape.Samples.Count} samples · {original} · key {result.EvaluationKeyId} v{result.EvaluationKeyVersion}";
            AppendLog(
                $"Testing replay: {System.IO.Path.GetFileName(path)} → {result.ScoreDisplay} {result.Grade} " +
                $"({original}) · ranked={result.IsRanked}");
            SelectedTab = TestingTabIndex;
        }
        catch (Exception ex)
        {
            TestingStatus = "Evaluate failed: " + ex.Message;
            AppendLog("Flight tape evaluate failed: " + ex);
            MessageBox.Show(
                $"Could not evaluate flight tape:\n{ex.Message}",
                "Challenge Lab — Testing",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsEvaluatingFlightTape = false;
        }
    }

    private LandingEvaluationKey ResolveKeyForTape(FlightTapeDocument tape)
    {
        var freeId = _freeEvaluationKeyLoad.Key?.Id;
        if (!string.IsNullOrWhiteSpace(freeId)
            && string.Equals(tape.EvaluationKeyId, freeId, StringComparison.OrdinalIgnoreCase)
            && _freeEvaluationKeyLoad.Key is { } freeKey
            && _freeEvaluationKeyLoad.IsValid)
        {
            return freeKey;
        }

        // Free-flight synthetic challenges usually use free key even if id missing on older tapes.
        if (tape.Challenge is not null
            && tape.Challenge.Id.StartsWith("free-", StringComparison.OrdinalIgnoreCase)
            && _freeEvaluationKeyLoad.Key is { } freeKey2
            && _freeEvaluationKeyLoad.IsValid)
        {
            return freeKey2;
        }

        return _evaluationKey
               ?? throw new InvalidOperationException("Challenge evaluation key is not loaded.");
    }

    /// <summary>Build a non-persisted highscore-shaped entry so Testing can reuse the report panel.</summary>
    private static HighscoreEntry HighscoreEntryFromScoreResult(ScoreResult result)
    {
        var criteria = result.Criteria.Select(criterion => new HighscoreCriterionDetail
        {
            Id = criterion.Id,
            DisplayName = criterion.DisplayName,
            ScorePercent = criterion.ScorePercent is null ? null : Math.Round(criterion.ScorePercent.Value, 1),
            RawValue = criterion.RawValue,
            Unit = criterion.Unit,
            Note = criterion.Note,
            Status = criterion.Status,
            UnavailableReason = criterion.UnavailableReason,
            PhaseId = criterion.PhaseId,
            PhaseDisplayName = criterion.PhaseDisplayName,
            PhaseImportancePercent = criterion.PhaseImportancePercent,
            PhaseWeightPercent = criterion.PhaseWeightPercent,
            MaxOverallPoints = criterion.MaxOverallPoints
        }).ToList();

        var phases = result.PhaseScores.Select(phase => new HighscorePhaseDetail
        {
            PhaseId = phase.PhaseId,
            DisplayName = phase.DisplayName,
            WeightPercent = phase.WeightPercent,
            ScorePercent = phase.ScorePercent,
            Used = phase.IsComplete
        }).ToList();

        return new HighscoreEntry
        {
            Id = Guid.NewGuid(),
            Utc = result.ScoredAtUtc,
            ChallengeId = result.ChallengeId,
            ChallengeTitle = result.ChallengeTitle + " (replay)",
            ScorePercent = result.ScorePercent ?? 0,
            Grade = result.Grade,
            Notes = result.Summary,
            ScoreBeforeGatesPercent = result.ScoreBeforeGatesPercent,
            GearUpPenaltyApplied = result.GearUpPenaltyApplied,
            FlapsPenaltyApplied = result.FlapsPenaltyApplied,
            Phases = phases,
            VerticalSpeedFpm = result.Diagnostics.TouchdownVerticalSpeedFpm,
            Criteria = criteria,
            EvaluationKeyId = result.EvaluationKeyId,
            EvaluationKeyVersion = result.EvaluationKeyVersion,
            ScoringProfileHash = result.ScoringProfileHash,
            RankedBucketId = result.RankedBucketId,
            Diagnostics = result.Diagnostics,
            LandingVisualization = result.LandingVisualization
        };
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
        StopFreeInference(resetTarget: true);
        DetachSession();
        _sim.StateChanged -= OnSimStateChanged;
        _sim.TelemetryReceived -= OnTelemetry;
        _sim.Dispose();
    }
}
