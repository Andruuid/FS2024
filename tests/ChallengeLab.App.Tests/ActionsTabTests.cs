using System.IO;
using ChallengeLab.App.ViewModels;
using ChallengeLab.Core.Career;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.FlightLoading;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Snapshots;
using ChallengeLab.SimConnect;

namespace ChallengeLab.App.Tests;

[Collection("Wpf")]
public sealed class ActionsTabTests
{
    [Theory]
    [InlineData("109.75")]
    [InlineData("/109.75")]
    [InlineData("109,75")]
    public void ManualSet_NormalizesAcceptedInputs_AndSendsFrequencyOnly(string input)
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;
            vm.IlsFrequencyInput = input;

            vm.SetIlsCommand.Execute(null);
            WaitFor(() => !vm.IsActionRunning);

            Assert.Equal(1, env.Sim.IlsCalls);
            Assert.Equal(109.75m, env.Sim.LastIlsFrequency);
            Assert.Null(env.Sim.LastIlsCourse);
            Assert.Contains("Course was not changed", vm.ActionsStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ManualSet_InvalidFrequencyReportsValidationWithoutSending()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;
            vm.IlsFrequencyInput = "117.90";

            vm.SetIlsCommand.Execute(null);

            Assert.Equal(0, env.Sim.IlsCalls);
            Assert.Contains("108.10 to 111.95", vm.ActionsStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Catalogue_ExactIcaoSelectsAirport_ShowsAmbiguity_AndSendsSelectedCourse()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;

            vm.IlsAirportQuery = "ksea";

            Assert.Single(vm.IlsAirportResults);
            Assert.NotNull(vm.SelectedIlsAirport);
            Assert.Equal("KSEA", vm.SelectedIlsAirport!.Icao);
            Assert.Equal(2, vm.SelectedIlsAirport.Runways.Count);
            Assert.NotNull(vm.SelectedIlsRunway);
            Assert.True(vm.HasIlsAmbiguity);
            Assert.Contains("RW16C 164°", vm.IlsAmbiguityWarning, StringComparison.Ordinal);

            vm.SetSelectedIlsCommand.Execute(null);
            WaitFor(() => !vm.IsActionRunning);

            Assert.Equal(111.70m, env.Sim.LastIlsFrequency);
            Assert.Equal(164, env.Sim.LastIlsCourse);
            Assert.Contains("KSEA RW16C", vm.ActionsStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ZurichIls_RestoresNewestMatchingSnapshotPaused_ThenTunesFrequencyAndCourse()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            env.SnapshotStore.Save(BuildSnapshot("ZurichRnw28", new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero)));
            env.SnapshotStore.Save(BuildSnapshot("ZurichRnw28", new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero)));
            env.SnapshotStore.Save(BuildSnapshot("Other", new DateTimeOffset(2026, 7, 22, 10, 0, 0, TimeSpan.Zero)));
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;

            vm.ZurichIlsCommand.Execute(null);
            WaitFor(() => !vm.IsActionRunning);

            Assert.Equal(1, env.Sim.RestoreCalls);
            Assert.Equal(new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
                env.Sim.LastRestoredSnapshot!.CreatedUtc);
            Assert.NotNull(env.Sim.LastRestoreOptions);
            Assert.True(env.Sim.LastRestoreOptions!.RestoreWeather);
            Assert.True(env.Sim.LastRestoreOptions.RestoreTime);
            Assert.True(env.Sim.LastRestoreOptions.RestoreFuel);
            Assert.True(env.Sim.LastRestoreOptions.RestoreEngines);
            Assert.True(env.Sim.LastRestoreOptions.RestoreLights);
            Assert.True(env.Sim.LastRestoreOptions.RestoreAutopilot);
            Assert.False(env.Sim.LastRestoreOptions.AutoResume);
            Assert.True(vm.IsSnapshotResumeReady);
            Assert.Equal(109.75m, env.Sim.LastIlsFrequency);
            Assert.Equal(273, env.Sim.LastIlsCourse);
            Assert.Contains("PAUSED", vm.ActionsStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ZurichIls_TuningFailureLeavesSuccessfulRestorePausedAndReportsPartialCompletion()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            env.SnapshotStore.Save(BuildSnapshot("ZurichRnw28", DateTimeOffset.UtcNow));
            env.Sim.IlsException = new InvalidOperationException("test MCDU failure");
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;

            vm.ZurichIlsCommand.Execute(null);
            WaitFor(() => !vm.IsActionRunning);

            Assert.Equal(1, env.Sim.RestoreCalls);
            Assert.True(vm.IsSnapshotResumeReady);
            Assert.Contains("remains PAUSED", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("test MCDU failure", vm.ActionsStatus, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ZurichResumeNow_ResumesImmediately_WaitsHalfSecond_ThenResendsIls()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            env.SnapshotStore.Save(BuildSnapshot("ZurichRnw28", DateTimeOffset.UtcNow));
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;

            vm.ZurichIlsCommand.Execute(null);
            WaitFor(() => !vm.IsActionRunning);
            Assert.Equal(1, env.Sim.IlsCalls);
            Assert.True(vm.ResumeNowCommand.CanExecute(null));

            vm.ResumeNowCommand.Execute(null);

            Assert.Equal(1, env.Sim.ResumeCalls);
            Assert.True(vm.IsActionRunning);
            Assert.False(vm.IsSnapshotResumeReady);
            Assert.False(vm.ResumeNowCommand.CanExecute(null));
            WaitFor(() => !vm.IsActionRunning);

            Assert.Equal(2, env.Sim.IlsCalls);
            Assert.Equal(109.75m, env.Sim.LastIlsFrequency);
            Assert.Equal(273, env.Sim.LastIlsCourse);
            Assert.NotNull(env.Sim.LastResumeUtc);
            Assert.True(
                env.Sim.IlsCallTimes[1] - env.Sim.LastResumeUtc.Value >= TimeSpan.FromMilliseconds(450),
                "The ILS retry was sent before the requested half-second delay elapsed.");
            Assert.Contains("sent again", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("flying", vm.ActionsStatus, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void RunningAction_DisablesOtherActionAndStoreRestoreCommands()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            env.Sim.IlsCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            env.SnapshotStore.Save(BuildSnapshot("Other", DateTimeOffset.UtcNow));
            using var vm = env.CreateViewModel();
            vm.RefreshSnapshotsCommand.Execute(null);
            vm.SelectedSnapshot = vm.Snapshots[0];
            vm.IsConnected = true;

            vm.SetIlsCommand.Execute(null);
            Assert.True(vm.IsActionRunning);
            Assert.False(vm.SetIlsCommand.CanExecute(null));
            Assert.False(vm.ZurichIlsCommand.CanExecute(null));
            Assert.False(vm.LoadFltCommand.CanExecute(null));
            Assert.False(vm.LoadSnapshotCommand.CanExecute(null));

            env.Sim.IlsCompletion.SetResult();
            WaitFor(() => !vm.IsActionRunning);
            Assert.True(vm.LoadSnapshotCommand.CanExecute(null));
        });
    }

    [Fact]
    public void LoadFlt_ConfirmsCallsFixedFileAndPersistsPartialReport()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            env.Sim.FlightLoadResult = new FlightLoadResult
            {
                Outcome = FlightLoadOutcome.PartialSuccess,
                FlightFilePath = env.FltPath,
                FlightFileSha256 = "ABC123",
                Message = "Flight state validated; weather unavailable.",
                FlightLoadedEventReceived = true,
                LoadedFilename = "andi1.flt",
                ConfirmedFlightStatePath = env.FltPath,
                ConsecutiveValidSamples = 3,
                ElapsedSeconds = 12.345,
                Target = new FltFileMetadata
                {
                    FilePath = env.FltPath,
                    AircraftTitle = "Airbus A320neo V2",
                    Latitude = 47.45,
                    Longitude = 8.56,
                    AltitudeFeet = 4464,
                    HeadingDegrees = 274,
                    AirspeedFeetPerSecond = 236,
                    AirspeedKts = 139.8261771,
                    OnGround = false
                },
                FinalObservation = new FlightLoadObservation
                {
                    AircraftTitle = "Airbus A320neo V2",
                    Latitude = 47.45,
                    Longitude = 8.56,
                    AltitudeFeet = 4464,
                    HeadingTrueDeg = 274,
                    AirspeedKts = 139.8,
                    OnGround = false
                },
                ValidationIssues = ["UseWeatherFile=False; weather was not loaded."],
                Timeline =
                [
                    new FlightLoadTimelineEntry
                    {
                        Stage = "Readiness",
                        Message = "3 consecutive target-matching samples."
                    }
                ],
                Weather = new FlightLoadWeatherAssessment
                {
                    Status = FlightLoadWeatherStatus.NotRequested,
                    PresetFile = "andi1.wpr",
                    InitialWindDirectionDeg = 210,
                    InitialWindVelocityKts = 8,
                    FinalWindDirectionDeg = 220,
                    FinalWindVelocityKts = 10
                }
            };
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;
            vm.ConfirmAction = (_, _) => true;

            vm.LoadFltCommand.Execute(null);
            WaitFor(() => !vm.IsActionRunning);

            Assert.Equal(1, env.Sim.FlightLoadCalls);
            Assert.Equal(Path.GetFullPath(env.FltPath), Path.GetFullPath(env.Sim.LastFlightLoadRequest!.FlightFilePath));
            Assert.Equal(FlightLoadPausePolicy.AutoRelease, env.Sim.LastFlightLoadRequest.PausePolicy);
            Assert.Equal(TimeSpan.FromSeconds(3), env.Sim.LastFlightLoadRequest.PauseReleaseTimeout);
            Assert.Equal(TimeSpan.FromMilliseconds(750), env.Sim.LastFlightLoadRequest.LiveSettleDelay);
            Assert.Equal(TimeSpan.FromSeconds(30), env.Sim.LastFlightLoadRequest.AcceptanceTimeout);
            Assert.Equal(TimeSpan.FromSeconds(180), env.Sim.LastFlightLoadRequest.ReadyTimeout);
            Assert.Equal(TimeSpan.FromSeconds(15), env.Sim.LastFlightLoadRequest.ValidationTimeout);
            Assert.Contains("FLT LOAD PARTIAL SUCCESS", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("Attempt:", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("IAS raw=236.000 ft/s", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("IAS converted=139.83 kt", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("FlightLoaded event=yes", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("consecutive matching samples=3", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("Weather: status=NotRequested", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("observed change=", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.Contains("Timeline:", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.NotNull(vm.LastFlightLoadReportPath);
            Assert.True(File.Exists(vm.LastFlightLoadReportPath));
            Assert.Contains(vm.LastFlightLoadReportPath!, vm.ActionsStatus, StringComparison.Ordinal);
            var report = env.ReportStore.Load(vm.LastFlightLoadReportPath!);
            Assert.Equal(FlightLoadOutcome.PartialSuccess, report.Outcome);
            Assert.Equal("andi1.wpr", report.Weather!.PresetFile);
        });
    }

    [Fact]
    public void CopyActionsStatus_CopiesTheCompleteSelectableMessage()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            using var vm = env.CreateViewModel();
            string? copied = null;
            vm.SetClipboardText = text => copied = text;

            vm.CopyActionsStatusCommand.Execute(null);

            Assert.Equal(vm.ActionsStatus, copied);
        });
    }

    [Fact]
    public void LoadFlt_DeclinedConfirmationSendsNothingAndWritesNoReport()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;
            vm.ConfirmAction = (_, _) => false;

            vm.LoadFltCommand.Execute(null);

            Assert.Equal(0, env.Sim.FlightLoadCalls);
            Assert.Empty(Directory.EnumerateFiles(env.ReportStore.DirectoryPath, "*.json"));
            Assert.Contains("cancelled", vm.ActionsStatus, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void LoadFlt_CancelStopsWaitPersistsReportAndImmediatelyReenablesActions()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            env.Sim.FlightLoadCompletion = new TaskCompletionSource<FlightLoadResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;
            vm.ConfirmAction = (_, _) => true;

            vm.LoadFltCommand.Execute(null);
            WaitFor(() => vm.IsFlightLoadRunning);

            Assert.True(vm.CancelFlightLoadCommand.CanExecute(null));
            Assert.False(vm.SetIlsCommand.CanExecute(null));
            vm.CancelFlightLoadCommand.Execute(null);
            WaitFor(() => !vm.IsActionRunning);

            Assert.False(vm.IsFlightLoadRunning);
            Assert.False(vm.CancelFlightLoadCommand.CanExecute(null));
            Assert.True(vm.SetIlsCommand.CanExecute(null));
            Assert.Contains("FLT LOAD CANCELLED", vm.ActionsStatus, StringComparison.Ordinal);
            Assert.NotNull(vm.LastFlightLoadReportPath);
            var report = env.ReportStore.Load(vm.LastFlightLoadReportPath!);
            Assert.Equal(FlightLoadOutcome.Cancelled, report.Outcome);
            Assert.True(report.LoadIssued);
            Assert.False(report.LoadAccepted);
        });
    }

    [Fact]
    public void LoadFlt_ConfirmationWarnsThatPauseIsBrieflyReleasedWithoutAutomaticRepause()
    {
        RunSta(() =>
        {
            using var env = new TestEnv();
            using var vm = env.CreateViewModel();
            vm.IsConnected = true;
            string? confirmation = null;
            vm.ConfirmAction = (message, _) =>
            {
                confirmation = message;
                return false;
            };

            vm.LoadFltCommand.Execute(null);

            Assert.Contains("Normal Pause or Active Pause", confirmation, StringComparison.Ordinal);
            Assert.Contains("approximately one second", confirmation, StringComparison.Ordinal);
            Assert.Contains("never retried or automatically re-paused", confirmation, StringComparison.Ordinal);
            Assert.Contains("current simulator clock is preserved", confirmation, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ActionsXaml_IsImmediatelyAfterStore_AndContainsExpectedBindings()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.App", "Views", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.App", "ViewModels", "MainViewModel.cs"));

        var store = xaml.IndexOf("<TabItem Header=\"STORE\">", StringComparison.Ordinal);
        var actions = xaml.IndexOf("<TabItem Header=\"ACTIONS\">", StringComparison.Ordinal);
        Assert.InRange(store, 0, actions - 1);
        Assert.DoesNotContain("<TabItem Header=", xaml[(store + 24)..actions], StringComparison.Ordinal);
        Assert.Contains("ZurichIlsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SetIlsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("SetSelectedIlsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("LoadFltCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("CancelFlightLoadCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("IsFlightLoadRunning", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenFlightLoadReportsFolderCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("CopyActionsStatusCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FlightLoadReportsFolderPath", xaml, StringComparison.Ordinal);
        Assert.Contains("briefly releases Normal Pause or Active Pause", xaml, StringComparison.Ordinal);
        Assert.Contains("FlightLoadTimeNotice", xaml, StringComparison.Ordinal);
        Assert.Contains("IlsAmbiguityWarning", xaml, StringComparison.Ordinal);
        Assert.Equal(2, xaml.Split("Command=\"{Binding ResumeNowCommand}\"", StringSplitOptions.None).Length - 1);
        Assert.Contains("waits 0.5 seconds, then resends ILS", xaml, StringComparison.Ordinal);
        Assert.Contains("public const int ActionsTabIndex = 6;", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void FlightLoadCall_IsIsolatedFromSafeChallengeAndSnapshotPipelines()
    {
        var root = FindRepositoryRoot();
        var diagnostic = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.SimConnect", "SimConnectClient.FlightLoad.cs"));
        var safeChallenge = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.SimConnect", "SimConnectClient.cs"));
        var snapshot = File.ReadAllText(Path.Combine(
            root, "src", "ChallengeLab.SimConnect", "SimConnectClient.Snapshot.cs"));

        Assert.Equal(1, diagnostic.Split("_sim.FlightLoad(", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("_sim.FlightLoad(", safeChallenge, StringComparison.Ordinal);
        Assert.DoesNotContain("_sim.FlightLoad(", snapshot, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("andi1", true)]
    [InlineData("andi1.flt", true)]
    [InlineData("C:\\developer\\andi1", true)]
    [InlineData("final.flt", false)]
    [InlineData("other", false)]
    public void FlightLoadFlowCorrelation_AcceptsTargetStemAndRejectsUnrelatedLoads(
        string flowIdentity,
        bool expected)
    {
        Assert.Equal(expected, SimConnectClient.FlowIdentityMatches(
            "C:\\developer\\andi1.flt", flowIdentity));
    }

    [Theory]
    [InlineData(0u, false, false)]
    [InlineData(1u, true, false)]
    [InlineData(2u, true, false)]
    [InlineData(4u, false, true)]
    [InlineData(8u, true, false)]
    [InlineData(5u, true, true)]
    [InlineData(12u, true, true)]
    public void FlightLoadPauseFlags_DistinguishNormalAndActivePause(
        uint flags,
        bool normal,
        bool active)
    {
        Assert.Equal(normal, SimConnectClient.HasDiagnosticNormalPause(flags));
        Assert.Equal(active, SimConnectClient.HasDiagnosticActivePause(flags));
    }

    [Fact]
    public void FlightLoadOperationalProbe_RejectsPauseDialogDisabledSimAndDisabledInput()
    {
        var live = new FlightLoadObservation
        {
            PauseStateAvailable = true,
            PauseStateFlags = 0,
            SimDisabled = false,
            UserInputEnabled = true
        };

        Assert.True(SimConnectClient.IsDiagnosticOperationalProbe(live, simRunning: true, dialogMode: false));
        Assert.False(SimConnectClient.IsDiagnosticOperationalProbe(
            live with { PauseStateFlags = 1, NormalPauseActive = true }, true, false));
        Assert.False(SimConnectClient.IsDiagnosticOperationalProbe(
            live with { PauseStateFlags = 4, ActivePauseActive = true }, true, false));
        Assert.False(SimConnectClient.IsDiagnosticOperationalProbe(live, true, true));
        Assert.False(SimConnectClient.IsDiagnosticOperationalProbe(live, false, false));
        Assert.False(SimConnectClient.IsDiagnosticOperationalProbe(
            live with { SimDisabled = true }, true, false));
        Assert.False(SimConnectClient.IsDiagnosticOperationalProbe(
            live with { UserInputEnabled = false }, true, false));
        Assert.False(SimConnectClient.IsDiagnosticOperationalProbe(
            live with { MotionSimulationActive = false }, true, false));
    }

    [Fact]
    public void FlightLoadPhases_CannotMoveBackward()
    {
        var phase = FlightLoadPhase.Preflight;
        foreach (var next in new[]
                 {
                     FlightLoadPhase.ReleasingPause,
                     FlightLoadPhase.RequestSent,
                     FlightLoadPhase.LoadAccepted,
                     FlightLoadPhase.AwaitingReady,
                     FlightLoadPhase.ValidatingState,
                     FlightLoadPhase.Operational,
                     FlightLoadPhase.Completed
                 })
        {
            phase = SimConnectClient.AdvanceDiagnosticPhase(phase, next);
            Assert.Equal(next, phase);
            Assert.Equal(phase, SimConnectClient.AdvanceDiagnosticPhase(phase, FlightLoadPhase.Preflight));
        }
    }

    [Fact]
    public void FlightLoadTimeouts_ClassifyAcceptanceReadyAndValidationStages()
    {
        var origin = DateTimeOffset.Parse("2026-07-21T12:00:00Z");
        var request = new FlightLoadRequest
        {
            FlightFilePath = "andi1.flt",
            AcceptanceTimeout = TimeSpan.FromSeconds(30),
            ReadyTimeout = TimeSpan.FromSeconds(180),
            ValidationTimeout = TimeSpan.FromSeconds(15)
        };

        Assert.Null(SimConnectClient.DetermineDiagnosticTimeoutOutcome(
            origin.AddSeconds(29), origin, null, null, null, false, request));
        Assert.Equal(FlightLoadOutcome.TimedOut, SimConnectClient.DetermineDiagnosticTimeoutOutcome(
            origin.AddSeconds(30), origin, null, null, null, false, request));

        var accepted = origin.AddSeconds(5);
        Assert.Null(SimConnectClient.DetermineDiagnosticTimeoutOutcome(
            accepted.AddSeconds(179), origin, accepted, null, null, false, request));
        Assert.Equal(FlightLoadOutcome.LoadedAwaitingReady,
            SimConnectClient.DetermineDiagnosticTimeoutOutcome(
                accepted.AddSeconds(180), origin, accepted, null, null, false, request));

        var started = accepted.AddSeconds(20);
        Assert.Equal(FlightLoadOutcome.LoadedAwaitingReady,
            SimConnectClient.DetermineDiagnosticTimeoutOutcome(
                started.AddSeconds(15), origin, accepted, started, null, false, request));
        Assert.Equal(FlightLoadOutcome.PartialSuccess,
            SimConnectClient.DetermineDiagnosticTimeoutOutcome(
                started.AddSeconds(15), origin, accepted, started, started, true, request));
    }

    [Fact]
    public void FlightLoadSource_ReleasesActiveThenNormalPauseBeforeSingleRequestAndNeverFreezesPose()
    {
        var path = Path.Combine(
            FindRepositoryRoot(), "src", "ChallengeLab.SimConnect", "SimConnectClient.FlightLoad.cs");
        var source = File.ReadAllText(path);
        var normalize = source.IndexOf("await NormalizeDiagnosticFlightLoadEntryAsync", StringComparison.Ordinal);
        var request = source.IndexOf("_sim.FlightLoad(", StringComparison.Ordinal);
        var activePauseOff = source.IndexOf("EnsureActivePauseOff();", StringComparison.Ordinal);
        var normalPauseOff = source.IndexOf("PauseSim(false);", activePauseOff, StringComparison.Ordinal);

        Assert.InRange(normalize, 0, request - 1);
        Assert.InRange(activePauseOff, 0, normalPauseOff - 1);
        Assert.Equal(2, source.Split("PauseSim(false);", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("FreezePosition", source, StringComparison.Ordinal);
        Assert.DoesNotContain("FreezeAttitude", source, StringComparison.Ordinal);
        Assert.Contains("RestoreDiagnosticOriginalPause", source, StringComparison.Ordinal);
    }

    [Fact]
    public void McduSequence_IsExactForFrequencyOnlyAndFrequencyWithCourse()
    {
        Assert.Equal(
            [
                "L:INI_MCDU1_RADNAV", "L:INI_MCDU1_SLASH", "L:INI_MCDU1_1",
                "L:INI_MCDU1_0", "L:INI_MCDU1_9", "L:INI_MCDU1_DECIMAL",
                "L:INI_MCDU1_7", "L:INI_MCDU1_5", "L:INI_MCDU1_LSK3L"
            ],
            McduIlsCommandBuilder.EffectiveLVars(109.75m, null));

        Assert.Equal(
            [
                "L:INI_MCDU1_RADNAV", "L:INI_MCDU1_SLASH", "L:INI_MCDU1_1",
                "L:INI_MCDU1_0", "L:INI_MCDU1_9", "L:INI_MCDU1_DECIMAL",
                "L:INI_MCDU1_7", "L:INI_MCDU1_5", "L:INI_MCDU1_LSK3L",
                "L:INI_MCDU1_2", "L:INI_MCDU1_7", "L:INI_MCDU1_3", "L:INI_MCDU1_LSK4L"
            ],
            McduIlsCommandBuilder.EffectiveLVars(109.75m, 273));

        Assert.True(SimConnectClient.IsMcduIlsAircraft("A320neo V2"));
        Assert.True(SimConnectClient.IsMcduIlsAircraft("Microsoft A320neo V2"));
        Assert.False(SimConnectClient.IsMcduIlsAircraft("A330-200 (RR)"));
    }

    private static FlightStateSnapshot BuildSnapshot(string name, DateTimeOffset createdUtc) => new()
    {
        CreatedUtc = createdUtc,
        Name = name,
        AircraftTitle = "A320neo V2",
        Latitude = 47.4647,
        Longitude = 8.5492,
        AltitudeFeet = 1416,
        HeadingTrueDeg = 273,
        OnGround = true,
        FlapsHandleCount = 4
    };

    private sealed class TestEnv : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "ChallengeLabActionsUi", Guid.NewGuid().ToString("N"));

        public TestEnv()
        {
            Directory.CreateDirectory(_directory);
            SnapshotStore = new SnapshotStore(Path.Combine(_directory, "snapshots"));
            ReportStore = new FlightLoadReportStore(Path.Combine(_directory, "flight-load-tests"));
            var ils = Path.Combine(_directory, "ils.csv");
            var airports = Path.Combine(_directory, "airports.csv");
            File.WriteAllText(ils,
                "icao,elevation_ft,heading_deg,frequency_mhz,range_nm,ident,runway\n" +
                "LSZH,1416,273.01172,109.75,27,IZW,ILS RW28\n" +
                "KSEA,433,164.2,111.70,27,IUC,ILS RW16C\n" +
                "KSEA,433,344.2,111.70,27,IUC,ILS RW34C\n");
            File.WriteAllText(airports,
                "id,ident,type,name,latitude_deg,longitude_deg,iso_country,municipality,icao_code,gps_code,iata_code\n" +
                "1,LSZH,large_airport,Zurich Airport,47.46,8.55,CH,Zurich,LSZH,LSZH,ZRH\n" +
                "2,KSEA,large_airport,Seattle-Tacoma,47.45,-122.31,US,Seattle,KSEA,KSEA,SEA\n");
            Catalog = IlsFrequencyCatalog.Load(ils, airports);
        }

        public FakeSimBridge Sim { get; } = new();
        public SnapshotStore SnapshotStore { get; }
        public IlsFrequencyCatalog Catalog { get; }
        public FlightLoadReportStore ReportStore { get; }
        public string FltPath { get; } = Path.Combine(
            FindRepositoryRoot(), "data", "FltFiles", "andi1.flt");

        public MainViewModel CreateViewModel()
        {
            return new MainViewModel(
                Sim,
                new ConfigLoader(FindConfig()),
                new HighscoreStore(Path.Combine(_directory, "highscores.json")),
                new CareerProgressStore(Path.Combine(_directory, "career.json")),
                careerRandom: null,
                runwayCatalog: null,
                snapshotStore: SnapshotStore,
                ilsFrequencyCatalog: Catalog,
                flightLoadReportStore: ReportStore,
                diagnosticFltPath: FltPath);
        }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }

    private sealed class FakeSimBridge : ISimBridge
    {
        public SimConnectionState State => SimConnectionState.Connected;
        public string? StatusMessage => "Connected";
        public bool IsConnected => true;
        public int RestoreCalls { get; private set; }
        public FlightStateSnapshot? LastRestoredSnapshot { get; private set; }
        public SnapshotRestoreOptions? LastRestoreOptions { get; private set; }
        public int IlsCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public DateTimeOffset? LastResumeUtc { get; private set; }
        public List<DateTimeOffset> IlsCallTimes { get; } = new();
        public decimal? LastIlsFrequency { get; private set; }
        public int? LastIlsCourse { get; private set; }
        public Exception? IlsException { get; set; }
        public TaskCompletionSource? IlsCompletion { get; set; }
        public int FlightLoadCalls { get; private set; }
        public FlightLoadRequest? LastFlightLoadRequest { get; private set; }
        public FlightLoadResult? FlightLoadResult { get; set; }
        public TaskCompletionSource<FlightLoadResult>? FlightLoadCompletion { get; set; }

        public event EventHandler<SimConnectionState>? StateChanged { add { } remove { } }
        public event EventHandler<TelemetrySample>? TelemetryReceived { add { } remove { } }
        public event EventHandler<string>? LogMessage { add { } remove { } }
        public void Connect(IntPtr windowHandle) { }
        public void Disconnect() { }
        public void ReceiveMessage() { }
        public Task<IReadOnlyList<AirportFacility>> GetAirportsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<AirportFacility>>([]);
        public Task<AirportRunwayFacility> GetAirportRunwaysAsync(
            AirportFacility airport, CancellationToken ct = default) =>
            Task.FromResult(new AirportRunwayFacility(airport, [], []));
        public Task<SpawnApplyResult> LoadScenarioAsync(
            ChallengeConfig challenge, string flightFileAbsolutePath,
            IProgress<string>? progress = null, CancellationToken ct = default) =>
            Task.FromResult(SpawnApplyResult.Ok("ready", 0, 0, 0, 0, 0, 140, false));
        public void ConfigureAircraft(AircraftSetupConfig setup) { }
        public void ApplyWeather(WeatherConfig weather) { }
        public void ApplyTimeOfDay(TimeOfDayConfig? timeOfDay) { }
        public void Teleport(SpawnConfig spawn) { }
        public void ResumeFlight()
        {
            ResumeCalls++;
            LastResumeUtc = DateTimeOffset.UtcNow;
        }
        public Task<FlightStateSnapshot?> CaptureSnapshotAsync(CancellationToken ct = default) =>
            Task.FromResult<FlightStateSnapshot?>(null);

        public Task<SpawnApplyResult> RestoreSnapshotAsync(
            FlightStateSnapshot snapshot, SnapshotRestoreOptions options,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            RestoreCalls++;
            LastRestoredSnapshot = snapshot;
            LastRestoreOptions = options;
            return Task.FromResult(SpawnApplyResult.Ok(
                "restored", 0, 0, 0, snapshot.Latitude, snapshot.Longitude,
                snapshot.AltitudeFeet, snapshot.OnGround));
        }

        public Task SetMcduIlsAsync(
            decimal frequencyMhz, int? courseDegrees = null,
            IProgress<string>? progress = null, CancellationToken ct = default)
        {
            IlsCalls++;
            IlsCallTimes.Add(DateTimeOffset.UtcNow);
            LastIlsFrequency = frequencyMhz;
            LastIlsCourse = courseDegrees;
            if (IlsException is not null)
                return Task.FromException(IlsException);
            return IlsCompletion?.Task ?? Task.CompletedTask;
        }

        public async Task<FlightLoadResult> LoadFlightFileAsync(
            FlightLoadRequest request,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            FlightLoadCalls++;
            LastFlightLoadRequest = request;
            progress?.Report("Test FLT load…");
            if (FlightLoadCompletion is not null)
            {
                using var registration = ct.Register(() => FlightLoadCompletion.TrySetResult(
                    new FlightLoadResult
                    {
                        Outcome = FlightLoadOutcome.Cancelled,
                        FlightFilePath = request.FlightFilePath,
                        LoadIssued = true,
                        Message = "Stopped waiting after FlightLoad was issued; the simulator load cannot be undone."
                    }));
                return await FlightLoadCompletion.Task;
            }

            return FlightLoadResult ?? new FlightLoadResult
            {
                Outcome = FlightLoadOutcome.Succeeded,
                FlightFilePath = request.FlightFilePath,
                Message = "test"
            };
        }

        public void Dispose() { }
    }

    private static void WaitFor(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, 2_000), "Timed out waiting for asynchronous action.");

    private static string FindConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "config");
            if (File.Exists(Path.Combine(path, "catalog.json"))) return path;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("config not found");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "ChallengeLab.App")))
                return directory.FullName;
            directory = directory.Parent;
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
