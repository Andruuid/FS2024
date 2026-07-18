using System.Windows;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using ChallengeLab.App.Controls;
using ChallengeLab.App.ViewModels;
using ChallengeLab.App.Views;
using ChallengeLab.Core.Career;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
using ChallengeLab.SimConnect;

namespace ChallengeLab.App.Tests;

[Collection("Wpf")]
public sealed class FreeFlightHudModeTests
{
    private static void VerifyIncrementalFacilityLoadingAndClear()
    {
            var scorePath = Path.Combine(Path.GetTempPath(), $"challenge-lab-{Guid.NewGuid():N}.json");
            var careerPath = Path.Combine(Path.GetTempPath(), $"challenge-lab-career-{Guid.NewGuid():N}.json");
            var block = new AirportFacility("BLOCK", "ZZ", .01, -.15, 0);
            var good = new AirportFacility("GOOD", "ZZ", 0, 0, 0);
            var alternate = new AirportFacility("ALT", "ZZ", 0, .02, 0);
            var sim = new FakeSimBridge
            {
                Airports = [block, good, alternate],
                BlockedAirportDetails = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "BLOCK" },
                AirportDetails = new Dictionary<string, AirportRunwayFacility>(StringComparer.OrdinalIgnoreCase)
                {
                    ["GOOD"] = Detail(good),
                    ["ALT"] = Detail(alternate)
                }
            };
            var vm = new MainViewModel(
                sim,
                new ConfigLoader(FindConfig()),
                new HighscoreStore(scorePath),
                new CareerProgressStore(careerPath));

            try
            {
                sim.SetState(SimConnectionState.Connected);
                sim.EmitTelemetry(ApproachSample(
                    "Airbus A320 neo Asobo",
                    longitude: -.15,
                    heading: 90,
                    gearHandlePosition: 1));

                WaitUntil(
                    () => vm.FreeAirportStatus.Contains("Likely GOOD RWY 09", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(3));
                Assert.Contains("Likely GOOD RWY 09", vm.FreeAirportStatus, StringComparison.Ordinal);
                Assert.Contains("BLOCK", sim.RequestedAirportDetails);
                Assert.Contains("GOOD", sim.RequestedAirportDetails);
                AssertNoSimulatorMutation(sim);

                vm.CleanMetricsCommand.Execute(null);

                WaitUntil(
                    () => vm.FreeAirportStatus.Contains("Likely ALT RWY 09", StringComparison.Ordinal),
                    TimeSpan.FromSeconds(3));
                Assert.Contains("Likely ALT RWY 09", vm.FreeAirportStatus, StringComparison.Ordinal);
                AssertNoSimulatorMutation(sim);
            }
            finally
            {
                vm.Dispose();
                if (File.Exists(scorePath)) File.Delete(scorePath);
                if (File.Exists(careerPath)) File.Delete(careerPath);
            }
    }

