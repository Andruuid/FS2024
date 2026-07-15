using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using ChallengeLab.App.ViewModels;
using ChallengeLab.App.Views;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.SimConnect;

namespace ChallengeLab.App.Tests;

public sealed class FreeFlightHudModeTests
{
    [Fact]
    public void ModeTransitions_CancelDetectionAndNeverMutateSimulator()
    {
        RunSta(() =>
        {
            var app = new ChallengeLab.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            var scorePath = Path.Combine(Path.GetTempPath(), $"challenge-lab-{Guid.NewGuid():N}.json");
            var sim = new FakeSimBridge { BlockAirportCatalog = true };
            var vm = new MainViewModel(
                sim,
                new ConfigLoader(FindConfig()),
                new HighscoreStore(scorePath));
            CompanionHudWindow? hud = null;
            try
            {
                Assert.True(vm.IsNormalMode);
                Assert.False(vm.IsFreeMode);

                var showHudCount = 0;
                vm.RequestShowHud += () => showHudCount++;
                sim.SetState(SimConnectionState.Connected);
                Assert.Equal(1, showHudCount);

                sim.EmitTelemetry(ApproachSample());
                vm.FreeModeCommand.Execute(null);

                Assert.True(vm.IsFreeMode);
                Assert.False(vm.StartChallengeCommand.CanExecute(null));
                Assert.False(vm.RestartCommand.CanExecute(null));
                Assert.True(vm.CleanMetricsCommand.CanExecute(null));
                Assert.True(sim.AirportCatalogRequested);
                AssertNoSimulatorMutation(sim);

                hud = new CompanionHudWindow(vm);
                hud.Show();
                hud.UpdateLayout();
                Assert.Equal("Clear", ((Button)hud.FindName("CleanButton")).Content);
                Assert.Equal(Visibility.Collapsed, ((Button)hud.FindName("GoButton")).Visibility);
                Assert.Equal(Visibility.Collapsed, ((Button)hud.FindName("RestartButton")).Visibility);
                Assert.Equal(Color.FromRgb(0x2D, 0xE2, 0xE6), FindButton(hud, "Free").Background is SolidColorBrush freeBrush
                    ? freeBrush.Color
                    : default);

                sim.SetState(SimConnectionState.Disconnected);

                Assert.True(sim.FacilityRequestCancelled);
                Assert.Equal("Detecting", vm.PhaseLabel);
                Assert.Contains("waiting for simulator", vm.FreeAirportStatus);
                AssertNoSimulatorMutation(sim);

                vm.NormalModeCommand.Execute(null);

                Assert.True(vm.IsNormalMode);
                Assert.Equal("Idle", vm.PhaseLabel);
                Assert.Equal(Visibility.Visible, ((Button)hud.FindName("GoButton")).Visibility);
                Assert.Equal(Visibility.Visible, ((Button)hud.FindName("RestartButton")).Visibility);
                Assert.Equal(Color.FromRgb(0x2D, 0xE2, 0xE6), FindButton(hud, "Normal").Background is SolidColorBrush normalBrush
                    ? normalBrush.Color
                    : default);
                AssertNoSimulatorMutation(sim);
            }
            finally
            {
                hud?.Close();
                vm.Dispose();
                app.Shutdown();
                if (File.Exists(scorePath)) File.Delete(scorePath);
            }
        });
    }

    private static Button FindButton(DependencyObject parent, string content)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && Equals(button.Content, content))
                return button;
            try { return FindButton(child, content); }
            catch (InvalidOperationException) { }
        }

        throw new InvalidOperationException($"Button '{content}' was not found.");
    }

    private static void AssertNoSimulatorMutation(FakeSimBridge sim)
    {
        Assert.Equal(0, sim.LoadScenarioCalls);
        Assert.Equal(0, sim.ConfigureCalls);
        Assert.Equal(0, sim.WeatherCalls);
        Assert.Equal(0, sim.TimeCalls);
        Assert.Equal(0, sim.TeleportCalls);
        Assert.Equal(0, sim.ResumeCalls);
    }

    private static TelemetrySample ApproachSample() => new()
    {
        Latitude = 0,
        Longitude = -.04,
        AltitudeFeet = 1000,
        AglFeet = 1000,
        RadioHeightFeet = 1000,
        AirspeedKts = 90,
        GroundSpeedKts = 90,
        GroundTrackTrueDeg = 90,
        HeadingTrueDeg = 110,
        SimOnGround = false,
        DesignSpeedVs0Kts = 45,
        IsGearWheels = true,
        IsGearRetractable = true
    };

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

    private sealed class FakeSimBridge : ISimBridge
    {
        public SimConnectionState State { get; private set; } = SimConnectionState.Disconnected;
        public string? StatusMessage => State.ToString();
        public bool IsConnected => State == SimConnectionState.Connected;
        public bool BlockAirportCatalog { get; init; }
        public bool AirportCatalogRequested { get; private set; }
        public bool FacilityRequestCancelled { get; private set; }
        public int LoadScenarioCalls { get; private set; }
        public int ConfigureCalls { get; private set; }
        public int WeatherCalls { get; private set; }
        public int TimeCalls { get; private set; }
        public int TeleportCalls { get; private set; }
        public int ResumeCalls { get; private set; }

        public event EventHandler<SimConnectionState>? StateChanged;
        public event EventHandler<TelemetrySample>? TelemetryReceived;
        public event EventHandler<string>? LogMessage { add { } remove { } }

        public void SetState(SimConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }

        public void EmitTelemetry(TelemetrySample sample) => TelemetryReceived?.Invoke(this, sample);
        public void Connect(IntPtr windowHandle) { }
        public void Disconnect() => SetState(SimConnectionState.Disconnected);
        public void ReceiveMessage() { }

        public async Task<IReadOnlyList<AirportFacility>> GetAirportsAsync(CancellationToken ct = default)
        {
            AirportCatalogRequested = true;
            using var registration = ct.Register(() => FacilityRequestCancelled = true);
            if (BlockAirportCatalog)
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return [new AirportFacility("TEST", "ZZ", 0, 0, 0)];
        }

        public Task<AirportRunwayFacility> GetAirportRunwaysAsync(
            AirportFacility airport,
            CancellationToken ct = default) =>
            Task.FromResult(new AirportRunwayFacility(airport, [], []));

        public Task<SpawnApplyResult> LoadScenarioAsync(
            ChallengeConfig challenge,
            string flightFileAbsolutePath,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            LoadScenarioCalls++;
            return Task.FromResult(SpawnApplyResult.Ok("unexpected", 0, 0, 0, 0, 0, 0, false));
        }

        public void ConfigureAircraft(AircraftSetupConfig setup) => ConfigureCalls++;
        public void ApplyWeather(WeatherConfig weather) => WeatherCalls++;
        public void ApplyTimeOfDay(TimeOfDayConfig? timeOfDay) => TimeCalls++;
        public void Teleport(SpawnConfig spawn) => TeleportCalls++;
        public void ResumeFlight() => ResumeCalls++;
        public void Dispose() { }
    }
}
