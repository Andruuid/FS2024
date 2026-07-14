using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxImage = System.Windows.MessageBoxImage;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using ChallengeLab.SimConnect;

namespace ChallengeLab.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ConfigLoader _configLoader;
    private readonly HighscoreStore _highscores;
    private readonly ScoreEngine _scoreEngine = new();
    private readonly ISimBridge _sim;
    private readonly DispatcherTimer _reconnectTimer;

    private ChallengeCardViewModel? _selectedChallenge;
    private DifficultyLevel _selectedDifficulty = DifficultyLevel.Strict;
    private string _connectionStatus = "Disconnected";
    private bool _isConnected;
    private bool _isLoading;
    private double _loadProgress;
    private string _loadStatus = "";
    private string _hudTip = "Start a challenge to begin.";
    private string _phaseLabel = "Idle";
    private string _liveStats = "—";
    private ScoreResult? _lastScore;
    private LandingSession? _session;
    private ScoringProfileConfig? _activeProfile;
    private ChallengeConfig? _activeChallenge;
    private bool _resultVisible;
    private int _selectedTab;
    private HighscoreEntry? _selectedHighscore;
    private LandingReportViewModel? _landingReport;

    public MainViewModel(ISimBridge sim, ConfigLoader? configLoader = null, HighscoreStore? highscores = null)
    {
        _sim = sim;
        _configLoader = configLoader ?? new ConfigLoader();
        _highscores = highscores ?? new HighscoreStore();

        Challenges = new ObservableCollection<ChallengeCardViewModel>();
        Highscores = new ObservableCollection<HighscoreEntry>();
        CriterionResults = new ObservableCollection<CriterionResultViewModel>();

        StartChallengeCommand = new RelayCommand(async () => await StartChallengeAsync(), () =>
            SelectedChallenge is { Available: true } && !IsLoading);
        RestartCommand = new RelayCommand(async () => await RestartAsync(), () =>
            _activeChallenge is not null && !IsLoading);
        ConnectCommand = new RelayCommand(TriggerConnect);
        DismissResultCommand = new RelayCommand(() => ResultVisible = false);
        ClearHighscoreSelectionCommand = new RelayCommand(() => SelectedHighscore = null);
        OpenMenuCommand = new RelayCommand(() =>
        {
            ResultVisible = false;
            SelectedTab = 0;
            RequestActivateMain?.Invoke();
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
        RefreshHighscores();
    }

    public event Action? RequestConnect;
    public event Action? RequestShowHud;
    public event Action? RequestActivateMain;
    public event Action<ScoreResult>? ScoreComputed;

    public void TriggerConnect() => RequestConnect?.Invoke();

    public ObservableCollection<ChallengeCardViewModel> Challenges { get; }
    public ObservableCollection<HighscoreEntry> Highscores { get; }
    public ObservableCollection<CriterionResultViewModel> CriterionResults { get; }

    public ICommand StartChallengeCommand { get; }
    public ICommand RestartCommand { get; }
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
            LandingReport = value is null ? null : new LandingReportViewModel(value);
        }
    }

    public LandingReportViewModel? LandingReport
    {
        get => _landingReport;
        private set => SetProperty(ref _landingReport, value);
    }

    public bool HasLandingReport => LandingReport is not null;

    public IEnumerable<DifficultyLevel> Difficulties { get; } =
        new[] { DifficultyLevel.Easy, DifficultyLevel.Strict };

    public ChallengeCardViewModel? SelectedChallenge
    {
        get => _selectedChallenge;
        set
        {
            SetProperty(ref _selectedChallenge, value);
            (StartChallengeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public DifficultyLevel SelectedDifficulty
    {
        get => _selectedDifficulty;
        set => SetProperty(ref _selectedDifficulty, value);
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
                var ordered = value.Criteria.Where(x => x.Applied)
                    .OrderByDescending(c => c.Id is "touchdown_vs" or "touchdownVerticalSpeedFpm" ||
                                            c.DisplayName.Contains("firmness", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                    .ThenByDescending(c => c.Weight)
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
        if (_activeChallenge is null) return;
        await RunLoadAsync(_activeChallenge);
    }

    private async Task RunLoadAsync(ChallengeConfig challenge)
    {
        IsLoading = true;
        ResultVisible = false;
        LastScore = null;
        LoadProgress = 0;
        LoadStatus = "Starting…";
        PhaseLabel = "Loading";

        try
        {
            var profilePath = challenge.ScoringProfile;
            _activeProfile = _configLoader.LoadScoringProfile(profilePath);
            _activeChallenge = challenge;

            _session?.Reset();
            _session = new LandingSession(challenge, _activeProfile);
            _session.PhaseChanged += (_, p) =>
                Application.Current.Dispatcher.Invoke(() => PhaseLabel = p.ToString());
            _session.SettledReady += OnSettledReady;

            var stages = new[]
            {
                "Connecting…",
                "Loading flight…",
                "Positioning…",
                "Weather…",
                "Configuring aircraft…",
                "Arming scoring…"
            };

            var progress = new Progress<string>(msg =>
            {
                LoadStatus = msg;
                var idx = Array.FindIndex(stages, s => msg.Contains(s.TrimEnd('…'), StringComparison.OrdinalIgnoreCase));
                if (idx < 0)
                {
                    if (msg.Contains("flight", StringComparison.OrdinalIgnoreCase)) idx = 1;
                    else if (msg.Contains("Position", StringComparison.OrdinalIgnoreCase) || msg.Contains("teleport", StringComparison.OrdinalIgnoreCase)) idx = 2;
                    else if (msg.Contains("weather", StringComparison.OrdinalIgnoreCase)) idx = 3;
                    else if (msg.Contains("gear", StringComparison.OrdinalIgnoreCase) || msg.Contains("Configur", StringComparison.OrdinalIgnoreCase)) idx = 4;
                    else if (msg.Contains("armed", StringComparison.OrdinalIgnoreCase)) idx = 5;
                    else idx = 0;
                }
                LoadProgress = (idx + 1) / (double)stages.Length * 100;
            });

            var flightPath = _configLoader.ResolveFlightPath(challenge.FlightFile);
            await _sim.LoadScenarioAsync(challenge, flightPath, progress);

            LoadProgress = 100;
            LoadStatus = "Armed — fly the landing!";
            _session.Arm();
            PhaseLabel = "Armed";
            HudTip = challenge.HudTips.FirstOrDefault() ?? "Good luck.";
            RotateTips(challenge);
            RequestShowHud?.Invoke();
            (RestartCommand as RelayCommand)?.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            LoadStatus = "Failed";
            AppendLog($"Load failed: {ex.Message}");
            MessageBox.Show($"Could not load challenge:\n{ex.Message}", "Challenge Lab",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
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
        if (_session is null || _activeChallenge is null || _activeProfile is null) return;
        if (_session.IsComplete && LastScore is not null) return;

        Application.Current.Dispatcher.Invoke(() =>
        {
            var result = _scoreEngine.Evaluate(_activeChallenge, _activeProfile, _session.Snapshot, SelectedDifficulty);
            LastScore = result;
            ResultVisible = true;
            _highscores.Add(result);
            RefreshHighscores();
            HudTip = $"Score {result.ScorePercent:0.#}% · Grade {result.Grade}";
            PhaseLabel = "Scored";
            ScoreComputed?.Invoke(result);
            RequestShowHud?.Invoke();
            AppendLog($"Scored {result.ScorePercent}% ({result.Grade}) on {result.ChallengeTitle} [{result.Level}]");
        });
    }

    private void OnTelemetry(object? sender, TelemetrySample sample)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            LiveStats =
                $"GS {sample.GroundSpeedKts:0} kt  ·  VS {sample.VerticalSpeedFpm:0} fpm  ·  " +
                $"Bank {sample.BankDeg:0.0}°  ·  Wind {sample.WindDirectionDeg:000}/{sample.WindVelocityKts:0}kt  ·  " +
                $"{(sample.SimOnGround ? "GND" : "AIR")}";

            _session?.Ingest(sample);
        });
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
        Highscores.Clear();
        foreach (var e in _highscores.Entries)
            Highscores.Add(e);
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
        _sim.StateChanged -= OnSimStateChanged;
        _sim.TelemetryReceived -= OnTelemetry;
        _sim.Dispose();
    }
}