    [Fact]
    public void ModeTransitions_CancelDetectionAndNeverMutateSimulator()
    {
        RunSta(() =>
        {
            var app = new ChallengeLab.App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();
            VerifyIncrementalFacilityLoadingAndClear();
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
                // Free is the default HUD mode; Normal is entered only when a challenge loads.
                Assert.True(vm.IsFreeMode);
                Assert.False(vm.IsNormalMode);

                var showHudCount = 0;
                vm.RequestShowHud += () => showHudCount++;
                sim.SetState(SimConnectionState.Connected);
                Assert.Equal(1, showHudCount);

                sim.EmitTelemetry(ApproachSample());

                Assert.True(vm.IsFreeMode);
                Assert.False(vm.RestartCommand.CanExecute(null));
                Assert.True(vm.CleanMetricsCommand.CanExecute(null));
                // Free is default: connect starts the 1s inference timer; wait for the first scan.
                WaitUntil(() => sim.AirportCatalogRequested, TimeSpan.FromSeconds(3));
                Assert.True(sim.AirportCatalogRequested);
                AssertNoSimulatorMutation(sim);

                InvokeArmFreeFlightSession(vm, ApproachSample());
                Assert.Equal("Detecting", vm.PhaseLabel);
                Assert.Contains("waiting for", vm.HudTip, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("aircraft TITLE", vm.SpeedTargetInfo, StringComparison.Ordinal);

                InvokeArmFreeFlightSession(vm, ApproachSample("Airbus A320 neo Asobo"));
                Assert.Contains("Armed", vm.PhaseLabel, StringComparison.Ordinal);
                Assert.Contains("VAPP 136", vm.SpeedTargetInfo, StringComparison.Ordinal);

                sim.EmitTelemetry(ApproachSample());
                Assert.NotEqual("Detecting", vm.PhaseLabel);

                sim.EmitTelemetry(ApproachSample("  airbus a320 NEO asobo  "));
                Assert.NotEqual("Detecting", vm.PhaseLabel);

                sim.EmitTelemetry(ApproachSample("Cessna 172 Skyhawk Asobo"));
                Assert.Equal("Detecting", vm.PhaseLabel);
                Assert.Contains("Aircraft changed", vm.HudTip, StringComparison.Ordinal);
                Assert.Contains("Cessna 172 Skyhawk Asobo", vm.HudTip, StringComparison.Ordinal);
                Assert.StartsWith("Optimal landing speed:", vm.SpeedTargetInfo, StringComparison.Ordinal);
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
                Assert.Null(secondary.FindName("AirspeedCard"));
                Assert.NotNull(secondary.FindName("PathPositionCard"));
                Assert.NotNull(secondary.FindName("DescentAngleCard"));
                Assert.NotNull(secondary.FindName("VerticalSpeedCard"));
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

    private static void InvokeArmFreeFlightSession(MainViewModel vm, TelemetrySample sample)
    {
        var method = typeof(MainViewModel).GetMethod(
            "ArmFreeFlightSession",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MainViewModel), "ArmFreeFlightSession");
        var airport = new AirportFacility("TEST", "ZZ", 0, 0, 10);
        var runway = new RunwayEndFacility(airport, "09", 0, 0, 10, 90, 2000, 45, 4, false);
        method.Invoke(vm, [new FreeFlightTarget(runway, 2, 0, 0), sample, CancellationToken.None, false]);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            // Pump WPF dispatcher so DispatcherTimer ticks can fire.
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                System.Windows.Threading.DispatcherPriority.Background,
                new Action(() => { }));
            Thread.Sleep(20);
        }
    }

    private static TelemetrySample ApproachSample(
        string? aircraftTitle = null,
        double longitude = -.04,
        double heading = 110,
        double gearHandlePosition = 1) => new()
    {
        Latitude = 0,
        Longitude = longitude,
        AltitudeFeet = 1000,
        AglFeet = 1000,
        RadioHeightFeet = 1000,
        AirspeedKts = 90,
        GroundSpeedKts = 90,
        HeadingTrueDeg = heading,
        SimOnGround = false,
        DesignSpeedVs0Kts = 45,
        AircraftTitle = aircraftTitle,
        IsGearWheels = true,
        IsGearRetractable = true,
        GearHandlePosition = gearHandlePosition
    };

    private static AirportRunwayFacility Detail(AirportFacility airport) => new(
        airport,
        [new RunwayFacility(
            CenterLatitude: airport.Latitude,
            CenterLongitude: airport.Longitude,
            AltitudeMeters: 0,
            HeadingTrueDeg: 90,
            LengthMeters: 2_000,
            WidthMeters: 45,
            Surface: 4,
            PrimaryNumber: 9,
            PrimaryDesignator: 0,
            SecondaryNumber: 27,
            SecondaryDesignator: 0,
            PrimaryClosed: false,
            SecondaryClosed: false,
            PrimaryLandingAllowed: true,
            SecondaryLandingAllowed: true)],
        [new RunwayStartFacility(airport.Latitude, airport.Longitude, 0, 90, 9, 0, 1)]);

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
        public IReadOnlyList<AirportFacility>? Airports { get; init; }
        public IReadOnlySet<string> BlockedAirportDetails { get; init; } = new HashSet<string>();
        public IReadOnlyDictionary<string, AirportRunwayFacility> AirportDetails { get; init; } =
            new Dictionary<string, AirportRunwayFacility>();
        public List<string> RequestedAirportDetails { get; } = [];
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
            return Airports ?? [new AirportFacility("TEST", "ZZ", 0, 0, 0)];
        }

        public async Task<AirportRunwayFacility> GetAirportRunwaysAsync(
            AirportFacility airport,
            CancellationToken ct = default)
        {
            RequestedAirportDetails.Add(airport.Icao);
            if (BlockedAirportDetails.Contains(airport.Icao))
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return AirportDetails.TryGetValue(airport.Icao, out var detail)
                ? detail
                : new AirportRunwayFacility(airport, [], []);
        }

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
