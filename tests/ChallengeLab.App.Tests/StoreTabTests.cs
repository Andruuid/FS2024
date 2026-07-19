using System.IO;
using ChallengeLab.App.ViewModels;
using ChallengeLab.Core.Career;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Snapshots;
using ChallengeLab.SimConnect;

namespace ChallengeLab.App.Tests;

[Collection("Wpf")]
public sealed class StoreTabTests
{
    [Fact]
    public void SaveCommand_GatedOnConnectionAndTelemetry()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            var vm = env.CreateViewModel();
            try
            {
                // Disconnected + no telemetry → save unavailable.
                Assert.False(vm.SaveSnapshotCommand.CanExecute(null));

                // Connected but still no telemetry received → save stays unavailable.
                vm.IsConnected = true;
                Assert.False(vm.SaveSnapshotCommand.CanExecute(null));
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void LoadCommand_PassesSnapshotAndOptionsToBridge_AndReportsStatus()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            var vm = env.CreateViewModel();
            try
            {
                var path = env.SnapshotStore.Save(BuildSnapshot("Test approach"));
                vm.RefreshSnapshotsCommand.Execute(null);
                Assert.Single(vm.Snapshots);

                vm.SelectedSnapshot = vm.Snapshots[0];
                Assert.False(vm.LoadSnapshotCommand.CanExecute(null)); // disconnected

                vm.IsConnected = true;
                Assert.True(vm.LoadSnapshotCommand.CanExecute(null));
                Assert.False(vm.ResumeNowCommand.CanExecute(null));

                vm.RestoreWeatherEnabled = false;
                vm.RestoreAutopilotEnabled = true;
                vm.AutoResumeAfterRestore = false;
                vm.LoadSnapshotCommand.Execute(null);

                Assert.Equal(1, env.Sim.RestoreCalls);
                Assert.NotNull(env.Sim.LastRestoredSnapshot);
                Assert.Equal("Test approach", env.Sim.LastRestoredSnapshot!.Name);
                Assert.NotNull(env.Sim.LastRestoreOptions);
                Assert.False(env.Sim.LastRestoreOptions!.RestoreWeather);
                Assert.True(env.Sim.LastRestoreOptions.RestoreAutopilot);
                Assert.False(env.Sim.LastRestoreOptions.AutoResume);
                Assert.Contains("Restored", vm.StoreStatus, StringComparison.Ordinal);
                Assert.Contains("PAUSED", vm.StoreStatus, StringComparison.Ordinal);
                Assert.False(vm.IsRestoringSnapshot);
                Assert.True(vm.IsSnapshotResumeReady);
                Assert.True(vm.ResumeNowCommand.CanExecute(null));

                vm.ResumeNowCommand.Execute(null);
                Assert.Equal(1, env.Sim.ResumeCalls);
                Assert.False(vm.IsSnapshotResumeReady);
                Assert.False(vm.ResumeNowCommand.CanExecute(null));
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void LoadCommand_FailedReadinessKeepsResumeDisabled()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            env.Sim.RestoreResult = SpawnApplyResult.Fail(
                "Stored state is not fully ready — gear=down 42% want down fully.");
            var vm = env.CreateViewModel();
            try
            {
                env.SnapshotStore.Save(BuildSnapshot("Not ready"));
                vm.RefreshSnapshotsCommand.Execute(null);
                vm.SelectedSnapshot = vm.Snapshots[0];
                vm.IsConnected = true;

                vm.LoadSnapshotCommand.Execute(null);

                Assert.False(vm.IsRestoringSnapshot);
                Assert.False(vm.IsSnapshotResumeReady);
                Assert.False(vm.ResumeNowCommand.CanExecute(null));
                Assert.Contains("Restore failed", vm.StoreStatus, StringComparison.Ordinal);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void RenameAndDelete_FlowThroughStore_DeleteUsesInjectedConfirm()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            var vm = env.CreateViewModel();
            try
            {
                env.SnapshotStore.Save(BuildSnapshot("initial name"));
                vm.RefreshSnapshotsCommand.Execute(null);
                vm.SelectedSnapshot = vm.Snapshots[0];

                // Rename via inline editor commands.
                Assert.True(vm.RenameSnapshotCommand.CanExecute(null));
                vm.RenameSnapshotCommand.Execute(null);
                Assert.True(vm.IsRenamingSnapshot);
                Assert.Equal("initial name", vm.RenameText);
                vm.RenameText = "renamed flight";
                vm.ConfirmRenameSnapshotCommand.Execute(null);

                Assert.False(vm.IsRenamingSnapshot);
                Assert.Single(vm.Snapshots);
                Assert.Equal("renamed flight", vm.Snapshots[0].Name);
                Assert.NotNull(vm.SelectedSnapshot);
                Assert.Equal("renamed flight", vm.SelectedSnapshot!.Name);

                // Delete declined → file stays.
                var confirmCalls = 0;
                vm.ConfirmAction = (_, _) =>
                {
                    confirmCalls++;
                    return false;
                };
                vm.DeleteSnapshotCommand.Execute(null);
                Assert.Equal(1, confirmCalls);
                Assert.Single(vm.Snapshots);

                // Delete confirmed → gone.
                vm.ConfirmAction = (_, _) => true;
                vm.DeleteSnapshotCommand.Execute(null);
                Assert.Empty(vm.Snapshots);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void RefreshSnapshots_AssignsNewCollectionInstance()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            var vm = env.CreateViewModel();
            try
            {
                var before = vm.Snapshots;
                env.SnapshotStore.Save(BuildSnapshot("fresh"));
                vm.RefreshSnapshotsCommand.Execute(null);

                // AGENTS.md WPF lesson: rebuilds must assign a NEW ObservableCollection.
                Assert.NotSame(before, vm.Snapshots);
                Assert.Single(vm.Snapshots);
            }
            finally
            {
                vm.Dispose();
            }
        });
    }

    [Fact]
    public void StoreXaml_HasStoreTabAfterTestingWithExpectedBindings()
    {
        var root = FindRepositoryRoot();
        var mainXaml = File.ReadAllText(
            Path.Combine(root, "src", "ChallengeLab.App", "Views", "MainWindow.xaml"));
        var mainViewModel = File.ReadAllText(
            Path.Combine(root, "src", "ChallengeLab.App", "ViewModels", "MainViewModel.cs"));

        var testingTab = mainXaml.IndexOf("<TabItem Header=\"TESTING\">", StringComparison.Ordinal);
        var storeTab = mainXaml.IndexOf("<TabItem Header=\"STORE\">", StringComparison.Ordinal);
        Assert.InRange(testingTab, 0, storeTab - 1);

        Assert.Contains("SaveSnapshotCommand", mainXaml, StringComparison.Ordinal);
        Assert.Contains("LoadSnapshotCommand", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding Snapshots}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedSnapshot}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("IsRenamingSnapshot", mainXaml, StringComparison.Ordinal);
        Assert.Contains("IsRestoringSnapshot", mainXaml, StringComparison.Ordinal);
        Assert.Contains("IsSnapshotResumeReady", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("public const int StoreTabIndex = 5;", mainViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotRestore_HasFrozenPoseGearRecoveryAndNoFlightLoad()
    {
        var root = FindRepositoryRoot();
        var snapshotBridge = File.ReadAllText(
            Path.Combine(root, "src", "ChallengeLab.SimConnect", "SimConnectClient.Snapshot.cs"));

        Assert.Contains("AdvanceSnapshotGearWhileFrozenAsync", snapshotBridge, StringComparison.Ordinal);
        Assert.Contains("PauseSim(false)", snapshotBridge, StringComparison.Ordinal);
        Assert.Contains("GroundGearClearanceFeet", snapshotBridge, StringComparison.Ordinal);
        Assert.Contains("GEAR POSITION:0", snapshotBridge, StringComparison.Ordinal);
        Assert.Contains("TrySetGearPositionsDirect", snapshotBridge, StringComparison.Ordinal);
        Assert.DoesNotContain("FlightLoad(", snapshotBridge, StringComparison.Ordinal);
    }

    private static FlightStateSnapshot BuildSnapshot(string name) => new()
    {
        CreatedUtc = new DateTimeOffset(2026, 7, 18, 14, 30, 0, TimeSpan.Zero),
        Name = name,
        AircraftTitle = "A330-200 (RR)",
        Latitude = 47.4647,
        Longitude = 8.5492,
        AltitudeFeet = 4200,
        HeadingTrueDeg = 137,
        IasKts = 140,
        OnGround = false,
        FlapsHandleCount = 4
    };

    private sealed class TestEnv : IDisposable
    {
        private readonly string _tempDirectory = Path.Combine(
            Path.GetTempPath(), "ChallengeLabStoreUi", Guid.NewGuid().ToString("N"));

        public TestEnv()
        {
            Directory.CreateDirectory(_tempDirectory);
            SnapshotStore = new SnapshotStore(Path.Combine(_tempDirectory, "snapshots"));
        }

        public FakeSimBridge Sim { get; } = new();
        public SnapshotStore SnapshotStore { get; }

        public MainViewModel CreateViewModel() => new(
            Sim,
            new ConfigLoader(FindConfig()),
            new HighscoreStore(Path.Combine(_tempDirectory, "highscores.json")),
            new CareerProgressStore(Path.Combine(_tempDirectory, "career.json")),
            careerRandom: null,
            runwayCatalog: null,
            snapshotStore: SnapshotStore);

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }

    private sealed class FakeSimBridge : ISimBridge
    {
        public SimConnectionState State { get; private set; } = SimConnectionState.Disconnected;
        public string? StatusMessage => State.ToString();
        public bool IsConnected => State == SimConnectionState.Connected;
        public FlightStateSnapshot? NextSnapshot { get; set; }
        public SpawnApplyResult RestoreResult { get; set; } =
            SpawnApplyResult.Ok("restored", 0, 0, 140, 47.4647, 8.5492, 4200, false);
        public int CaptureCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public FlightStateSnapshot? LastRestoredSnapshot { get; private set; }
        public SnapshotRestoreOptions? LastRestoreOptions { get; private set; }

        public event EventHandler<SimConnectionState>? StateChanged;
        public event EventHandler<Core.Models.TelemetrySample>? TelemetryReceived { add { } remove { } }
        public event EventHandler<string>? LogMessage { add { } remove { } }

        public void Connect(IntPtr windowHandle) { }

        public void Disconnect()
        {
            State = SimConnectionState.Disconnected;
            StateChanged?.Invoke(this, State);
        }

        public void ReceiveMessage() { }

        public Task<IReadOnlyList<Core.Facilities.AirportFacility>> GetAirportsAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Core.Facilities.AirportFacility>>([]);

        public Task<Core.Facilities.AirportRunwayFacility> GetAirportRunwaysAsync(
            Core.Facilities.AirportFacility airport,
            CancellationToken ct = default) =>
            Task.FromResult(new Core.Facilities.AirportRunwayFacility(airport, [], []));

        public Task<SpawnApplyResult> LoadScenarioAsync(
            ChallengeConfig challenge,
            string flightFileAbsolutePath,
            IProgress<string>? progress = null,
            CancellationToken ct = default) =>
            Task.FromResult(SpawnApplyResult.Ok("ready", 0, 0, 0, 0, 0, 140, false));

        public void ConfigureAircraft(AircraftSetupConfig setup) { }
        public void ApplyWeather(WeatherConfig weather) { }
        public void ApplyTimeOfDay(TimeOfDayConfig? timeOfDay) { }
        public void Teleport(SpawnConfig spawn) { }
        public void ResumeFlight() => ResumeCalls++;

        public Task<FlightStateSnapshot?> CaptureSnapshotAsync(CancellationToken ct = default)
        {
            CaptureCalls++;
            return Task.FromResult(NextSnapshot);
        }

        public Task<SpawnApplyResult> RestoreSnapshotAsync(
            FlightStateSnapshot snapshot,
            SnapshotRestoreOptions options,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            RestoreCalls++;
            LastRestoredSnapshot = snapshot;
            LastRestoreOptions = options;
            return Task.FromResult(RestoreResult);
        }

        public void Dispose() { }
    }

    private static string FindConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var path = Path.Combine(dir.FullName, "config");
            if (File.Exists(Path.Combine(path, "catalog.json"))) return path;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("config not found");
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "ChallengeLab.App")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
