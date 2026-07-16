using System.IO;
using System.Windows;
using ChallengeLab.App.ViewModels;
using ChallengeLab.Core.Career;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.SimConnect;

namespace ChallengeLab.App.Tests;

[Collection("Wpf")]
public sealed class CareerModeUiTests
{
    [Fact]
    public void FreshCareer_IsFirstAndClassified_ThenAcceptanceRevealsPersistedMission()
    {
        RunSta(() =>
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "ChallengeLabCareerUi", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var sim = new FakeSimBridge();
            var vm = new MainViewModel(
                sim,
                new ConfigLoader(FindConfig()),
                new HighscoreStore(Path.Combine(tempDirectory, "highscores.json")),
                new CareerProgressStore(Path.Combine(tempDirectory, "career.json")),
                new FixedRandom(1));
            try
            {
                Assert.Equal(MainViewModel.CareerTabIndex, vm.SelectedTab);
                Assert.True(vm.IsCareerAvailable, vm.CareerConfigurationStatus);
                Assert.Equal("Cadet", vm.CareerCurrentRankTitle);
                Assert.Equal("0 / 5 PROMOTIONS", vm.CareerProgressText);
                Assert.False(vm.CareerHasAssignment);
                Assert.True(vm.CareerNeedsAssignment);
                Assert.Equal("", vm.CareerAssignmentTitle);
                Assert.Equal("", vm.CareerAssignmentAirportRunway);
                Assert.Equal("", vm.CareerAssignmentWeather);
                Assert.Equal("", vm.CareerAssignmentDescription);
                Assert.All(vm.CareerRewards, reward => Assert.False(reward.IsUnlocked));
                Assert.DoesNotContain(vm.Challenges, c => c.Id.StartsWith("career-", StringComparison.Ordinal));

                Assert.True(vm.AcceptCareerAssignmentCommand.CanExecute(null));
                vm.AcceptCareerAssignmentCommand.Execute(null);

                Assert.True(vm.CareerHasAssignment);
                Assert.False(vm.CareerNeedsAssignment);
                Assert.NotEmpty(vm.CareerAssignmentTitle);
                Assert.NotEmpty(vm.CareerAssignmentAirportRunway);
                Assert.NotEmpty(vm.CareerAssignmentWeather);
                Assert.NotEmpty(vm.CareerAssignmentDescription);
                Assert.False(vm.AcceptCareerAssignmentCommand.CanExecute(null));

                var loader = new ConfigLoader(FindConfig());
                var reloaded = new CareerProgressionService(
                    loader.LoadCatalog().Career!,
                    loader.LoadAllChallenges(),
                    new CareerProgressStore(Path.Combine(tempDirectory, "career.json")),
                    new FixedRandom(0));
                Assert.Equal(vm.CareerAssignmentTitle, reloaded.AcceptedAssignment!.Title);
            }
            finally
            {
                vm.Dispose();
                try { Directory.Delete(tempDirectory, recursive: true); }
                catch { /* best effort cleanup */ }
            }
        });
    }

    [Fact]
    public void HudMenu_OpensHighscoresWhileStartupRemainsCareer()
    {
        RunSta(() =>
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "ChallengeLabMenuUi", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var vm = new MainViewModel(
                new FakeSimBridge(),
                new ConfigLoader(FindConfig()),
                new HighscoreStore(Path.Combine(tempDirectory, "highscores.json")),
                new CareerProgressStore(Path.Combine(tempDirectory, "career.json")),
                new FixedRandom(0));
            try
            {
                Assert.Equal(MainViewModel.CareerTabIndex, vm.SelectedTab);
                vm.SelectedTab = MainViewModel.SessionTabIndex;
                var toggleRequested = false;
                vm.RequestToggleMain += () => toggleRequested = true;

                vm.OpenMenuCommand.Execute(null);

                Assert.True(toggleRequested);
                Assert.Equal(MainViewModel.HighscoresTabIndex, vm.SelectedTab);
            }
            finally
            {
                vm.Dispose();
                try { Directory.Delete(tempDirectory, recursive: true); }
                catch { /* best effort cleanup */ }
            }
        });
    }

    [Fact]
    public void CareerXaml_HasFirstTabMysteryRewardsAndOneWayReadOnlyProgressBindings()
    {
        var root = FindRepositoryRoot();
        var mainXaml = File.ReadAllText(Path.Combine(root, "src", "ChallengeLab.App", "Views", "MainWindow.xaml"));
        var hudXaml = File.ReadAllText(Path.Combine(root, "src", "ChallengeLab.App", "Views", "CompanionHudWindow.xaml"));
        var mainViewModel = File.ReadAllText(Path.Combine(root, "src", "ChallengeLab.App", "ViewModels", "MainViewModel.cs"));

        var careerTab = mainXaml.IndexOf("<TabItem Header=\"CAREER\">", StringComparison.Ordinal);
        var challengesTab = mainXaml.IndexOf("<TabItem Header=\"CHALLENGES\">", StringComparison.Ordinal);
        Assert.InRange(careerTab, 0, challengesTab - 1);
        Assert.Contains("CareerProgressPercent, Mode=OneWay", mainXaml, StringComparison.Ordinal);
        Assert.Contains("LoadProgress, Mode=OneWay", mainXaml, StringComparison.Ordinal);
        Assert.Contains("DisplayTitle", mainXaml, StringComparison.Ordinal);
        Assert.Contains("UNLOCKED · COMING SOON", File.ReadAllText(
            Path.Combine(root, "src", "ChallengeLab.App", "ViewModels", "CareerRewardSlotViewModel.cs")));
        Assert.Contains("CareerStatusStrip", hudXaml, StringComparison.Ordinal);
        Assert.Contains("CareerHudStatus, Mode=OneWay", hudXaml, StringComparison.Ordinal);
        Assert.Contains("LandingAttemptOrigin.CareerAssignment", mainViewModel, StringComparison.Ordinal);
        Assert.Contains("careerAttempt ? _careerAttemptStageNumber", mainViewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedPromotionAndCompletion_RevealOnlyEarnedPlaceholderCards()
    {
        RunSta(() =>
        {
            var tempDirectory = Path.Combine(Path.GetTempPath(), "ChallengeLabCareerUi", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var careerPath = Path.Combine(tempDirectory, "career.json");
            var store = new CareerProgressStore(careerPath);
            store.Save(new CareerProgressState
            {
                CompletedStageCount = 1,
                PreviousAssignmentId = "barcelona-crosswind-final",
                AttemptCount = 2,
                LastResult = new CareerOutcome
                {
                    Kind = CareerOutcomeKind.Passed,
                    ChallengeId = "barcelona-crosswind-final",
                    RankId = "cadet",
                    RankTitle = "Cadet",
                    IsRanked = true,
                    ScorePercent = 84.2,
                    Message = "PROMOTED — First Officer · 84.2%"
                }
            });

            var vm = CreateViewModel(tempDirectory, store);
            try
            {
                Assert.Equal("First Officer", vm.CareerCurrentRankTitle);
                Assert.Single(vm.CareerRewards, reward => reward.IsUnlocked);
                var unlockedCard = Assert.Single(vm.Challenges,
                    challenge => challenge.Id == "career-madeira-storm-corridor");
                Assert.False(unlockedCard.Available);
                Assert.Equal("COMING SOON", unlockedCard.StatusLabel);
                Assert.Contains("PROMOTED", vm.CareerLastOutcomeText, StringComparison.Ordinal);
            }
            finally
            {
                vm.Dispose();
            }

            store.Save(new CareerProgressState
            {
                CompletedStageCount = 5,
                PreviousAssignmentId = "skiathos-short-final",
                AttemptCount = 7,
                LastResult = new CareerOutcome
                {
                    Kind = CareerOutcomeKind.Complete,
                    ChallengeId = "skiathos-short-final",
                    RankId = "command-captain",
                    RankTitle = "Command Captain",
                    IsRanked = true,
                    ScorePercent = 91,
                    Message = "CAREER COMPLETE — 91.0%"
                }
            });

            var complete = CreateViewModel(tempDirectory, store);
            try
            {
                Assert.True(complete.CareerIsComplete);
                Assert.False(complete.CareerNeedsAssignment);
                Assert.False(complete.AcceptCareerAssignmentCommand.CanExecute(null));
                Assert.All(complete.CareerRewards, reward => Assert.True(reward.IsUnlocked));
                Assert.Equal(5, complete.Challenges.Count(c => c.Id.StartsWith("career-", StringComparison.Ordinal)));
            }
            finally
            {
                complete.Dispose();
                try { Directory.Delete(tempDirectory, recursive: true); }
                catch { /* best effort cleanup */ }
            }
        });
    }

    private static MainViewModel CreateViewModel(string tempDirectory, CareerProgressStore store) => new(
        new FakeSimBridge(),
        new ConfigLoader(FindConfig()),
        new HighscoreStore(Path.Combine(tempDirectory, Guid.NewGuid().ToString("N") + ".json")),
        store,
        new FixedRandom(0));

    private static string FindConfig() => Path.Combine(FindRepositoryRoot(), "config");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChallengeLab.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root not found.");
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

    private sealed class FixedRandom(int value) : IRandomIndexProvider
    {
        public int Next(int exclusiveUpperBound) => value % exclusiveUpperBound;
    }

    private sealed class FakeSimBridge : ISimBridge
    {
        public SimConnectionState State { get; private set; } = SimConnectionState.Connected;
        public string? StatusMessage => State.ToString();
        public bool IsConnected => State == SimConnectionState.Connected;
        public int LoadScenarioCalls { get; private set; }

        public event EventHandler<SimConnectionState>? StateChanged;
        public event EventHandler<TelemetrySample>? TelemetryReceived { add { } remove { } }
        public event EventHandler<string>? LogMessage { add { } remove { } }

        public void SetState(SimConnectionState state)
        {
            State = state;
            StateChanged?.Invoke(this, state);
        }

        public void Connect(IntPtr windowHandle) { }
        public void Disconnect() => SetState(SimConnectionState.Disconnected);
        public void ReceiveMessage() { }
        public Task<IReadOnlyList<AirportFacility>> GetAirportsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AirportFacility>>([]);
        public Task<AirportRunwayFacility> GetAirportRunwaysAsync(AirportFacility airport, CancellationToken ct = default) =>
            Task.FromResult(new AirportRunwayFacility(airport, [], []));

        public Task<SpawnApplyResult> LoadScenarioAsync(
            ChallengeConfig challenge,
            string flightFileAbsolutePath,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            LoadScenarioCalls++;
            return Task.FromResult(SpawnApplyResult.Ok("ready", 0, 0, 0, 0, 0, 140, false));
        }

        public void ConfigureAircraft(AircraftSetupConfig setup) { }
        public void ApplyWeather(WeatherConfig weather) { }
        public void ApplyTimeOfDay(TimeOfDayConfig? timeOfDay) { }
        public void Teleport(SpawnConfig spawn) { }
        public void ResumeFlight() { }
        public void Dispose() { }
    }
}
