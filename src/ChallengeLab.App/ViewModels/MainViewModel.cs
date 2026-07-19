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
using MessageBoxResult = System.Windows.MessageBoxResult;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using ChallengeLab.Core.Career;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scenarios;
using ChallengeLab.Core.Scoring;
using ChallengeLab.Core.Snapshots;
using ChallengeLab.App.Controls.Aether;
using ChallengeLab.App.Controls.Hud;
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
    public const int StoreTabIndex = 5;

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
    private readonly RunwayReferenceResolver _runwayReferenceResolver;
    private ScoreEngine? _activeScoreEngine;
    private LandingSessionSettings? _activeSessionSettings;
    private readonly DispatcherTimer _reconnectTimer;
    private readonly DispatcherTimer _freeInferenceTimer;
    private readonly FreeFlightRunwayInference _freeInference = new();
    private readonly LandingGuidanceHold _landingGuidanceHold = new();
    private readonly FreeFlightInferenceLog _freeInferenceLog = new();
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
    private string _hudTip = "Free mode observes this flight and detects the runway from position and aircraft heading.";
    private string _phaseLabel = "Detecting";
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
    private readonly SnapshotStore _snapshotStore;
    private ObservableCollection<SnapshotListItem> _snapshots = new();
    private SnapshotListItem? _selectedSnapshot;
    private SnapshotDetailViewModel? _selectedSnapshotDetail;
    private string _snapshotNameInput = "";
    private string _storeStatus = "Save the current flight state, or load a stored one.";
    private bool _isCapturingSnapshot;
    private bool _isRestoringSnapshot;
    private bool _isRenamingSnapshot;
    private string _renameText = "";
    private bool _restoreWeatherEnabled = true;
    private bool _restoreAutopilotEnabled = true;
    private bool _autoResumeAfterRestore;
    private LandingReportViewModel? _landingReport;
    private string _reportStatus = "";
    private string _reportBodyText = "";
    private ObservableCollection<ReportMetricViewModel> _reportMetrics = new();
    private string _windowTitle = AppBuild.WindowTitleDefault;
    private HudOperatingMode _hudOperatingMode = HudOperatingMode.Free;
    private string _freeAirportStatus = "Detecting airport and runway...";
    private CancellationTokenSource? _freeFlightCts;
    private bool _freeInferenceBusy;
    private string _lastFreeInferenceError = "";
    private readonly Dictionary<string, AirportRunwayFacility> _freeAirportDetails =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _freeAirportDetailLoads = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<AirportFacility>? _freeAirportCatalog;
    private bool _freeGearDownActive;
    private FreeFlightTargetLock? _freeTargetLock;
    private FreeFlightTarget? _lastLoggedFreeCandidate;
    private string? _pendingFreeAircraftTitle;
    private DateTimeOffset? _pendingFreeAircraftTitleSince;
    private int _pendingFreeAircraftTitleSamples;
    private string _freeTargetMonitorStatus = "Runway data warming · lower gear to select an aligned runway";
    private bool _isSecondaryHudVisible;
    private bool _isFighterHudVisible;
    private bool _isAetherHudVisible;
    private long _fighterHudSequence;
    private long _aetherHudSequence;

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
        IRandomIndexProvider? careerRandom = null,
        OurAirportsRunwayCatalog? runwayCatalog = null,
        SnapshotStore? snapshotStore = null)
    {
        _sim = sim;
        _runwayReferenceResolver = new RunwayReferenceResolver(runwayCatalog);
        _configLoader = configLoader ?? new ConfigLoader();
        _highscores = highscores ?? new HighscoreStore();
        _landingTraces = new LandingTraceStore();
        _flightTapes = new FlightTapeStore();
        _snapshotStore = snapshotStore ?? new SnapshotStore();
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

        // Start is available from Free (default) or Normal — load path switches into Normal.
        StartChallengeCommand = new RelayCommand(async () => await StartChallengeAsync(), () =>
            SelectedChallenge is { Available: true } && HasValidScoringConfiguration && !IsLoading);
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
        ToggleFighterHudCommand = new RelayCommand(ToggleFighterHud);
        ToggleAetherHudCommand = new RelayCommand(ToggleAetherHud);
        RefreshFlightTapesCommand = new RelayCommand(RefreshFlightTapes);
        EvaluateSelectedFlightTapeCommand = new RelayCommand(
            EvaluateSelectedFlightTape,
            () => SelectedFlightTape is not null && HasValidScoringConfiguration && !IsEvaluatingFlightTape);
        BrowseFlightTapeCommand = new RelayCommand(
            BrowseAndEvaluateFlightTape,
            () => HasValidScoringConfiguration && !IsEvaluatingFlightTape);
        OpenFlightsFolderCommand = new RelayCommand(OpenFlightsFolder);
        SaveSnapshotCommand = new RelayCommand(async () => await SaveSnapshotAsync(), CanSaveSnapshot);
        LoadSnapshotCommand = new RelayCommand(async () => await LoadSelectedSnapshotAsync(), () =>
            SelectedSnapshot is not null && IsConnected && !IsRestoringSnapshot
            && !IsCapturingSnapshot && !IsLoading);
        RenameSnapshotCommand = new RelayCommand(BeginRenameSnapshot, () =>
            SelectedSnapshot is not null && !IsRestoringSnapshot);
        ConfirmRenameSnapshotCommand = new RelayCommand(ConfirmRenameSnapshot, () => IsRenamingSnapshot);
        CancelRenameSnapshotCommand = new RelayCommand(() =>
        {
            IsRenamingSnapshot = false;
            RenameText = "";
        });
        DeleteSnapshotCommand = new RelayCommand(DeleteSelectedSnapshot, () =>
            SelectedSnapshot is not null && !IsRestoringSnapshot);
        RefreshSnapshotsCommand = new RelayCommand(RefreshSnapshots);
        OpenSnapshotsFolderCommand = new RelayCommand(OpenSnapshotsFolder);
        ResumeNowCommand = new RelayCommand(() => _sim.ResumeFlight(), () => IsConnected);
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
        AppendLog("HUD mode: Free (default) — observing current flight; no simulator state changes until a challenge is loaded.");
        LogEvaluationKeyStatus();
        LogFreeEvaluationKeyStatus();
        RefreshHighscores();
        RefreshFlightTapes();
        RefreshSnapshots();
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
    /// <summary>Open/close the approach overlay HUD window (wired in MainWindow).</summary>
    public event Action? RequestToggleFighterHud;
    /// <summary>Push a presentation frame to the fighter HUD when it is open.</summary>
    internal event Action<HudPresentationFrame?>? FighterHudPresentation;
    /// <summary>Open/close the independent Aether (HUD 2) overlay (wired in MainWindow).</summary>
    public event Action? RequestToggleAetherHud;
    /// <summary>Push a presentation snapshot to the Aether overlay when it is open.</summary>
    internal event Action<AetherSnapshot?>? AetherPresentation;
    public event Action<ScoreResult>? ScoreComputed;

    public SecondaryHudViewModel SecondaryHud { get; }

    public bool IsSecondaryHudVisible
    {
        get => _isSecondaryHudVisible;
        private set => SetProperty(ref _isSecondaryHudVisible, value);
    }

    public void SetSecondaryHudVisible(bool visible) => IsSecondaryHudVisible = visible;

    public bool IsFighterHudVisible
    {
        get => _isFighterHudVisible;
        private set => SetProperty(ref _isFighterHudVisible, value);
    }

    public void SetFighterHudVisible(bool visible)
    {
        if (IsFighterHudVisible == visible)
            return;

        IsFighterHudVisible = visible;
        if (visible)
        {
            AppendLog("Approach HUD on — overlay tracks the active MSFS window.");
            PushFighterHudPresentation();
        }
        else
        {
            AppendLog("Approach HUD off.");
            FighterHudPresentation?.Invoke(null);
        }
    }

    private void ToggleFighterHud()
    {
        RequestToggleFighterHud?.Invoke();
    }

    public bool IsAetherHudVisible
    {
        get => _isAetherHudVisible;
        private set => SetProperty(ref _isAetherHudVisible, value);
    }

    public void SetAetherHudVisible(bool visible)
    {
        if (IsAetherHudVisible == visible)
            return;

        IsAetherHudVisible = visible;
        if (visible)
        {
            AppendLog("Aether HUD on — independent approach overlay on the MSFS window.");
            PushAetherPresentation();
        }
        else
        {
            AppendLog("Aether HUD off.");
            AetherPresentation?.Invoke(null);
        }
    }

    private void ToggleAetherHud()
    {
        RequestToggleAetherHud?.Invoke();
    }

    public bool HasValidScoringConfiguration => _scoreEngine is not null && _sessionSettings is not null;
    public bool HasValidFreeScoringConfiguration =>
        _freeScoreEngine is not null && _freeSessionSettings is not null;
    internal FreeFlightTargetLock? FreeTargetLockForDiagnostics => _freeTargetLock;
    public string ConfigurationStatus { get; }

    private ScoreEngine? CurrentScoreEngine =>
        _activeScoreEngine ?? (IsFreeMode ? _freeScoreEngine : _scoreEngine);
    private LandingSessionSettings? CurrentSessionSettings =>
        _activeSessionSettings ?? (IsFreeMode ? _freeSessionSettings : _sessionSettings);
    private ChallengeConfig? CurrentGuidanceChallenge =>
        _activeChallenge ?? (IsFreeMode ? _freeTargetLock?.GuidanceChallenge : null);

    public HudOperatingMode OperatingMode
    {
        get => _hudOperatingMode;
        private set
        {
            if (_hudOperatingMode == value) return;
            SetProperty(ref _hudOperatingMode, value);
            RaisePropertyChanged(nameof(IsNormalMode));
            RaisePropertyChanged(nameof(IsFreeMode));
            RaisePropertyChanged(nameof(CleanActionLabel));
            RaisePropertyChanged(nameof(CleanActionToolTip));
            RaiseModeCommandStates();
        }
    }

    public bool IsNormalMode => OperatingMode == HudOperatingMode.Normal;
    public bool IsFreeMode => OperatingMode == HudOperatingMode.Free;
    public string CleanActionLabel => IsFreeMode ? "Reacquire" : "Clear";
    public string CleanActionToolTip => IsFreeMode
        ? "Release this Free Mode attempt and reacquire the best aligned runway. Every nearby airport remains eligible."
        : "Clear this attempt's landing metrics and re-arm scoring in place.";

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
    /// <summary>Reacquire the Free runway, or wipe Normal/Career metrics without a re-spawn.</summary>
    public ICommand CleanMetricsCommand { get; }
    public ICommand NormalModeCommand { get; }
    public ICommand FreeModeCommand { get; }
    /// <summary>HUD Go: SET PAUSE OFF (resume flight after Start/Restart hold).</summary>
    public ICommand GoCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DismissResultCommand { get; }
    public ICommand ClearHighscoreSelectionCommand { get; }
    public ICommand ToggleSecondaryHudCommand { get; }
    /// <summary>Show or hide the fighter-style sim overlay HUD.</summary>
    public ICommand ToggleFighterHudCommand { get; }
    /// <summary>Show or hide the independent Aether (HUD 2) overlay.</summary>
    public ICommand ToggleAetherHudCommand { get; }
    public ICommand OpenMenuCommand { get; }
    public ICommand RefreshFlightTapesCommand { get; }
    public ICommand EvaluateSelectedFlightTapeCommand { get; }
    public ICommand BrowseFlightTapeCommand { get; }
    public ICommand OpenFlightsFolderCommand { get; }
    public ICommand SaveSnapshotCommand { get; }
    public ICommand LoadSnapshotCommand { get; }
    public ICommand RenameSnapshotCommand { get; }
    public ICommand ConfirmRenameSnapshotCommand { get; }
    public ICommand CancelRenameSnapshotCommand { get; }
    public ICommand DeleteSnapshotCommand { get; }
    public ICommand RefreshSnapshotsCommand { get; }
    public ICommand OpenSnapshotsFolderCommand { get; }
    public ICommand ResumeNowCommand { get; }

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

    /// <summary>STORE tab list. Rebuilds assign a NEW collection (see AGENTS.md WPF lessons).</summary>
    public ObservableCollection<SnapshotListItem> Snapshots
    {
        get => _snapshots;
        private set => SetProperty(ref _snapshots, value);
    }

    public SnapshotListItem? SelectedSnapshot
    {
        get => _selectedSnapshot;
        set
        {
            if (ReferenceEquals(_selectedSnapshot, value)) return;
            SetProperty(ref _selectedSnapshot, value);
            IsRenamingSnapshot = false;
            LoadSelectedSnapshotDetail();
            (LoadSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RenameSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Formatted key state fields for the STORE detail column.</summary>
    public SnapshotDetailViewModel? SelectedSnapshotDetail
    {
        get => _selectedSnapshotDetail;
        private set
        {
            if (ReferenceEquals(_selectedSnapshotDetail, value)) return;
            SetProperty(ref _selectedSnapshotDetail, value);
            RaisePropertyChanged(nameof(HasSelectedSnapshotDetail));
        }
    }

    public bool HasSelectedSnapshotDetail => SelectedSnapshotDetail is not null;

    public string SnapshotNameInput
    {
        get => _snapshotNameInput;
        set => SetProperty(ref _snapshotNameInput, value);
    }

    public string StoreStatus
    {
        get => _storeStatus;
        private set => SetProperty(ref _storeStatus, value);
    }

    public bool IsCapturingSnapshot
    {
        get => _isCapturingSnapshot;
        private set
        {
            if (_isCapturingSnapshot == value) return;
            SetProperty(ref _isCapturingSnapshot, value);
            (SaveSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LoadSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsRestoringSnapshot
    {
        get => _isRestoringSnapshot;
        private set
        {
            if (_isRestoringSnapshot == value) return;
            SetProperty(ref _isRestoringSnapshot, value);
            (SaveSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LoadSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (RenameSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (DeleteSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsRenamingSnapshot
    {
        get => _isRenamingSnapshot;
        private set
        {
            if (_isRenamingSnapshot == value) return;
            SetProperty(ref _isRenamingSnapshot, value);
            (ConfirmRenameSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public string RenameText
    {
        get => _renameText;
        set => SetProperty(ref _renameText, value);
    }

    public bool RestoreWeatherEnabled
    {
        get => _restoreWeatherEnabled;
        set => SetProperty(ref _restoreWeatherEnabled, value);
    }

    public bool RestoreAutopilotEnabled
    {
        get => _restoreAutopilotEnabled;
        set => SetProperty(ref _restoreAutopilotEnabled, value);
    }

    public bool AutoResumeAfterRestore
    {
        get => _autoResumeAfterRestore;
        set => SetProperty(ref _autoResumeAfterRestore, value);
    }

    public string SnapshotsFolderPath => _snapshotStore.DirectoryPath;

    /// <summary>Confirmation hook (default MessageBox) — injectable so tests can intercept.</summary>
    public Func<string, string, bool> ConfirmAction { get; set; } = (message, title) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
        == MessageBoxResult.Yes;

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
            (SaveSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LoadSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (ResumeNowCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
            (SaveSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
            (LoadSnapshotCommand as RelayCommand)?.RaiseCanExecuteChanged();
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
            var csvMatches = 0;
            foreach (var challenge in challenges)
                if (_runwayReferenceResolver.TryApplyCsv(challenge.Runway))
                    csvMatches++;
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
            if (_runwayReferenceResolver.Catalog.IsAvailable)
            {
                AppendLog(
                    $"OurAirports {_runwayReferenceResolver.Catalog.SnapshotId}: " +
                    $"{_runwayReferenceResolver.Catalog.RunwayEndCount} indexed ends Â· " +
                    $"{csvMatches}/{challenges.Count} challenge runway matches.");
            }
            else
            {
                AppendLog("OurAirports unavailable: " + _runwayReferenceResolver.Catalog.LoadError);
            }
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
        ReleaseFreeTargetLock("operating mode changed", _lastTelemetry);
        StopFreeInference(resetTarget: true);
        _freeGearDownActive = false;
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
            HudTip = "Free mode observes this flight and detects the runway from position and aircraft heading.";
            SpeedTargetInfo = "Optimal landing speed: —";
            FreeAirportStatus = HasValidFreeScoringConfiguration
                ? "Detecting airport and runway..."
                : "Detecting unavailable · generic scoring profile is invalid";
            ShowFreeRunwaySearch("Runway data warming · lower gear to select an aligned runway");
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
        _freeAirportDetailLoads.Clear();
        if (resetTarget)
        {
            _freeInference.Reset();
            _lastLoggedFreeCandidate = null;
        }
    }

    private async Task RunFreeInferenceAsync()
    {
        if (!IsFreeMode || !_sim.IsConnected || _lastTelemetry is null
            || _freeInferenceBusy || _session is not null)
            return;

        var cts = _freeFlightCts;
        if (cts is null || cts.IsCancellationRequested)
            return;

        _freeInferenceBusy = true;
        try
        {
            var sample = _lastTelemetry;
            if (_freeTargetLock is not null)
            {
                EvaluateLockedFreeTarget(sample, cts);
                _lastFreeInferenceError = "";
                return;
            }

            var airports = _freeAirportCatalog ?? await _sim.GetAirportsAsync(cts.Token);
            if (!IsCurrentFreeScan(cts)) return;
            _freeAirportCatalog = airports;

            var nearby = _freeInference.RankNearbyAirports(sample, airports);
            if (nearby.Count == 0)
            {
                FreeAirportStatus = "No airport within 30 NM · scanning will continue";
                PhaseLabel = _freeGearDownActive ? "Targeting" : "Waiting for gear";
                ShowFreeRunwaySearch(FreeAirportStatus);
                return;
            }

            StartAirportDetailLoads(nearby, cts);
            EvaluateFreeInference(sample, nearby, cts, advanceStability: true);

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
            ShowFreeRunwaySearch(FreeAirportStatus);
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

    private void StartAirportDetailLoads(
        IReadOnlyList<AirportDistance> nearby,
        CancellationTokenSource cts)
    {
        foreach (var item in nearby)
        {
            if (!IsCurrentFreeScan(cts))
                break;
            var key = FreeAirportCacheKey(item.Airport);
            if (_freeAirportDetails.ContainsKey(key) || !_freeAirportDetailLoads.Add(key))
                continue;
            _ = LoadAirportDetailIncrementallyAsync(item.Airport, key, cts);
        }
    }

    private async Task LoadAirportDetailIncrementallyAsync(
        AirportFacility airport,
        string key,
        CancellationTokenSource cts)
    {
        try
        {
            var detail = await _sim.GetAirportRunwaysAsync(airport, cts.Token);
            if (!IsCurrentFreeScan(cts)) return;
            _freeAirportDetails[key] = detail;

            // Paint the first useful result as soon as its packet arrives. Multiple detail
            // responses in one scan must not advance the one-second stability counter.
            if (_lastTelemetry is { } sample && _freeAirportCatalog is { } airports)
            {
                var nearby = _freeInference.RankNearbyAirports(sample, airports);
                EvaluateFreeInference(sample, nearby, cts, advanceStability: false);
            }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (IsCurrentFreeScan(cts))
                AppendLog($"Facility {airport.Icao} unavailable: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_freeFlightCts, cts))
                _freeAirportDetailLoads.Remove(key);
        }
    }

    private void EvaluateFreeInference(
        TelemetrySample sample,
        IReadOnlyList<AirportDistance> nearby,
        CancellationTokenSource cts,
        bool advanceStability)
    {
        if (!IsCurrentFreeScan(cts) || _session is not null)
            return;

        var details = nearby
            .Select(item => _freeAirportDetails.GetValueOrDefault(FreeAirportCacheKey(item.Airport)))
            .Where(detail => detail is not null)
            .Cast<AirportRunwayFacility>()
            .ToList();
        var inference = _freeInference.Update(
            sample,
            nearby,
            details,
            advanceStability);
        LogCandidateTransition(sample, inference.ProvisionalTarget);
        var closestText = inference.NearestAirport is { } nearest
            ? $"Closest field {nearest.Airport.Icao} · {nearest.DistanceNm:0.0} NM (reference only)"
            : "Closest field unavailable";
        var progressText = $"runway data {details.Count}/{nearby.Count}";

        if (!_freeGearDownActive)
        {
            PhaseLabel = "Waiting for gear";
            FreeAirportStatus = $"{closestText} · {progressText} · lower gear to select an aligned runway";
            ShowFreeRunwaySearch(FreeAirportStatus);
            return;
        }

        if (inference.ProvisionalTarget is not { } provisional)
        {
            PhaseLabel = "Targeting";
            FreeAirportStatus = $"{closestText} · {progressText} · scanning aligned runways";
            ShowFreeRunwaySearch(FreeAirportStatus);
            return;
        }

        var policy = _freeEvaluationKeyLoad.Key?.FreeMode?.EvaluationStart
                     ?? new FreeFlightEvaluationStartPolicy();
        var start = FreeFlightEvaluationStartCalculator.Calculate(sample, provisional.Runway, policy);
        var stableText = inference.StableTarget is not null
            ? "confirmed"
            : $"confirming {inference.StableSamples}/{_freeInference.Settings.StableSamplesToConfirm}";
        var timingText = start.SecondsUntilStart is > 0
            ? $"evaluation in {Math.Ceiling(start.SecondsUntilStart.Value):0}s at {start.TriggerDistanceNm:0.0} NM"
            : start.IsPastPlannedStart
                ? "evaluation start reached"
                : $"evaluation starts at {start.TriggerDistanceNm:0.0} NM";
        var candidateText =
            $"Best aligned {provisional.Runway.Airport.Icao} RWY {provisional.Runway.RunwayId}" +
            $" · {FormatFreePathAngle(provisional.Runway)} · {start.CurrentApproachDistanceNm:0.0} NM";

        PhaseLabel = "Targeting";
        FreeAirportStatus = $"{candidateText} · {stableText} · {timingText} · {progressText}";
        HudTip = $"{candidateText} · selected by runway alignment; target may refine until evaluation begins.";
        _freeTargetMonitorStatus = $"{stableText} · {timingText} · {progressText}";
        SecondaryHud.ShowProvisionalFreeTarget(
            provisional.Runway.Airport.Icao,
            provisional.Runway.RunwayId,
            _freeTargetMonitorStatus);

        var target = inference.StableTarget;
        var lateAcquisition = false;
        if (target is null
            && start.IsReady
            && _freeInference.IsInsideConfirmationEnvelope(provisional))
        {
            target = provisional;
            lateAcquisition = true;
        }

        if (target is null)
            return;

        AcquireFreeTargetLock(target, sample, lateAcquisition);
        EvaluateLockedFreeTarget(sample, cts);
    }

    private static string FreeAirportCacheKey(AirportFacility airport) =>
        $"{airport.Icao}|{airport.Region}";

    private void ShowFreeRunwaySearch(string status)
    {
        if (_freeTargetLock is { } targetLock)
        {
            SecondaryHud.ShowLockedFreeTarget(
                targetLock.Runway.AirportIcao,
                targetLock.Runway.RunwayId,
                _freeTargetMonitorStatus);
            return;
        }

        _freeTargetMonitorStatus = status;
        SecondaryHud.ShowFreeRunwaySearch(_freeGearDownActive, status);
    }

    private void AcquireFreeTargetLock(
        FreeFlightTarget target,
        TelemetrySample sample,
        bool lateAcquisition)
    {
        if (_freeTargetLock is not null)
            return;

        _freeTargetLock = FreeFlightTargetLock.Acquire(
            target,
            sample,
            _runwayReferenceResolver,
            lateAcquisition);
        _landingGuidanceHold.Reset();
        var airport = _freeTargetLock.Runway.AirportIcao;
        var runway = _freeTargetLock.Runway.RunwayId;
        PhaseLabel = "Runway locked";
        HudTip = $"Locked {airport} RWY {runway} · retained until Reacquire or a new flight.";
        _freeTargetMonitorStatus = "locked · evaluation timing active";
        SecondaryHud.ShowLockedFreeTarget(
            airport,
            runway,
            _freeTargetMonitorStatus);
        AppendFreeInferenceLog(
            "lock-acquired",
            lateAcquisition ? "late acquisition inside evaluation envelope" : "stable target confirmed",
            sample,
            target);
        AppendLog(
            $"Free target locked: {airport} RWY {runway} · " +
            $"distance {target.ThresholdDistanceNm:0.00} NM · heading {target.HeadingErrorDeg:0.0}° · " +
            $"cross-track {target.CrossTrackNm:0.00} NM. Automatic candidate changes are now ignored.");
    }

    private void ReleaseFreeTargetLock(string reason, TelemetrySample? sample)
    {
        var targetLock = _freeTargetLock;
        if (targetLock is null)
        {
            _landingGuidanceHold.Reset();
            ResetPendingFreeAircraftChange();
            return;
        }

        if (sample is not null)
            AppendFreeInferenceLog("lock-released", reason, sample, targetLock.Target);
        AppendLog(
            $"Free target released: {targetLock.Runway.AirportIcao} RWY {targetLock.Runway.RunwayId} · {reason}.");
        _freeTargetLock = null;
        _landingGuidanceHold.Reset();
        ResetPendingFreeAircraftChange();
    }

    private void LogCandidateTransition(TelemetrySample sample, FreeFlightTarget? candidate)
    {
        var previousKey = _lastLoggedFreeCandidate?.Runway.Key;
        var currentKey = candidate?.Runway.Key;
        if (string.Equals(previousKey, currentKey, StringComparison.Ordinal))
            return;

        var reason = candidate is null
            ? "no eligible aligned runway in the current scan"
            : _lastLoggedFreeCandidate is null
                ? "eligible candidate acquired"
                : $"candidate changed from {previousKey}";
        AppendFreeInferenceLog(
            candidate is null ? "candidate-lost" : "candidate-changed",
            reason,
            sample,
            candidate ?? _lastLoggedFreeCandidate);
        _lastLoggedFreeCandidate = candidate;
    }

    private void AppendFreeInferenceLog(
        string eventName,
        string reason,
        TelemetrySample sample,
        FreeFlightTarget? candidate)
    {
        double? trackError = null;
        if (candidate is not null
            && sample.GroundTrackTrueDeg is { } track
            && double.IsFinite(track))
        {
            trackError = Math.Abs(LandingMonitorCalculator.NormalizeSignedDegrees(
                track - candidate.Runway.HeadingTrueDeg));
        }

        _freeInferenceLog.Append(new FreeFlightInferenceLogEntry(
            sample.Timestamp,
            eventName,
            reason,
            candidate?.Runway.Key,
            _freeTargetLock?.Key,
            candidate?.ThresholdDistanceNm,
            candidate?.HeadingErrorDeg,
            trackError,
            candidate?.CrossTrackNm,
            sample.Latitude,
            sample.Longitude,
            sample.AltitudeFeet,
            sample.AirspeedKts,
            sample.GroundSpeedKts,
            sample.GearHandlePosition,
            _sim.IsConnected,
            sample.AircraftTitle));
    }

    private void EvaluateLockedFreeTarget(
        TelemetrySample sample,
        CancellationTokenSource cts)
    {
        var targetLock = _freeTargetLock;
        if (targetLock is null || !IsCurrentFreeScan(cts) || _session is not null)
            return;

        var policy = _freeEvaluationKeyLoad.Key?.FreeMode?.EvaluationStart
                     ?? new FreeFlightEvaluationStartPolicy();
        var start = FreeFlightEvaluationStartCalculator.Calculate(
            sample,
            targetLock.Target.Runway,
            policy);
        var timingText = start.SecondsUntilStart is > 0
            ? $"evaluation in {Math.Ceiling(start.SecondsUntilStart.Value):0}s at {start.TriggerDistanceNm:0.0} NM"
            : start.IsPastPlannedStart
                ? "evaluation start reached"
                : $"evaluation starts at {start.TriggerDistanceNm:0.0} NM";
        var airport = targetLock.Runway.AirportIcao;
        var runway = targetLock.Runway.RunwayId;
        _freeTargetMonitorStatus = $"locked · {timingText}";
        PhaseLabel = "Runway locked";
        FreeAirportStatus =
            $"Locked {airport} RWY {runway} · {FormatFreePathAngle(targetLock.Target.Runway)} · {timingText}";
        SecondaryHud.ShowLockedFreeTarget(
            airport,
            runway,
            _freeTargetMonitorStatus);

        if (!start.IsReady)
            return;

        if (ArmFreeFlightSession(
                targetLock.Target,
                sample,
                cts.Token,
                targetLock.WasLateAcquisition))
        {
            StopFreeInference(resetTarget: false);
        }
    }

    private bool ArmFreeFlightSession(
        FreeFlightTarget target,
        TelemetrySample sample,
        CancellationToken cancellationToken,
        bool lateAcquisition = false)
    {
        if (_freeSessionSettings is null || _freeScoreEngine is null)
        {
            PhaseLabel = "Detecting";
            HudTip = "Free scoring profile is unavailable. Check the Session log.";
            return false;
        }

        var airport = target.Runway.Airport.Icao.Trim().ToUpperInvariant();
        var runway = target.Runway.RunwayId.Trim().ToUpperInvariant();
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsFreeMode || _session is not null)
            return false;

        if (string.IsNullOrWhiteSpace(sample.AircraftTitle))
        {
            PhaseLabel = "Detecting";
            HudTip = "Runway locked · waiting for the simulator to identify the aircraft.";
            SpeedTargetInfo = "Optimal landing speed: — · waiting for aircraft TITLE";
            return false;
        }

        var targetLock = _freeTargetLock;
        if (targetLock is not null
            && !string.Equals(targetLock.Key, target.Runway.Key, StringComparison.Ordinal))
        {
            AppendFreeInferenceLog(
                "arm-rejected",
                $"locked runway {targetLock.Key} cannot be replaced by {target.Runway.Key}",
                sample,
                target);
            AppendLog(
                $"Free arm ignored: {target.Runway.Key} cannot replace locked runway {targetLock.Key}. " +
                "Use Reacquire or Clean to release it first.");
            return false;
        }

        if (targetLock is null)
        {
            AcquireFreeTargetLock(target, sample, lateAcquisition);
            targetLock = _freeTargetLock;
        }

        var lockedTitle = targetLock?.GuidanceChallenge.AircraftTitles.FirstOrDefault();
        var challenge = targetLock is not null
                        && !string.IsNullOrWhiteSpace(lockedTitle)
                        && string.Equals(
                            lockedTitle.Trim(),
                            sample.AircraftTitle.Trim(),
                            StringComparison.OrdinalIgnoreCase)
            ? targetLock.GuidanceChallenge
            : FreeFlightChallengeFactory.Create(target, sample, _runwayReferenceResolver);
        if (targetLock is not null && !ReferenceEquals(challenge, targetLock.GuidanceChallenge))
            _freeTargetLock = targetLock with { GuidanceChallenge = challenge };
        var aimingStartFeet = TouchdownPointCalculator.ResolveAimingMarkerFeet(challenge.Runway);
        var idealNearFeet = aimingStartFeet + TouchdownPointCalculator.DefaultIdealNearOffsetFeet;
        var idealFarFeet = aimingStartFeet + TouchdownPointCalculator.DefaultIdealFarOffsetFeet;
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
        HudTip =
            $"Locked {airport} RWY {runway} · aiming blocks {aimingStartFeet:0} ft · " +
            $"ideal {idealNearFeet:0}-{idealFarFeet:0} ft · scoring this landing." +
            (lateAcquisition ? " Late acquisition: scoring began as soon as runway data arrived." : "");
        FreeAirportStatus =
            $"Locked {airport} RWY {runway} · {FormatFreePathAngle(target.Runway)}" +
            (lateAcquisition ? " · late acquisition, evaluation started now" : " · evaluation active");
        LastScore = null;
        ResultVisible = false;
        SetPreviewPerfect("free flight · runway locked · unmeasured metrics assumed 100%");
        UpdateSpeedTargetInfo(challenge, sample, sample.AirspeedKts);
        (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
        AppendLog(
            $"Free armed{(lateAcquisition ? " (late acquisition)" : "")}: " +
            $"{airport} RWY {runway} · {FormatFreePathAngle(target.Runway)} · " +
            $"{target.ThresholdDistanceNm:0.0} NM · " +
            $"heading error {target.HeadingErrorDeg:0.0}° · cross-track {target.CrossTrackNm:0.00} NM · " +
            $"aiming marker start {aimingStartFeet:0} ft · " +
            $"ideal band {idealNearFeet:0}-{idealFarFeet:0} ft · " +
            $"source {challenge.Runway.RunwayDataSource}/{challenge.Runway.AimingMarkerConfidence} · " +
            $"gear gate={(challenge.RequireGearDown ? "on" : "not applicable")} · " +
            $"capabilities frozen ({challenge.FreeFlightCapabilities?.GateDecisions.Count ?? 0} gate decisions).");
        return true;
    }

    /// <summary>Compact free-mode path angle for HUD strip (e.g. "6.65° catalog").</summary>
    private static string FormatFreePathAngle(RunwayEndFacility runway)
    {
        var deg = RunwayPathGeometry.SanitizeGlideslopeDeg(runway.GlideslopeDeg);
        var source = string.IsNullOrWhiteSpace(runway.GlideslopeSource)
            ? GlideslopeAngleResolver.SourceDefault
            : runway.GlideslopeSource.Trim();
        return $"{deg:0.##}° {source}";
    }

    private async Task StartChallengeAsync()
    {
        if (SelectedChallenge is null || !SelectedChallenge.Available) return;

        if (!EnsureSimulatorConnected()) return;

        // Leave Free (or any non-challenge HUD workflow) only when a challenge is actually loaded.
        if (!IsNormalMode)
            await SetHudOperatingModeAsync(HudOperatingMode.Normal);

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

        await ResolveRunwayReferenceAsync(challenge);

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

    private async Task ResolveRunwayReferenceAsync(ChallengeConfig challenge)
    {
        if (_runwayReferenceResolver.TryApplyCsv(challenge.Runway))
            return;

        if (_sim.IsConnected)
        {
            try
            {
                var airportId = OurAirportsRunwayCatalog.NormalizeAirport(challenge.Runway.AirportIcao);
                var runwayId = OurAirportsRunwayCatalog.NormalizeRunway(challenge.Runway.RunwayId);
                var airports = await _sim.GetAirportsAsync(CancellationToken.None);
                foreach (var airport in airports.Where(candidate =>
                             string.Equals(
                                 OurAirportsRunwayCatalog.NormalizeAirport(candidate.Icao),
                                 airportId,
                                 StringComparison.Ordinal)))
                {
                    var detail = await _sim.GetAirportRunwaysAsync(airport, CancellationToken.None);
                    var runwayEnd = RunwayFacilityGeometry.BuildEnds(detail).FirstOrDefault(candidate =>
                        string.Equals(
                            OurAirportsRunwayCatalog.NormalizeRunway(candidate.RunwayId),
                            runwayId,
                            StringComparison.Ordinal));
                    if (runwayEnd is null) continue;

                    ApplySimulatorRunway(challenge.Runway, runwayEnd);
                    AppendLog(
                        $"Runway reference fallback: {airportId} RWY {runwayId} from SimConnect.");
                    return;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"Runway reference SimConnect fallback failed: {ex.Message}");
            }
        }

        if (string.IsNullOrWhiteSpace(challenge.Runway.RunwayDataSource))
            challenge.Runway.RunwayDataSource = "Stored challenge geometry";
        RunwayReferenceResolver.ApplyAimingPoint(
            challenge.Runway,
            challenge.Runway.RunwayDataSource,
            "Low");
    }

    private static void ApplySimulatorRunway(RunwayConfig destination, RunwayEndFacility source)
    {
        destination.CountryCode = source.CountryCode;
        destination.ThresholdLatitude = source.ThresholdLatitude;
        destination.ThresholdLongitude = source.ThresholdLongitude;
        destination.ElevationFeet = source.ElevationFeet;
        destination.HeadingTrueDeg = source.HeadingTrueDeg;
        destination.LengthM = source.LengthMeters;
        destination.WidthM = source.WidthMeters;
        destination.GlideslopeDeg = source.GlideslopeDeg;
        destination.GlideslopeSource = source.GlideslopeSource;
        destination.DisplacedThresholdM = source.DisplacedThresholdMeters;
        destination.LandingDistanceAvailableM = source.LandingDistanceAvailableMeters;
        destination.RunwayDataSource = "SimConnect";
        destination.RunwayDataSnapshotId = "";
        RunwayReferenceResolver.ApplyAimingPoint(destination, "SimConnect", "Medium");
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
    /// HUD action: Free Mode releases and reacquires the best aligned runway; Normal/Career
    /// zero landing metrics at this moment. No simulator state is changed.
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
            ReleaseFreeTargetLock("manual Reacquire", _lastTelemetry);
            _activeChallenge = null;
            LastScore = null;
            ResultVisible = false;
            StopFreeInference(resetTarget: true);
            PhaseLabel = _freeGearDownActive ? "Targeting" : "Waiting for gear";
            HudTip = "Reacquiring best aligned runway from your current position · all nearby airports eligible.";
            FreeAirportStatus = _freeGearDownActive
                ? "Reacquiring best aligned runway · all nearby airports eligible"
                : "Reacquire requested · runway data warming; lower gear to select an aligned runway";
            ShowFreeRunwaySearch(FreeAirportStatus);
            SpeedTargetInfo = "Optimal landing speed: —";
            _freeFlightCts = new CancellationTokenSource();
            _freeInferenceTimer.Start();
            _ = RunFreeInferenceAsync();
            AppendLog("Free Reacquire: attempt and runway released; all nearby airports remain eligible; detection restarted in place.");
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
            var previousSample = _lastTelemetry;
            _lastTelemetry = sample;
            // Session tab keeps the strip; scored HUD swaps to Peak G / touchdown VS cards.
            LiveStats =
                $"IAS {sample.AirspeedKts:0} kt  ·  GS {sample.GroundSpeedKts:0} kt  ·  " +
                $"VS {sample.VerticalSpeedFpm:0} fpm  ·  Bank {sample.BankDeg:0.0}°  ·  " +
                $"Wind {sample.WindDirectionDeg:000}/{sample.WindVelocityKts:0}kt  ·  " +
                $"{(sample.SimOnGround ? "GND" : "AIR")}";

            if (TryHandleFreeFlightPositionJump(previousSample, sample))
                return;
            HandleFreeDiscoveryGearState(sample);
            if (TryHandleFreeFlightAircraftChange(sample))
                return;

            double? targetTouchdownIas = null;
            var sessionSettings = CurrentSessionSettings;
            var guidanceChallenge = CurrentGuidanceChallenge;
            if (guidanceChallenge is not null && sessionSettings is not null)
            {
                UpdateSpeedTargetInfo(guidanceChallenge, sample, sample.AirspeedKts);
                targetTouchdownIas = SpeedTargetCalculator
                    .Resolve(guidanceChallenge, sessionSettings, sample)
                    .TargetTouchdownIasKts;
            }

            var rawGuidance = LandingMonitorCalculator.Calculate(
                sample,
                guidanceChallenge?.Runway,
                targetTouchdownIas,
                sessionSettings?.ApproachPathMinDistNm ?? .2,
                sessionSettings?.ApproachPathMaxDistNm ?? 4.5);
            var sharedGuidance = _landingGuidanceHold.Update(
                sample,
                guidanceChallenge?.Runway,
                rawGuidance,
                sessionSettings?.FlareAglFeet ?? 50);

            if (_flightTapeRecorder.IsActive)
                _flightTapeRecorder.Add(sample);

            _session?.Ingest(sample);
            UpdateLivePreview(force: false);
            SecondaryHud.Update(
                sample,
                guidanceChallenge,
                sessionSettings,
                targetTouchdownIas,
                PreviewScorePercent,
                _session?.Phase ?? LandingPhase.Idle,
                _sim.IsConnected,
                sharedGuidance,
                evaluationArmed: _session is not null);
            if (IsFighterHudVisible)
                PushFighterHudPresentation(sample, sharedGuidance: sharedGuidance);
            if (IsAetherHudVisible)
                PushAetherPresentation(sample, sharedGuidance: sharedGuidance);
            if (IsFreeMode && _session is null)
            {
                if (_freeTargetLock is { } targetLock)
                {
                    SecondaryHud.ShowLockedFreeTarget(
                        targetLock.Runway.AirportIcao,
                        targetLock.Runway.RunwayId,
                        _freeTargetMonitorStatus);
                }
                else if (_freeGearDownActive && _freeInference.ProvisionalTarget is { } provisional)
                {
                    SecondaryHud.ShowProvisionalFreeTarget(
                        provisional.Runway.Airport.Icao,
                        provisional.Runway.RunwayId,
                        _freeTargetMonitorStatus);
                }
                else
                {
                    SecondaryHud.ShowFreeRunwaySearch(_freeGearDownActive, _freeTargetMonitorStatus);
                }
            }
        });
    }

    private void HandleFreeDiscoveryGearState(TelemetrySample sample)
    {
        if (!IsFreeMode)
            return;

        var gearDown = !sample.IsGearRetractable || sample.GearHandlePosition > 0.5;
        if (_session is not null)
        {
            // Once evaluation starts, gear changes are part of scoring and never release
            // the frozen airport/runway target.
            _freeGearDownActive = gearDown;
            return;
        }

        if (_freeTargetLock is not null)
        {
            // Gear movement remains part of scoring readiness, but never releases an acquired runway.
            _freeGearDownActive = gearDown;
            return;
        }

        if (gearDown == _freeGearDownActive)
            return;

        _freeGearDownActive = gearDown;
        if (!gearDown)
        {
            _freeInference.Reset();
            PhaseLabel = "Waiting for gear";
            FreeAirportStatus = "Runway data warming · lower gear to select an aligned runway";
            SecondaryHud.ResetAttempt();
            ShowFreeRunwaySearch(FreeAirportStatus);
            return;
        }

        PhaseLabel = "Targeting";
        FreeAirportStatus = "Gear down · scanning aligned runways · all nearby airports eligible";
        ShowFreeRunwaySearch(FreeAirportStatus);
        _ = RunFreeInferenceAsync();
    }

    private bool TryHandleFreeFlightAircraftChange(TelemetrySample sample)
    {
        if (!IsFreeMode
            || CurrentGuidanceChallenge is null
            || _session?.Phase is LandingPhase.Scored)
        {
            ResetPendingFreeAircraftChange();
            return false;
        }

        var expectedTitle = CurrentGuidanceChallenge.AircraftTitles
            .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title))
            ?.Trim();
        var actualTitle = sample.AircraftTitle?.Trim();
        if (string.IsNullOrWhiteSpace(expectedTitle)
            || string.IsNullOrWhiteSpace(actualTitle)
            || string.Equals(expectedTitle, actualTitle, StringComparison.OrdinalIgnoreCase))
        {
            ResetPendingFreeAircraftChange();
            return false;
        }

        if (!string.Equals(_pendingFreeAircraftTitle, actualTitle, StringComparison.OrdinalIgnoreCase))
        {
            _pendingFreeAircraftTitle = actualTitle;
            _pendingFreeAircraftTitleSince = sample.Timestamp;
            _pendingFreeAircraftTitleSamples = 1;
            return false;
        }

        _pendingFreeAircraftTitleSamples++;
        var mismatchDuration = _pendingFreeAircraftTitleSince is { } since
            ? sample.Timestamp - since
            : TimeSpan.Zero;
        if (_pendingFreeAircraftTitleSamples < 3 || mismatchDuration < TimeSpan.FromSeconds(2))
            return false;

        RestartFreeDetection(
            $"Aircraft changed from '{expectedTitle}' to '{actualTitle}' · previous attempt cancelled.",
            $"Free aircraft change: '{expectedTitle}' → '{actualTitle}'; attempt cancelled and detection restarted.");
        return true;
    }

    private void RestartFreeDetection(string hudTip, string logMessage)
    {
        SecondaryHud.ResetAttempt();
        DetachSession();
        ReleaseFreeTargetLock(logMessage, _lastTelemetry);
        _activeChallenge = null;
        LastScore = null;
        ResultVisible = false;
        StopFreeInference(resetTarget: true);
        PhaseLabel = _freeGearDownActive ? "Targeting" : "Waiting for gear";
        HudTip = hudTip;
        FreeAirportStatus = _freeGearDownActive
            ? "Reacquiring best aligned runway · all nearby airports eligible"
            : "Runway data warming · lower gear to select an aligned runway";
        ShowFreeRunwaySearch(FreeAirportStatus);
        SpeedTargetInfo = "Optimal landing speed: —";
        _freeFlightCts = new CancellationTokenSource();
        _freeInferenceTimer.Start();
        _ = RunFreeInferenceAsync();
        AppendLog(logMessage);
        (CleanMetricsCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool TryHandleFreeFlightPositionJump(
        TelemetrySample? previous,
        TelemetrySample current)
    {
        if (!IsFreeMode || _freeTargetLock is null || previous is null)
            return false;

        var jumpNm = GeoUtil.HaversineMetersPublic(
                previous.Latitude,
                previous.Longitude,
                current.Latitude,
                current.Longitude)
            / RunwayPathGeometry.MetersPerNauticalMile;
        if (!double.IsFinite(jumpNm) || jumpNm <= 30)
            return false;

        RestartFreeDetection(
            $"New flight position detected ({jumpNm:0.0} NM jump) · reacquiring runway.",
            $"Free new-flight position jump: {jumpNm:0.0} NM; previous target released.");
        return true;
    }

    private void ResetPendingFreeAircraftChange()
    {
        _pendingFreeAircraftTitle = null;
        _pendingFreeAircraftTitleSince = null;
        _pendingFreeAircraftTitleSamples = 0;
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

        var vs = d.TouchdownSinkRateFpm != 0
            ? d.TouchdownSinkRateFpm
            : d.TouchdownVerticalSpeedFpm;
        if (vs == 0 && _session?.Snapshot is { TouchdownSinkRateFpm: var snapVs } && snapVs != 0)
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
                    StopFreeInference(resetTarget: _freeTargetLock is null);
                    if (_freeTargetLock is { } targetLock)
                    {
                        PhaseLabel = _session is null ? "Runway locked" : PhaseLabel;
                        FreeAirportStatus =
                            $"Locked {targetLock.Runway.AirportIcao} RWY {targetLock.Runway.RunwayId} · connection restored";
                        _freeTargetMonitorStatus = "locked · connection restored";
                        SecondaryHud.ShowLockedFreeTarget(
                            targetLock.Runway.AirportIcao,
                            targetLock.Runway.RunwayId,
                            _freeTargetMonitorStatus);
                        if (_lastTelemetry is { } restoredSample)
                            AppendFreeInferenceLog(
                                "connection-restored",
                                "locked runway retained",
                                restoredSample,
                                targetLock.Target);
                    }
                    else
                    {
                        _freeAirportCatalog = null;
                        _freeAirportDetails.Clear();
                        _freeGearDownActive = false;
                        PhaseLabel = "Detecting";
                        FreeAirportStatus = "Detecting airport and runway...";
                        ShowFreeRunwaySearch("Runway data warming · lower gear to select an aligned runway");
                    }

                    if (_session is null)
                    {
                        _freeFlightCts = new CancellationTokenSource();
                        _freeInferenceTimer.Start();
                        _ = RunFreeInferenceAsync();
                    }
                }
            }
            else
            {
                SecondaryHud.SetDisconnected();
                if (IsFighterHudVisible)
                    PushFighterHudPresentation(sample: null, connected: false);
                if (IsAetherHudVisible)
                    PushAetherPresentation(sample: null, connected: false);
                if (!IsFreeMode) return;
                StopFreeInference(resetTarget: _freeTargetLock is null);
                if (_freeTargetLock is { } targetLock)
                {
                    PhaseLabel = "Runway locked";
                    FreeAirportStatus =
                        $"Locked {targetLock.Runway.AirportIcao} RWY {targetLock.Runway.RunwayId} · waiting for simulator connection";
                    if (_lastTelemetry is { } disconnectedSample)
                        AppendFreeInferenceLog(
                            "connection-lost",
                            "locked runway retained",
                            disconnectedSample,
                            targetLock.Target);
                }
                else
                {
                    _freeAirportCatalog = null;
                    _freeAirportDetails.Clear();
                    _freeGearDownActive = false;
                    PhaseLabel = "Detecting";
                    FreeAirportStatus = "Detecting · waiting for simulator connection";
                }
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

    private bool CanSaveSnapshot() =>
        IsConnected && !IsCapturingSnapshot && !IsRestoringSnapshot && !IsLoading
        && _lastTelemetry is not null
        // CAMERA STATE ≥ 11 = menus / world map — no live flight to capture there.
        && (_lastTelemetry.CameraState is null || _lastTelemetry.CameraState < 11);

    private async Task SaveSnapshotAsync()
    {
        if (!CanSaveSnapshot()) return;

        IsCapturingSnapshot = true;
        StoreStatus = "Capturing flight state…";
        try
        {
            var snapshot = await _sim.CaptureSnapshotAsync();
            if (snapshot is null)
            {
                StoreStatus = "Could not read flight state — are you in an active flight?";
                return;
            }

            snapshot.AppBuildTag = AppBuild.Tag;
            snapshot.Airport = ResolveNearestAirport(snapshot.Latitude, snapshot.Longitude);
            snapshot.Name = SnapshotNameBuilder.BuildDefaultName(
                SnapshotNameInput, snapshot.Airport, snapshot.Latitude, snapshot.Longitude);

            var path = _snapshotStore.Save(snapshot);
            AppendLog($"Snapshot saved: '{snapshot.Name}' → {path}");
            SnapshotNameInput = "";
            RefreshSnapshots();
            SelectedSnapshot = Snapshots.FirstOrDefault(s =>
                string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));

            var pauseNote = snapshot.PauseContext == SnapshotPauseContext.ActivePause
                ? " (saved during active pause — velocities may be frozen values)"
                : "";
            StoreStatus = $"Saved: {snapshot.Name}{pauseNote}";
        }
        catch (Exception ex)
        {
            AppendLog($"Snapshot save failed: {ex.Message}");
            StoreStatus = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsCapturingSnapshot = false;
        }
    }

    /// <summary>
    /// Offline OurAirports CSV first (instant, has name/country), cached live catalog as
    /// fallback. Never blocks the save on the ~30 s live facilities load.
    /// </summary>
    private SnapshotAirportInfo? ResolveNearestAirport(double latitude, double longitude)
    {
        try
        {
            var index = OurAirportsAirportIndex.Default;
            if (index.IsAvailable && index.FindNearest(latitude, longitude) is { } nearest)
            {
                return new SnapshotAirportInfo
                {
                    Icao = nearest.Ident,
                    Name = string.IsNullOrWhiteSpace(nearest.Name) ? null : nearest.Name,
                    Municipality = string.IsNullOrWhiteSpace(nearest.Municipality) ? null : nearest.Municipality,
                    CountryCode = string.IsNullOrWhiteSpace(nearest.CountryCode) ? null : nearest.CountryCode,
                    DistanceNm = nearest.DistanceNm
                };
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Nearest-airport lookup (CSV) failed: {ex.Message}");
        }

        try
        {
            if (_freeAirportCatalog is { Count: > 0 } catalog)
            {
                AirportFacility? best = null;
                var bestMeters = double.MaxValue;
                foreach (var airport in catalog)
                {
                    var meters = GeoUtil.HaversineMetersPublic(
                        latitude, longitude, airport.Latitude, airport.Longitude);
                    if (meters < bestMeters)
                    {
                        bestMeters = meters;
                        best = airport;
                    }
                }

                if (best is not null)
                {
                    return new SnapshotAirportInfo
                    {
                        Icao = best.Icao,
                        CountryCode = string.IsNullOrWhiteSpace(best.Country) ? null : best.Country,
                        DistanceNm = bestMeters / 1852.0
                    };
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Nearest-airport lookup (live) failed: {ex.Message}");
        }

        return null;
    }

    private async Task LoadSelectedSnapshotAsync()
    {
        var item = SelectedSnapshot;
        if (item is null || !IsConnected || IsRestoringSnapshot || IsLoading) return;

        FlightStateSnapshot snapshot;
        try
        {
            snapshot = _snapshotStore.Load(item.Path);
        }
        catch (Exception ex)
        {
            StoreStatus = $"Could not read snapshot: {ex.Message}";
            AppendLog($"Snapshot load failed for '{item.Path}': {ex.Message}");
            return;
        }

        IsRestoringSnapshot = true;
        StoreStatus = $"Loading '{snapshot.Name}'…";
        AppendLog($"Snapshot restore requested: '{snapshot.Name}' (safe apply, no FlightLoad).");

        // Teleporting away invalidates any armed challenge/scoring session.
        DetachSession();
        ReleaseFreeTargetLock("snapshot restore/new flight", _lastTelemetry);
        StopFreeInference(resetTarget: true);
        _activeChallenge = null;
        SetAttemptOrigin(LandingAttemptOrigin.DefaultChallenge);
        LastScore = null;
        ResultVisible = false;
        PhaseLabel = "Loading";

        try
        {
            var options = new SnapshotRestoreOptions
            {
                RestoreWeather = RestoreWeatherEnabled,
                RestoreAutopilot = RestoreAutopilotEnabled,
                AutoResume = AutoResumeAfterRestore
            };
            var progress = new Progress<string>(msg => StoreStatus = msg);
            var result = await _sim.RestoreSnapshotAsync(snapshot, options, progress);

            if (result.Success)
            {
                AppendLog(
                    $"Snapshot restored: '{snapshot.Name}' · horiz={result.HorizontalErrorM:0} m · " +
                    $"altErr={result.AltErrorFeet:0} ft · onGround={result.ReportedOnGround}.");
                StoreStatus = AutoResumeAfterRestore
                    ? $"Restored: {snapshot.Name} — flying."
                    : $"Restored: {snapshot.Name} — PAUSED. Click Resume now (or unpause in the sim) when ready.";
            }
            else
            {
                AppendLog($"Snapshot restore FAILED: {result.Message}");
                StoreStatus = $"Restore failed: {result.Message}";
            }
        }
        catch (AircraftMismatchException ex)
        {
            var wanted = ex.ExpectedTitles.FirstOrDefault() ?? snapshot.AircraftTitle;
            StoreStatus = $"Wrong aircraft — this snapshot needs '{wanted}'.";
            AppendLog($"Snapshot restore blocked: aircraft mismatch (sim='{ex.ActualTitle}', snapshot='{wanted}').");
            MessageBox.Show(
                $"This snapshot was saved in:\n{wanted}\n\n" +
                $"Current aircraft: {ex.ActualTitle}\n\n" +
                "Challenge Lab will not swap the aircraft mid-session (that path can crash MSFS 2024).\n\n" +
                "Do this:\n" +
                "1. World Map → select the snapshot aircraft\n" +
                "2. Start a free flight (any airport)\n" +
                "3. Load the snapshot again.",
                "Challenge Lab — Store",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            AppendLog($"Snapshot restore error: {ex.Message}");
            StoreStatus = $"Restore error: {ex.Message}";
        }
        finally
        {
            IsRestoringSnapshot = false;
            RestartObservationAfterSnapshotRestore();
        }
    }

    /// <summary>Re-arm Free observation (or Idle in Normal) after a snapshot teleport.</summary>
    private void RestartObservationAfterSnapshotRestore()
    {
        if (IsFreeMode)
        {
            PhaseLabel = "Detecting";
            FreeAirportStatus = "Detecting airport and runway...";
            _freeFlightCts?.Cancel();
            _freeFlightCts?.Dispose();
            _freeFlightCts = new CancellationTokenSource();
            _freeInferenceTimer.Start();
        }
        else
        {
            PhaseLabel = "Idle";
        }
    }

    private void BeginRenameSnapshot()
    {
        if (SelectedSnapshot is null) return;
        RenameText = SelectedSnapshot.Name;
        IsRenamingSnapshot = true;
    }

    private void ConfirmRenameSnapshot()
    {
        var item = SelectedSnapshot;
        if (item is null || !IsRenamingSnapshot) return;

        var newName = (RenameText ?? "").Trim();
        if (newName.Length == 0)
        {
            StoreStatus = "Enter a name before renaming.";
            return;
        }

        try
        {
            var newPath = _snapshotStore.Rename(item.Path, newName);
            IsRenamingSnapshot = false;
            RenameText = "";
            RefreshSnapshots();
            SelectedSnapshot = Snapshots.FirstOrDefault(s =>
                string.Equals(s.Path, newPath, StringComparison.OrdinalIgnoreCase));
            StoreStatus = $"Renamed to: {newName}";
            AppendLog($"Snapshot renamed → {newPath}");
        }
        catch (Exception ex)
        {
            StoreStatus = $"Rename failed: {ex.Message}";
            AppendLog($"Snapshot rename failed: {ex.Message}");
        }
    }

    private void DeleteSelectedSnapshot()
    {
        var item = SelectedSnapshot;
        if (item is null) return;
        if (!ConfirmAction($"Delete this stored flight?\n\n{item.DisplayName}", "Challenge Lab — Store"))
            return;

        try
        {
            _snapshotStore.Delete(item.Path);
            AppendLog($"Snapshot deleted: {item.Path}");
            SelectedSnapshot = null;
            RefreshSnapshots();
            StoreStatus = "Deleted.";
        }
        catch (Exception ex)
        {
            StoreStatus = $"Delete failed: {ex.Message}";
            AppendLog($"Snapshot delete failed: {ex.Message}");
        }
    }

    private void RefreshSnapshots()
    {
        var selectedPath = SelectedSnapshot?.Path;
        // NEW collection instance so the ListBox reliably refreshes (AGENTS.md lesson).
        Snapshots = new ObservableCollection<SnapshotListItem>(_snapshotStore.List());
        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            SelectedSnapshot = Snapshots.FirstOrDefault(s =>
                string.Equals(s.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            SelectedSnapshot = null;
            SelectedSnapshotDetail = null;
        }

        if (Snapshots.Count == 0)
            StoreStatus = "No stored flights yet. Save one while flying (or parked).";
    }

    private void LoadSelectedSnapshotDetail()
    {
        if (SelectedSnapshot is null)
        {
            SelectedSnapshotDetail = null;
            return;
        }

        try
        {
            var snapshot = _snapshotStore.Load(SelectedSnapshot.Path);
            SelectedSnapshotDetail = new SnapshotDetailViewModel(snapshot);
        }
        catch (Exception ex)
        {
            SelectedSnapshotDetail = null;
            AppendLog($"Snapshot detail load failed: {ex.Message}");
        }
    }

    private void OpenSnapshotsFolder()
    {
        try
        {
            Directory.CreateDirectory(SnapshotsFolderPath);
            Process.Start(new ProcessStartInfo
            {
                FileName = SnapshotsFolderPath,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            AppendLog($"Open snapshots folder failed: {ex.Message}");
            MessageBox.Show(
                $"Could not open snapshots folder:\n{ex.Message}",
                "Challenge Lab — Store",
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
            if (tape.Challenge is { } tapedChallenge
                && string.IsNullOrWhiteSpace(tapedChallenge.Runway.RunwayDataSource))
            {
                if (!_runwayReferenceResolver.TryApplyCsv(tapedChallenge.Runway))
                {
                    tapedChallenge.Runway.RunwayDataSource = "Stored flight-tape geometry";
                    RunwayReferenceResolver.ApplyAimingPoint(
                        tapedChallenge.Runway,
                        tapedChallenge.Runway.RunwayDataSource,
                        "Low");
                }
            }
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
            VerticalSpeedFpm = result.Diagnostics.TouchdownSinkRateFpm != 0
                ? result.Diagnostics.TouchdownSinkRateFpm
                : result.Diagnostics.TouchdownVerticalSpeedFpm,
            Criteria = criteria,
            EvaluationKeyId = result.EvaluationKeyId,
            EvaluationKeyVersion = result.EvaluationKeyVersion,
            ScoringProfileHash = result.ScoringProfileHash,
            RankedBucketId = result.RankedBucketId,
            Diagnostics = result.Diagnostics,
            LandingVisualization = result.LandingVisualization
        };
    }

    private void PushFighterHudPresentation(
        ChallengeLab.Core.Models.TelemetrySample? sample = null,
        bool? connected = null,
        LandingMonitorReading? sharedGuidance = null)
    {
        if (!IsFighterHudVisible)
            return;

        var isConnected = connected ?? _sim.IsConnected;
        sample ??= _lastTelemetry;
        _fighterHudSequence++;
        if (sample is null)
        {
            FighterHudPresentation?.Invoke(HudPresentationFrame.Disconnected(_fighterHudSequence));
            return;
        }

        var settings = CurrentSessionSettings;
        var guidanceChallenge = CurrentGuidanceChallenge;
        double? targetTouchdownIas = null;
        if (guidanceChallenge is not null && settings is not null)
        {
            targetTouchdownIas = SpeedTargetCalculator
                .Resolve(guidanceChallenge, settings, sample)
                .TargetTouchdownIasKts;
        }

        var guidance = sharedGuidance ?? LandingMonitorCalculator.Calculate(
            sample,
            guidanceChallenge?.Runway,
            targetTouchdownIas,
            settings?.ApproachPathMinDistNm ?? .2,
            settings?.ApproachPathMaxDistNm ?? 4.5);
        if (sharedGuidance is null)
        {
            guidance = _landingGuidanceHold.Update(
                sample,
                guidanceChallenge?.Runway,
                guidance,
                settings?.FlareAglFeet ?? 50);
        }

        FighterHudPresentation?.Invoke(HudPresentationFrame.FromGuidance(
            sample,
            isConnected,
            _fighterHudSequence,
            guidanceChallenge?.Runway,
            guidance));
    }

    private void PushAetherPresentation(
        ChallengeLab.Core.Models.TelemetrySample? sample = null,
        bool? connected = null,
        LandingMonitorReading? sharedGuidance = null)
    {
        if (!IsAetherHudVisible)
            return;

        var isConnected = connected ?? _sim.IsConnected;
        sample ??= _lastTelemetry;
        _aetherHudSequence++;
        if (sample is null)
        {
            AetherPresentation?.Invoke(AetherSnapshot.Disconnected(_aetherHudSequence));
            return;
        }

        var settings = CurrentSessionSettings;
        var guidanceChallenge = CurrentGuidanceChallenge;
        double? targetTouchdownIas = null;
        if (guidanceChallenge is not null && settings is not null)
        {
            targetTouchdownIas = SpeedTargetCalculator
                .Resolve(guidanceChallenge, settings, sample)
                .TargetTouchdownIasKts;
        }

        var guidance = sharedGuidance ?? LandingMonitorCalculator.Calculate(
            sample,
            guidanceChallenge?.Runway,
            targetTouchdownIas,
            settings?.ApproachPathMinDistNm ?? .2,
            settings?.ApproachPathMaxDistNm ?? 4.5);
        if (sharedGuidance is null)
        {
            guidance = _landingGuidanceHold.Update(
                sample,
                guidanceChallenge?.Runway,
                guidance,
                settings?.FlareAglFeet ?? 50);
        }

        AetherPresentation?.Invoke(AetherMapper.FromGuidance(
            sample,
            isConnected,
            _aetherHudSequence,
            guidanceChallenge?.Runway,
            guidance));
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
