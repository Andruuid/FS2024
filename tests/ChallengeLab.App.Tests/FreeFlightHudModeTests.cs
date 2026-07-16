using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using System.Text;
using ChallengeLab.App.Controls;
using ChallengeLab.App.ViewModels;
using ChallengeLab.App.Views;
using ChallengeLab.Core.Career;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.SimConnect;

namespace ChallengeLab.App.Tests;

[Collection("Wpf")]
public sealed class FreeFlightHudModeTests
{
    [Fact]
    public void ModeTransitions_CancelDetectionAndNeverMutateSimulator()
    {
        RunSta(() =>
        {
            var app = new ChallengeLab.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            var bindingErrors = new BindingErrorListener();
            PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
            var scorePath = Path.Combine(Path.GetTempPath(), $"challenge-lab-{Guid.NewGuid():N}.json");
            var careerPath = Path.Combine(Path.GetTempPath(), $"challenge-lab-career-{Guid.NewGuid():N}.json");
            var sim = new FakeSimBridge { BlockAirportCatalog = true };
            var vm = new MainViewModel(
                sim,
                new ConfigLoader(FindConfig()),
                new HighscoreStore(scorePath),
                new CareerProgressStore(careerPath));
            CompanionHudWindow? hud = null;
            SecondaryHudWindow? secondary = null;
            SecondaryHudWindow? restoredSecondary = null;
            var positionPath = Path.Combine(Path.GetTempPath(), $"challenge-lab-hud-{Guid.NewGuid():N}.json");
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

                var monitorToggleRequests = 0;
                vm.RequestToggleSecondaryHud += () => monitorToggleRequests++;
                var monitorButton = Assert.IsType<Button>(hud.FindName("FlightMonitorButton"));
                monitorButton.Command.Execute(null);
                Assert.Equal(1, monitorToggleRequests);

                var positionStore = new SecondaryHudPositionStore(positionPath);
                secondary = new SecondaryHudWindow(vm, positionStore);
                secondary.Show();
                secondary.UpdateLayout();
                var windFlow = Assert.IsType<WindFlowIndicator>(secondary.FindName("WindFlow"));
                var angleBinding = System.Windows.Data.BindingOperations.GetBinding(
                    windFlow,
                    WindFlowIndicator.RelativeFromAngleProperty);
                var speedBinding = System.Windows.Data.BindingOperations.GetBinding(
                    windFlow,
                    WindFlowIndicator.WindSpeedKtsProperty);
                var activeBinding = System.Windows.Data.BindingOperations.GetBinding(
                    windFlow,
                    WindFlowIndicator.IsActiveProperty);
                Assert.Equal(System.Windows.Data.BindingMode.OneWay, angleBinding?.Mode);
                Assert.Equal(System.Windows.Data.BindingMode.OneWay, speedBinding?.Mode);
                Assert.Equal(System.Windows.Data.BindingMode.OneWay, activeBinding?.Mode);
                Assert.Null(secondary.FindName("ApproachProgressBar"));
                Assert.Null(secondary.FindName("ProgressAirplane"));
                Assert.NotNull(secondary.FindName("EtaText"));
                Assert.NotNull(secondary.FindName("ScoreGraph"));

                secondary.Left = SystemParameters.WorkArea.Left + 123;
                secondary.Top = SystemParameters.WorkArea.Top + 77;
                secondary.HideFromUser();
                Assert.False(secondary.IsVisible);
                secondary.Close();
                secondary = null;

                restoredSecondary = new SecondaryHudWindow(vm, positionStore);
                restoredSecondary.Show();
                restoredSecondary.UpdateLayout();
                Assert.InRange(
                    restoredSecondary.Left,
                    SystemParameters.WorkArea.Left + 122,
                    SystemParameters.WorkArea.Left + 124);
                Assert.InRange(
                    restoredSecondary.Top,
                    SystemParameters.WorkArea.Top + 76,
                    SystemParameters.WorkArea.Top + 78);

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
                Assert.True(string.IsNullOrWhiteSpace(bindingErrors.Errors), bindingErrors.Errors);
                AssertNoSimulatorMutation(sim);

                // Career uses the same safe Normal-mode load path, becomes active only
                // after the fake spawn verifies/arms, and paints the compact HUD strip.
                sim.SetState(SimConnectionState.Connected);
                vm.AcceptCareerAssignmentCommand.Execute(null);
                Assert.True(vm.CareerHasAssignment);
                vm.StartCareerAssignmentCommand.Execute(null);
                Assert.Equal(1, sim.LoadScenarioCalls);
                Assert.True(vm.IsCareerAttemptActive);
                Assert.Contains("80.0%", vm.CareerHudStatus, StringComparison.Ordinal);
                hud.UpdateLayout();
                Assert.Equal(
                    Visibility.Visible,
                    Assert.IsType<Border>(hud.FindName("CareerStatusStrip")).Visibility);

                vm.CleanMetricsCommand.Execute(null);
                Assert.True(vm.IsCareerAttemptActive);
                vm.RestartCommand.Execute(null);
                Assert.Equal(2, sim.LoadScenarioCalls);
                Assert.True(vm.IsCareerAttemptActive);

                vm.SelectedChallenge = vm.Challenges.First(c => c.Available);
                vm.StartChallengeCommand.Execute(null);
                Assert.Equal(3, sim.LoadScenarioCalls);
                Assert.False(vm.IsCareerAttemptActive);
                hud.UpdateLayout();
                Assert.Equal(
                    Visibility.Collapsed,
                    Assert.IsType<Border>(hud.FindName("CareerStatusStrip")).Visibility);
                Assert.True(string.IsNullOrWhiteSpace(bindingErrors.Errors), bindingErrors.Errors);
            }
            finally
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(bindingErrors);
                restoredSecondary?.Close();
                secondary?.Close();
                hud?.Close();
                vm.Dispose();
                app.Shutdown();
                if (File.Exists(scorePath)) File.Delete(scorePath);
                if (File.Exists(careerPath)) File.Delete(careerPath);
                if (File.Exists(positionPath)) File.Delete(positionPath);
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

    private sealed class BindingErrorListener : TraceListener
    {
        private readonly StringBuilder _errors = new();
        public string Errors => _errors.ToString();
        public override void Write(string? message) => _errors.Append(message);
        public override void WriteLine(string? message) => _errors.AppendLine(message);
    }
}
