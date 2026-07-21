using System.Security.Cryptography;
using ChallengeLab.Core.FlightLoading;
using ChallengeLab.Core.Models;
using Microsoft.FlightSimulator.SimConnect;
using MsfsSc = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace ChallengeLab.SimConnect;

/// <summary>
/// Experimental ACTIONS-tab FLT loader. This file is deliberately isolated from the safe
/// challenge/snapshot apply pipelines. It sends FlightLoad exactly once and only from a
/// verified, live same-aircraft flight.
/// </summary>
public sealed partial class SimConnectClient
{
    private const uint NormalPauseMask = 1u | 2u | 8u;
    private const uint ActivePauseMask = 4u;

    private DiagnosticFlightLoadOperation? _diagnosticFlightLoad;
    private TaskCompletionSource<bool?>? _diagnosticSimStateTcs;
    private TaskCompletionSource<bool?>? _diagnosticDialogStateTcs;
    private FlightLoadObservation? _latestDiagnosticObservation;
    private bool _diagnosticFlightLoadSubscriptionsReady;

    public async Task<FlightLoadResult> LoadFlightFileAsync(
        FlightLoadRequest request,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var requestedUtc = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        FltFileMetadata? target = null;
        string? sha256 = null;
        FlightLoadStartState? startState = null;
        DiagnosticFlightLoadOperation? operation = null;

        if (_diagnosticFlightLoad is not null)
            return ImmediateResult(FlightLoadOutcome.Blocked, request.FlightFilePath,
                "Another diagnostic FLT load is already running.", requestedUtc, attemptId);
        if (!IsConnected || _sim is null)
            return ImmediateResult(FlightLoadOutcome.Failed, request.FlightFilePath,
                "Not connected to the simulator.", requestedUtc, attemptId);

        try
        {
            progress?.Report("Reading FLT metadata...");
            target = FltFileParser.Parse(request.FlightFilePath);
            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target.FilePath)));
            if (target.UseWeatherFile && !target.WeatherPresetExists)
            {
                var reason = $"The FLT requires weather preset '{target.WeatherPresetFile}', but it is missing beside the FLT.";
                Log($"Diagnostic FlightLoad BLOCKED: {reason}");
                return BuildImmediateResult(
                    FlightLoadOutcome.Blocked, target, sha256, startState,
                    reason, requestedUtc, attemptId);
            }

            progress?.Report("Checking live simulator state...");
            var systemState = await RequestDiagnosticSystemSnapshotAsync(ct);
            var mode = systemState?.SimRunning switch
            {
                true => FlightLoadSimulatorMode.ActiveFlight,
                false => FlightLoadSimulatorMode.MainMenu,
                _ => FlightLoadSimulatorMode.Unknown
            };
            var currentTitle = mode == FlightLoadSimulatorMode.ActiveFlight
                ? await RequestAircraftTitleAsync(ct)
                : null;
            var initialObservation = mode == FlightLoadSimulatorMode.ActiveFlight
                ? await WaitForFreshDiagnosticObservationAsync(
                    DateTimeOffset.UtcNow - TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2), ct)
                : null;
            startState = new FlightLoadStartState
            {
                SimulatorMode = mode,
                AircraftTitle = currentTitle,
                Observation = initialObservation,
                SimRunning = systemState?.SimRunning,
                DialogMode = systemState?.DialogMode
            };

            var safety = FlightLoadSafetyPolicy.Evaluate(target, mode, currentTitle);
            if (!safety.Allowed)
            {
                Log($"Diagnostic FlightLoad BLOCKED: {safety.Reason}");
                return BuildImmediateResult(
                    FlightLoadOutcome.Blocked, target, sha256, startState,
                    safety.Reason, requestedUtc, attemptId);
            }

            operation = new DiagnosticFlightLoadOperation(
                attemptId,
                requestedUtc,
                target,
                sha256,
                startState,
                new FlightLoadReadinessEvaluator(target, request.RequiredConsecutiveSamples),
                request,
                progress);
            operation.Add("Preflight", safety.Reason);
            operation.Add("Time", target.DateTime.Description);
            operation.Add("Weather", target.UseWeatherFile
                ? $"FLT requests '{target.WeatherPresetFile}'; visual verification remains required."
                : $"FLT metadata names '{target.WeatherPresetFile ?? "no preset"}' with UseWeatherFile=False; runtime weather remains visually unverified.");
            _diagnosticFlightLoad = operation;

            var normalization = await NormalizeDiagnosticFlightLoadEntryAsync(operation, ct);
            operation.PauseNormalization = normalization;
            if (!normalization.VerifiedUnpaused)
            {
                CompleteOperation(operation, FlightLoadOutcome.Blocked, normalization.Message);
                return await operation.Completion.Task;
            }

            EnsureDiagnosticFlightLoadSubscriptions();
            ct.ThrowIfCancellationRequested();
            progress?.Report("Sending one experimental FlightLoad request...");
            operation.RequestIssuedUtc = DateTimeOffset.UtcNow;

            // The managed API returns void. Do not retry: later events, system state and
            // operational telemetry determine the result.
            _sim.FlightLoad(target.FilePath);
            operation.LoadIssued = true;
            operation.Advance(
                FlightLoadPhase.RequestSent,
                "Request",
                $"FlightLoad('{target.FilePath}')");
            Log($"Diagnostic FlightLoad sent once: {target.FilePath}");

            while (!operation.Completion.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                ReceiveMessage();

                if (IsConnected
                    && DateTimeOffset.UtcNow - operation.LastSystemQueryUtc >= TimeSpan.FromSeconds(1))
                    RequestDiagnosticRuntimeStates(operation);

                EvaluateDiagnosticDeadlines(operation);
                if (!operation.Completion.Task.IsCompleted)
                    await Task.Delay(100, ct);
            }

            return await operation.Completion.Task;
        }
        catch (OperationCanceledException)
        {
            if (operation is not null)
            {
                if (!operation.LoadIssued)
                    RestoreDiagnosticOriginalPause(operation);
                var message = operation.LoadIssued
                    ? "Stopped waiting. The FLT request was already sent and cannot be undone; MSFS may continue loading."
                    : "Diagnostic FLT load cancelled before any simulator load request was sent.";
                CompleteOperation(operation, FlightLoadOutcome.Cancelled, message);
                return await operation.Completion.Task;
            }

            return BuildImmediateResult(
                FlightLoadOutcome.Cancelled, target, sha256, startState,
                "Diagnostic FLT load cancelled before any simulator load request was sent.",
                requestedUtc, attemptId, request.FlightFilePath);
        }
        catch (Exception ex)
        {
            Log($"Diagnostic FlightLoad error: {ex}");
            if (operation is not null)
            {
                if (!operation.LoadIssued)
                    RestoreDiagnosticOriginalPause(operation);
                operation.Add("Error", ex.Message);
                CompleteOperation(operation, FlightLoadOutcome.Failed, ex.Message);
                return await operation.Completion.Task;
            }

            return BuildImmediateResult(
                FlightLoadOutcome.Failed, target, sha256, startState,
                ex.Message, requestedUtc, attemptId, request.FlightFilePath);
        }
        finally
        {
            if (_diagnosticFlightLoad?.AttemptId == attemptId)
                _diagnosticFlightLoad = null;
        }
    }

    private async Task<FlightLoadPauseNormalization> NormalizeDiagnosticFlightLoadEntryAsync(
        DiagnosticFlightLoadOperation operation,
        CancellationToken ct)
    {
        var initial = operation.StartState.Observation;
        var initialFlags = initial?.PauseStateFlags ?? CurrentPauseFlags();
        var pauseDetected = initialFlags is > 0
                            || initial?.NormalPauseActive == true
                            || initial?.ActivePauseActive == true;
        var activeReleaseSent = false;
        var normalReleaseSent = false;
        var consecutiveUnpaused = 0;
        operation.PauseNormalization = new FlightLoadPauseNormalization
        {
            Policy = operation.Request.PausePolicy,
            InitialFlags = initialFlags,
            InitialDialogMode = operation.StartState.DialogMode,
            PauseWasDetected = pauseDetected,
            FinalFlags = initialFlags,
            FinalDialogMode = operation.StartState.DialogMode,
            Message = pauseDetected
                ? "Pause normalization started; no FlightLoad request has been sent."
                : "Entry is initially unpaused; no FlightLoad request has been sent."
        };

        if (initial is null || !initial.PauseStateAvailable || initialFlags is null)
        {
            return new FlightLoadPauseNormalization
            {
                Policy = operation.Request.PausePolicy,
                InitialFlags = initialFlags,
                InitialDialogMode = operation.StartState.DialogMode,
                PauseWasDetected = pauseDetected,
                Message = "Pause state is unavailable. No FlightLoad request was sent."
            };
        }

        if (pauseDetected && operation.Request.PausePolicy == FlightLoadPausePolicy.RequireUnpaused)
        {
            return new FlightLoadPauseNormalization
            {
                Policy = operation.Request.PausePolicy,
                InitialFlags = initialFlags,
                InitialDialogMode = operation.StartState.DialogMode,
                PauseWasDetected = true,
                FinalFlags = initialFlags,
                FinalDialogMode = operation.StartState.DialogMode,
                Message = "The simulator is paused. Resume it and try Load FLT again."
            };
        }

        if (pauseDetected)
        {
            operation.Advance(
                FlightLoadPhase.ReleasingPause,
                "Pause",
                $"Pause_EX1 flags={initialFlags}; releasing pause before FlightLoad.",
                "Pause detected; safely resuming before FLT load...");
            if (HasDiagnosticActivePause(initialFlags))
            {
                EnsureActivePauseOff();
                activeReleaseSent = true;
                operation.PauseNormalization = operation.PauseNormalization! with
                {
                    ActivePauseReleaseSent = true
                };
            }

            PauseSim(false);
            PauseSim(false);
            normalReleaseSent = true;
            operation.PauseNormalization = operation.PauseNormalization! with
            {
                NormalPauseReleaseSent = true
            };

            var deadline = DateTimeOffset.UtcNow + PositiveOrDefault(
                operation.Request.PauseReleaseTimeout, TimeSpan.FromSeconds(3));
            var lastTimestamp = DateTimeOffset.MinValue;
            while (DateTimeOffset.UtcNow < deadline && consecutiveUnpaused < 3)
            {
                ct.ThrowIfCancellationRequested();
                ReceiveMessage();
                var observation = _latestDiagnosticObservation;
                if (observation is not null && observation.TimestampUtc > lastTimestamp)
                {
                    lastTimestamp = observation.TimestampUtc;
                    consecutiveUnpaused = observation.PauseStateAvailable
                                          && observation.PauseStateFlags == 0
                                          && !observation.NormalPauseActive
                                          && !observation.ActivePauseActive
                        ? consecutiveUnpaused + 1
                        : 0;
                }
                if (consecutiveUnpaused < 3)
                    await Task.Delay(50, ct);
            }
        }
        else
        {
            consecutiveUnpaused = 3;
        }

        if (consecutiveUnpaused < 3)
        {
            var failed = new FlightLoadPauseNormalization
            {
                Policy = operation.Request.PausePolicy,
                InitialFlags = initialFlags,
                InitialDialogMode = operation.StartState.DialogMode,
                PauseWasDetected = pauseDetected,
                ActivePauseReleaseSent = activeReleaseSent,
                NormalPauseReleaseSent = normalReleaseSent,
                ConsecutiveUnpausedSamples = consecutiveUnpaused,
                FinalFlags = CurrentPauseFlags(),
                Message = "Pause could not be released and verified within 3 seconds. No FlightLoad request was sent."
            };
            operation.PauseNormalization = failed;
            RestoreDiagnosticOriginalPause(operation);
            return operation.PauseNormalization!;
        }

        if (pauseDetected)
        {
            var settleDelay = PositiveOrDefault(operation.Request.LiveSettleDelay, TimeSpan.FromMilliseconds(750));
            operation.Progress?.Report("Simulator resumed; allowing a short live-settle interval...");
            await PumpDiagnosticMessagesForAsync(settleDelay, ct);
        }

        var finalSystem = await RequestDiagnosticSystemSnapshotAsync(ct);
        var finalObservation = await WaitForFreshDiagnosticObservationAsync(
            DateTimeOffset.UtcNow - TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), ct);
        var finalFlags = finalObservation?.PauseStateFlags ?? CurrentPauseFlags();
        var entryProblem = EntryReadinessProblem(finalSystem, finalObservation, finalFlags);
        var result = new FlightLoadPauseNormalization
        {
            Policy = operation.Request.PausePolicy,
            InitialFlags = initialFlags,
            InitialDialogMode = operation.StartState.DialogMode,
            PauseWasDetected = pauseDetected,
            ActivePauseReleaseSent = activeReleaseSent,
            NormalPauseReleaseSent = normalReleaseSent,
            VerifiedUnpaused = entryProblem is null,
            ConsecutiveUnpausedSamples = consecutiveUnpaused,
            FinalFlags = finalFlags,
            FinalDialogMode = finalSystem?.DialogMode,
            Message = entryProblem ?? (pauseDetected
                ? "Pause released and live simulator entry verified."
                : "Simulator entry is live and unpaused.")
        };
        operation.PauseNormalization = result;

        if (entryProblem is not null)
        {
            RestoreDiagnosticOriginalPause(operation);
            return operation.PauseNormalization!;
        }

        operation.Add("Preflight", result.Message);
        return result;
    }

    private static string? EntryReadinessProblem(
        DiagnosticSystemSnapshot? systemState,
        FlightLoadObservation? observation,
        uint? pauseFlags)
    {
        if (systemState?.SimRunning != true)
            return "The simulator is not in a running flight. Resume/press Ready to Fly in MSFS, then try Load FLT again.";
        if (systemState.DialogMode != false)
            return "The MSFS ESC/dialog screen is still open. Resume/press Ready to Fly in MSFS, then try Load FLT again.";
        if (observation is null)
            return "Fresh flight telemetry was not received. No FlightLoad request was sent.";
        if (!observation.PauseStateAvailable || pauseFlags != 0)
            return "The simulator did not remain fully unpaused. No FlightLoad request was sent.";
        if (observation.SimDisabled == true)
            return "The simulator is disabled or awaiting Ready to Fly. Resume/press Ready to Fly in MSFS, then try Load FLT again.";
        if (observation.UserInputEnabled == false)
            return "User flight input is not enabled. Resume/press Ready to Fly in MSFS, then try Load FLT again.";
        return null;
    }

    private async Task<DiagnosticSystemSnapshot?> RequestDiagnosticSystemSnapshotAsync(CancellationToken ct)
    {
        if (_sim is null) return null;
        var simTcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogTcs = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticSimStateTcs = simTcs;
        _diagnosticDialogStateTcs = dialogTcs;
        _sim.RequestSystemState(Requests.DiagnosticSimState, "Sim");
        _sim.RequestSystemState(Requests.DiagnosticDialogState, "DialogMode");

        try
        {
            for (var i = 0; i < 60 && (!simTcs.Task.IsCompleted || !dialogTcs.Task.IsCompleted); i++)
            {
                ct.ThrowIfCancellationRequested();
                ReceiveMessage();
                await Task.Delay(50, ct);
            }

            var sim = simTcs.Task.IsCompleted ? await simTcs.Task : null;
            var dialog = dialogTcs.Task.IsCompleted ? await dialogTcs.Task : null;
            return new DiagnosticSystemSnapshot(sim, dialog);
        }
        finally
        {
            if (ReferenceEquals(_diagnosticSimStateTcs, simTcs))
                _diagnosticSimStateTcs = null;
            if (ReferenceEquals(_diagnosticDialogStateTcs, dialogTcs))
                _diagnosticDialogStateTcs = null;
        }
    }

    private async Task<FlightLoadObservation?> WaitForFreshDiagnosticObservationAsync(
        DateTimeOffset notBefore,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            ReceiveMessage();
            if (_latestDiagnosticObservation is { } observation && observation.TimestampUtc >= notBefore)
                return observation;
            await Task.Delay(50, ct);
        }
        return null;
    }

    private async Task PumpDiagnosticMessagesForAsync(TimeSpan duration, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + duration;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            ReceiveMessage();
            await Task.Delay(50, ct);
        }
    }

    private uint? CurrentPauseFlags()
    {
        lock (_pauseStateLock)
            return _pauseStateKnown ? _pauseStateFlags : null;
    }

    private void RestoreDiagnosticOriginalPause(DiagnosticFlightLoadOperation operation)
    {
        if (operation.LoadIssued || operation.PauseNormalization is not { } normalization
                                 || normalization.OriginalPauseRestored
                                 || normalization.InitialFlags is not { } initialFlags
                                 || initialFlags == 0)
            return;

        try
        {
            if (HasDiagnosticNormalPause(initialFlags))
            {
                PauseSim(true);
                PauseSim(true);
            }
            if (HasDiagnosticActivePause(initialFlags))
                EnsureActivePauseOnForDiagnostic();
            operation.PauseNormalization = normalization with { OriginalPauseRestored = true };
            operation.Add("Pause", $"Original pause flags {initialFlags} restored best-effort because FlightLoad was not sent.");
        }
        catch (Exception ex)
        {
            operation.Add("Pause", $"Could not restore the original pause state: {ex.Message}");
        }
    }

    private void EnsureActivePauseOnForDiagnostic()
    {
        if (_sim is null) return;
        EnsureEvents();
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER,
            Events.ActivePauseOn,
            0,
            Groups.Input,
            SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    private void EnsureDiagnosticFlightLoadSubscriptions()
    {
        if (_sim is null || _diagnosticFlightLoadSubscriptionsReady) return;
        try
        {
            _sim.SubscribeToSystemEvent(Events.DiagnosticFlightLoaded, "FlightLoaded");
            _sim.SubscribeToSystemEvent(Events.DiagnosticSimStart, "SimStart");
            _sim.SubscribeToSystemEvent(Events.DiagnosticSimStop, "SimStop");
        }
        catch (Exception ex)
        {
            Log($"Diagnostic FlightLoaded/simulator subscriptions unavailable: {ex.Message}");
        }
        try
        {
            _sim.SubscribeToFlowEvent();
        }
        catch (Exception ex)
        {
            Log($"Diagnostic flow-event subscription unavailable: {ex.Message}");
        }

        _diagnosticFlightLoadSubscriptionsReady = true;
    }

    private void OnRecvDiagnosticEventFilename(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_EVENT_FILENAME data)
    {
        if ((Events)data.uEventID != Events.DiagnosticFlightLoaded
            || _diagnosticFlightLoad is not { LoadIssued: true } operation)
            return;

        var path = data.szFileName?.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            operation.Add("FlightLoaded", "Event received without a filename; waiting for system-state confirmation.");
            return;
        }
        if (!PathsMatch(operation.Target.FilePath, path))
        {
            operation.AddChanged("event-path", "Correlation", $"Ignored FlightLoaded event for different file: {path}");
            return;
        }

        operation.SystemEventSeen = true;
        operation.LoadedFilename = path;
        operation.Add("FlightLoaded", path);
        MarkDiagnosticLoadedSignal(operation, path, "FlightLoaded event");
        RequestDiagnosticRuntimeStates(operation);
    }

    private void OnRecvDiagnosticFlowEvent(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_FLOW_EVENT data)
    {
        if (_diagnosticFlightLoad is not { LoadIssued: true } operation) return;
        var flowPath = data.FltPath?.Trim();
        switch (data.FlowEvent)
        {
            case SIMCONNECT_FLOW_EVENT.FLT_LOAD:
                if (FlowIdentityMatches(operation.Target.FilePath, flowPath))
                    operation.Add("Flow", $"FLT_LOAD {flowPath}".Trim());
                else
                    operation.AddChanged("flow-load-other", "Flow", $"Internal/unrelated FLT_LOAD {flowPath}".Trim());
                return;

            case SIMCONNECT_FLOW_EVENT.FLT_LOADED:
                if (!FlowIdentityMatches(operation.Target.FilePath, flowPath))
                {
                    operation.AddChanged("flow-loaded-other", "Correlation",
                        $"Ignored FLT_LOADED flow event for different file: {flowPath}");
                    return;
                }
                operation.FlowLoadedSeen = true;
                if (string.IsNullOrWhiteSpace(operation.LoadedFilename))
                    operation.LoadedFilename = flowPath;
                operation.Add("Flow", $"FLT_LOADED {flowPath}".Trim());
                return;

            case SIMCONNECT_FLOW_EVENT.FLIGHT_START:
                operation.FlightStartEventSeen = true;
                MarkDiagnosticFlightStarted(operation, "FLIGHT_START flow event");
                return;

            case SIMCONNECT_FLOW_EVENT.FLIGHT_END:
                operation.Add("Flow", "FLIGHT_END received during the load attempt.");
                return;
        }
    }

    private void ObserveDiagnosticSimulatorEvent(bool running)
    {
        if (_diagnosticFlightLoad is not { } operation || !operation.LoadIssued) return;
        operation.LatestSimRunning = running;
        operation.AddChanged("sim-event", "Simulator", running ? "SimStart" : "SimStop");
        if (running)
        {
            operation.SimStartEventSeen = true;
            MarkDiagnosticFlightStarted(operation, "SimStart system event");
        }
    }

    private void OnRecvDiagnosticSystemState(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_SYSTEM_STATE data)
    {
        if (data.dwRequestID == (uint)Requests.DiagnosticSimState)
        {
            var running = data.dwInteger != 0;
            _diagnosticSimStateTcs?.TrySetResult(running);
            if (_diagnosticFlightLoad is { } operation)
            {
                operation.LatestSimRunning = running;
                operation.AddChanged("sim-state", "SystemState", $"Sim={(running ? 1 : 0)}");
            }
            return;
        }

        if (data.dwRequestID == (uint)Requests.DiagnosticDialogState)
        {
            var dialog = data.dwInteger != 0;
            _diagnosticDialogStateTcs?.TrySetResult(dialog);
            if (_diagnosticFlightLoad is { } operation)
            {
                operation.LatestDialogMode = dialog;
                operation.AddChanged("dialog-state", "SystemState", $"DialogMode={(dialog ? 1 : 0)}");
            }
            return;
        }

        if (data.dwRequestID != (uint)Requests.DiagnosticFlightLoadedState
            || _diagnosticFlightLoad is not { LoadIssued: true } loadOperation)
            return;

        var path = data.szString?.Trim();
        loadOperation.ConfirmedFlightStatePath = path;
        loadOperation.PathStateResponded = true;
        loadOperation.PathConfirmed = PathsMatch(loadOperation.Target.FilePath, path);
        loadOperation.AddChanged("flight-path", "SystemState",
            loadOperation.PathConfirmed
                ? $"FlightLoaded confirmed: {path}"
                : $"FlightLoaded path mismatch: {path ?? "(empty)"}");
        if (loadOperation.PathConfirmed)
            MarkDiagnosticLoadedSignal(loadOperation, path, "FlightLoaded system state");
    }

    private void RequestDiagnosticRuntimeStates(DiagnosticFlightLoadOperation operation)
    {
        if (_sim is null || !ReferenceEquals(_diagnosticFlightLoad, operation)) return;
        try
        {
            operation.LastSystemQueryUtc = DateTimeOffset.UtcNow;
            _sim.RequestSystemState(Requests.DiagnosticFlightLoadedState, "FlightLoaded");
            _sim.RequestSystemState(Requests.DiagnosticSimState, "Sim");
            _sim.RequestSystemState(Requests.DiagnosticDialogState, "DialogMode");
        }
        catch (Exception ex)
        {
            operation.AddChanged("system-query-error", "SystemState", $"Runtime-state query failed: {ex.Message}");
        }
    }

    private void MarkDiagnosticLoadedSignal(
        DiagnosticFlightLoadOperation operation,
        string? path,
        string source)
    {
        if (!string.IsNullOrWhiteSpace(path) && !PathsMatch(operation.Target.FilePath, path))
        {
            operation.AddChanged("loaded-signal-mismatch", "Correlation", $"Ignored {source} for different file: {path}");
            return;
        }
        if (operation.LoadAcceptedUtc is not null) return;

        var now = DateTimeOffset.UtcNow;
        operation.LoadAcceptedUtc = now;
        operation.Evaluator.MarkFlightLoaded(now);
        operation.Advance(
            FlightLoadPhase.LoadAccepted,
            "LoadAccepted",
            source,
            "FLT accepted by the simulator.");
        if (operation.FlightStartedUtc is not null)
        {
            operation.Advance(
                FlightLoadPhase.ValidatingState,
                "Readiness",
                "Flight has started; validating the loaded state.",
                "Flight started; validating the loaded state...");
        }
        else
        {
            operation.Advance(
                FlightLoadPhase.AwaitingReady,
                "Readiness",
                "Waiting for Ready to Fly / live flight control.",
                "FLT loaded. If Ready to Fly is visible, click it; waiting for the flight to start.");
        }
    }

    private void MarkDiagnosticFlightStarted(DiagnosticFlightLoadOperation operation, string source)
    {
        if (!operation.LoadIssued || operation.FlightStartedUtc is not null) return;
        operation.FlightStartedUtc = DateTimeOffset.UtcNow;
        operation.Add("FlightStart", source);
        if (operation.LoadAcceptedUtc is not null)
        {
            operation.Advance(
                FlightLoadPhase.ValidatingState,
                "Readiness",
                "Flight start detected; validating target state and live control.",
                "Flight started; validating the loaded state...");
        }
    }

    private void ObserveDiagnosticFlightLoadTelemetry(TelemetrySample sample)
    {
        var observation = FlightLoadObservation.FromTelemetry(sample);
        _latestDiagnosticObservation = observation;
        if (_diagnosticFlightLoad is not { LoadAcceptedUtc: not null } operation) return;

        var wasReady = operation.TargetStateValidated;
        operation.Evaluator.Observe(observation);
        operation.MaxConsecutiveValidSamples = Math.Max(
            operation.MaxConsecutiveValidSamples,
            operation.Evaluator.ConsecutiveValidSamples);
        if (operation.Evaluator.IsReady && !operation.TargetStateValidated)
        {
            operation.TargetStateValidated = true;
            operation.TargetMatchedObservation = observation;
            operation.Add("TargetState",
                $"{operation.Evaluator.ConsecutiveValidSamples} consecutive target-matching samples.");
        }

        operation.LatestSimDisabled = observation.SimDisabled;
        operation.LatestUserInputEnabled = observation.UserInputEnabled;
        operation.LatestMotionSimulationActive = observation.MotionSimulationActive;

        var generalOperational = IsDiagnosticOperationalProbe(
            observation,
            operation.LatestSimRunning,
            operation.LatestDialogMode);
        var lifecycleSeen = operation.FlightStartedUtc is not null;
        var fallbackProbe = !lifecycleSeen;
        if (generalOperational && (lifecycleSeen || fallbackProbe))
        {
            operation.StableOperationalProbeUtc ??= observation.TimestampUtc;
            operation.ConsecutiveOperationalSamples++;
            var stableFor = observation.TimestampUtc - operation.StableOperationalProbeUtc.Value;
            if (operation.ConsecutiveOperationalSamples >= 3
                && (lifecycleSeen || stableFor >= TimeSpan.FromSeconds(2)))
            {
                operation.ControlOperational = true;
                operation.ControlOperationalUtc ??= observation.TimestampUtc;
                if (!lifecycleSeen)
                {
                    operation.UsedStableProbeFallback = true;
                    operation.Advance(
                        FlightLoadPhase.ValidatingState,
                        "ReadinessFallback",
                        "Two seconds of stable live-control probes substituted for a missing flight-start event.",
                        "Live flight control detected; validating target state...");
                }
            }
        }
        else
        {
            operation.ConsecutiveOperationalSamples = 0;
            operation.StableOperationalProbeUtc = null;
        }

        if (!wasReady && operation.TargetStateValidated)
            operation.Progress?.Report("Target FLT state validated; waiting for live flight control...");

        if (operation.TargetStateValidated && operation.ControlOperational)
        {
            operation.OperationalUtc ??= observation.TimestampUtc;
            operation.Advance(
                FlightLoadPhase.Operational,
                "Operational",
                "Target state and live-control probes are operational.",
                "FLT state is operational; completing report...");
            CompleteReadyOperation(operation);
        }
    }

    private void EvaluateDiagnosticDeadlines(DiagnosticFlightLoadOperation operation)
    {
        if (operation.Completion.Task.IsCompleted || operation.RequestIssuedUtc is null) return;
        var now = DateTimeOffset.UtcNow;
        var timeoutOutcome = DetermineDiagnosticTimeoutOutcome(
            now,
            operation.RequestIssuedUtc.Value,
            operation.LoadAcceptedUtc,
            operation.FlightStartedUtc,
            operation.ControlOperationalUtc,
            operation.ControlOperational,
            operation.Request);
        switch (timeoutOutcome)
        {
            case FlightLoadOutcome.TimedOut:
                CompleteOperation(operation, FlightLoadOutcome.TimedOut,
                    "No authoritative FlightLoaded event or confirmed loaded path was received within 30 seconds.");
                return;
            case FlightLoadOutcome.PartialSuccess:
                CompleteOperation(operation, FlightLoadOutcome.PartialSuccess,
                    "The flight became operational, but target-state validation remained incomplete.");
                return;
            case FlightLoadOutcome.LoadedAwaitingReady when
                operation.FlightStartedUtc is not null || operation.ControlOperationalUtc is not null:
                CompleteOperation(operation, FlightLoadOutcome.LoadedAwaitingReady,
                    "The FLT was accepted, but live flight control did not become operational after flight start.");
                return;
            case FlightLoadOutcome.LoadedAwaitingReady:
                CompleteOperation(operation, FlightLoadOutcome.LoadedAwaitingReady,
                    "The FLT was accepted, but MSFS did not reach verified live flight control before the Ready-to-Fly timeout.");
                return;
        }
    }

    private void OnDiagnosticFlightLoadConnectionOpened()
    {
        if (_diagnosticFlightLoad is not { LoadIssued: true } operation) return;
        if (operation.DisconnectedDuringLoad)
        {
            operation.ReconnectedDuringLoad = true;
            operation.Add("Connection", "SimConnect reconnected during the load attempt.");
            operation.Progress?.Report("Reconnected; confirming the loaded FLT...");
        }
        RequestDiagnosticRuntimeStates(operation);
    }

    private void OnDiagnosticFlightLoadConnectionClosing()
    {
        _diagnosticSimStateTcs?.TrySetResult(null);
        _diagnosticDialogStateTcs?.TrySetResult(null);
        _latestDiagnosticObservation = null;
        if (_diagnosticFlightLoad is not { LoadIssued: true } operation
            || operation.Completion.Task.IsCompleted
            || operation.DisconnectedDuringLoad)
            return;

        operation.DisconnectedDuringLoad = true;
        operation.Add("Connection", "SimConnect disconnected during the load attempt; waiting for auto-reconnect.");
        operation.Progress?.Report("Connection lost during load; waiting for auto-reconnect...");
    }

    private void ObserveDiagnosticFlightLoadException(SIMCONNECT_RECV_EXCEPTION data)
    {
        if (_diagnosticFlightLoad is not { LoadIssued: true } operation) return;
        operation.Add("SimConnectException",
            $"Code {data.dwException}, send {data.dwSendID}, index {data.dwIndex}");
        if (!operation.SystemEventSeen && !operation.FlowLoadedSeen && data.dwException is 20 or 23)
            CompleteOperation(operation, FlightLoadOutcome.Failed,
                $"SimConnect rejected the FLT load (exception {data.dwException}).");
    }

    private void CompleteReadyOperation(DiagnosticFlightLoadOperation operation)
    {
        var fullyCorrelated = operation.SystemEventSeen || operation.PathConfirmed;
        var outcome = fullyCorrelated && operation.TargetStateValidated
            ? FlightLoadOutcome.Succeeded
            : FlightLoadOutcome.PartialSuccess;
        var message = outcome == FlightLoadOutcome.Succeeded
            ? "FLT path, target state and live flight control were verified. Confirm avionics and weather visually in MSFS."
            : "The flight is operational, but event/path or target-state correlation was incomplete.";
        CompleteOperation(operation, outcome, message);
    }

    private void CompleteOperation(
        DiagnosticFlightLoadOperation operation,
        FlightLoadOutcome outcome,
        string message)
    {
        if (operation.Completion.Task.IsCompleted) return;
        var finalPhase = operation.Phase;
        operation.Add("Result", $"{outcome}: {message}");
        var final = operation.TargetMatchedObservation
                    ?? operation.Evaluator.LastObservation
                    ?? _latestDiagnosticObservation;
        var issues = operation.TargetStateValidated
            ? new List<string>()
            : new List<string>(operation.Evaluator.LastValidation?.Issues ?? Array.Empty<string>());
        if (!operation.SystemEventSeen && !operation.PathConfirmed && operation.LoadIssued)
            issues.Add("No authoritative target FlightLoaded event or system-state path was confirmed.");
        if (operation.Target.DateTime.Status == FlightLoadTimeStatus.Invalid)
            issues.Add("DateTimeSeason is present but incomplete or invalid; the simulator clock was not changed by Challenge Lab.");

        operation.Phase = FlightLoadPhase.Completed;
        operation.Completion.TrySetResult(new FlightLoadResult
        {
            AttemptId = operation.AttemptId,
            RequestedUtc = operation.RequestedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Outcome = outcome,
            Phase = FlightLoadPhase.Completed,
            FlightFilePath = operation.Target.FilePath,
            FlightFileSha256 = operation.Sha256,
            Target = operation.Target,
            StartState = operation.StartState,
            FinalObservation = final,
            LoadIssued = operation.LoadIssued,
            LoadAccepted = operation.LoadAcceptedUtc is not null,
            FlightLoadedEventReceived = operation.SystemEventSeen,
            LoadedFilename = operation.LoadedFilename,
            ConfirmedFlightStatePath = operation.ConfirmedFlightStatePath,
            DisconnectedDuringLoad = operation.DisconnectedDuringLoad,
            ReconnectedDuringLoad = operation.ReconnectedDuringLoad,
            ConsecutiveValidSamples = operation.MaxConsecutiveValidSamples,
            ElapsedSeconds = (DateTimeOffset.UtcNow - operation.RequestedUtc).TotalSeconds,
            Message = message,
            ValidationIssues = issues.Distinct(StringComparer.Ordinal).ToArray(),
            Timeline = operation.Timeline.ToArray(),
            Weather = BuildWeatherAssessment(operation.Target, operation.StartState.Observation, final),
            PauseNormalization = operation.PauseNormalization,
            OperationalReadiness = new FlightLoadOperationalReadiness
            {
                FinalPhase = finalPhase,
                LoadAcceptedUtc = operation.LoadAcceptedUtc,
                FlightStartedUtc = operation.FlightStartedUtc,
                OperationalUtc = operation.OperationalUtc,
                FlightStartEventReceived = operation.FlightStartEventSeen,
                SimStartEventReceived = operation.SimStartEventSeen,
                SimRunning = operation.LatestSimRunning,
                DialogMode = operation.LatestDialogMode,
                SimDisabled = operation.LatestSimDisabled,
                UserInputEnabled = operation.LatestUserInputEnabled,
                MotionSimulationActive = operation.LatestMotionSimulationActive,
                ConsecutiveOperationalSamples = operation.ConsecutiveOperationalSamples,
                UsedStableProbeFallback = operation.UsedStableProbeFallback
            }
        });
    }

    private static FlightLoadResult BuildImmediateResult(
        FlightLoadOutcome outcome,
        FltFileMetadata? target,
        string? sha256,
        FlightLoadStartState? startState,
        string message,
        DateTimeOffset requestedUtc,
        Guid attemptId,
        string? fallbackPath = null) => new()
    {
        AttemptId = attemptId,
        RequestedUtc = requestedUtc,
        CompletedUtc = DateTimeOffset.UtcNow,
        Outcome = outcome,
        Phase = FlightLoadPhase.Completed,
        FlightFilePath = target?.FilePath ?? fallbackPath ?? "",
        FlightFileSha256 = sha256,
        Target = target,
        StartState = startState,
        Message = message,
        ElapsedSeconds = (DateTimeOffset.UtcNow - requestedUtc).TotalSeconds,
        ValidationIssues = [message],
        Timeline = [new FlightLoadTimelineEntry { Stage = outcome.ToString(), Message = message }],
        Weather = target is null ? null : BuildWeatherAssessment(target, startState?.Observation, null),
        PauseNormalization = startState?.Observation is { } observation
            ? new FlightLoadPauseNormalization
            {
                InitialFlags = observation.PauseStateFlags,
                InitialDialogMode = startState.DialogMode,
                PauseWasDetected = observation.PauseStateFlags is > 0
                                   || observation.NormalPauseActive
                                   || observation.ActivePauseActive,
                FinalFlags = observation.PauseStateFlags,
                FinalDialogMode = startState.DialogMode,
                Message = "Pause normalization was not attempted because preflight did not pass."
            }
            : null,
        OperationalReadiness = new FlightLoadOperationalReadiness { FinalPhase = FlightLoadPhase.Preflight }
    };

    private static FlightLoadResult ImmediateResult(
        FlightLoadOutcome outcome,
        string path,
        string message,
        DateTimeOffset requestedUtc,
        Guid attemptId) => BuildImmediateResult(
        outcome, null, null, null, message, requestedUtc, attemptId, path);

    private static FlightLoadWeatherAssessment BuildWeatherAssessment(
        FltFileMetadata target,
        FlightLoadObservation? initial,
        FlightLoadObservation? final) => new()
    {
        Status = target.WeatherStatus,
        RequestedFromFile = target.UseWeatherFile,
        UseLiveWeather = target.UseLiveWeather,
        PresetFile = target.WeatherPresetFile,
        PresetAbsolutePath = target.WeatherPresetAbsolutePath,
        PresetExists = target.WeatherPresetExists,
        InitialWindDirectionDeg = initial?.WindDirectionDeg,
        InitialWindVelocityKts = initial?.WindVelocityKts,
        FinalWindDirectionDeg = final?.WindDirectionDeg,
        FinalWindVelocityKts = final?.WindVelocityKts,
        ObservedWindChanged = ObservedWindChanged(initial, final),
        ManualVerification = target.UseWeatherFile
            ? "The FLT declares a weather file; confirm clouds, precipitation, gusts and turbulence visually in MSFS."
            : "UseWeatherFile=False does not prove that MSFS left weather unchanged; confirm the runtime weather visually."
    };

    private static bool? ObservedWindChanged(
        FlightLoadObservation? initial,
        FlightLoadObservation? final)
    {
        if (initial?.WindDirectionDeg is not { } initialDirection
            || initial.WindVelocityKts is not { } initialVelocity
            || final?.WindDirectionDeg is not { } finalDirection
            || final.WindVelocityKts is not { } finalVelocity)
            return null;

        var directionDelta = Math.Abs((initialDirection - finalDirection) % 360d);
        directionDelta = directionDelta > 180d ? 360d - directionDelta : directionDelta;
        return directionDelta >= 1d || Math.Abs(initialVelocity - finalVelocity) >= 0.5d;
    }

    private static bool PathsMatch(string expected, string? actual)
    {
        if (string.IsNullOrWhiteSpace(actual)) return false;
        try
        {
            if (string.Equals(Path.GetFullPath(expected), Path.GetFullPath(actual), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch
        {
            // Fall back to filename correlation for simulator-normalized or virtual paths.
        }

        return string.Equals(Path.GetFileName(expected), Path.GetFileName(actual), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool FlowIdentityMatches(string expected, string? actual)
    {
        if (PathsMatch(expected, actual)) return true;
        if (string.IsNullOrWhiteSpace(actual)) return false;
        var expectedStem = Path.GetFileNameWithoutExtension(expected.Trim());
        var actualStem = Path.GetFileNameWithoutExtension(actual.Trim());
        return expectedStem.Length > 0
               && string.Equals(expectedStem, actualStem, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool HasDiagnosticNormalPause(uint? flags) =>
        flags is { } value && (value & NormalPauseMask) != 0;

    internal static bool HasDiagnosticActivePause(uint? flags) =>
        flags is { } value && (value & ActivePauseMask) != 0;

    internal static bool IsDiagnosticOperationalProbe(
        FlightLoadObservation observation,
        bool? simRunning,
        bool? dialogMode) =>
        observation.PauseStateAvailable
        && observation.PauseStateFlags == 0
        && !observation.NormalPauseActive
        && !observation.ActivePauseActive
        && simRunning == true
        && dialogMode == false
        && observation.SimDisabled != true
        && observation.UserInputEnabled != false
        && observation.MotionSimulationActive != false;

    internal static FlightLoadPhase AdvanceDiagnosticPhase(
        FlightLoadPhase current,
        FlightLoadPhase requested) => requested > current ? requested : current;

    internal static FlightLoadOutcome? DetermineDiagnosticTimeoutOutcome(
        DateTimeOffset now,
        DateTimeOffset requestIssuedUtc,
        DateTimeOffset? loadAcceptedUtc,
        DateTimeOffset? flightStartedUtc,
        DateTimeOffset? controlOperationalUtc,
        bool controlOperational,
        FlightLoadRequest request)
    {
        if (loadAcceptedUtc is null)
        {
            return now - requestIssuedUtc >= PositiveOrDefault(
                request.AcceptanceTimeout, TimeSpan.FromSeconds(30))
                ? FlightLoadOutcome.TimedOut
                : null;
        }

        var validationStart = flightStartedUtc ?? controlOperationalUtc;
        if (validationStart is not null
            && now - validationStart.Value >= PositiveOrDefault(
                request.ValidationTimeout, TimeSpan.FromSeconds(15)))
            return controlOperational
                ? FlightLoadOutcome.PartialSuccess
                : FlightLoadOutcome.LoadedAwaitingReady;

        var readyTimeout = request.Timeout > TimeSpan.Zero
            ? request.Timeout
            : PositiveOrDefault(request.ReadyTimeout, TimeSpan.FromSeconds(180));
        return now - loadAcceptedUtc.Value >= readyTimeout
            ? FlightLoadOutcome.LoadedAwaitingReady
            : null;
    }

    private static TimeSpan PositiveOrDefault(TimeSpan value, TimeSpan fallback) =>
        value > TimeSpan.Zero ? value : fallback;

    private sealed class DiagnosticFlightLoadOperation
    {
        private readonly Dictionary<string, string> _lastTimelineByKey = new(StringComparer.Ordinal);

        public DiagnosticFlightLoadOperation(
            Guid attemptId,
            DateTimeOffset requestedUtc,
            FltFileMetadata target,
            string sha256,
            FlightLoadStartState startState,
            FlightLoadReadinessEvaluator evaluator,
            FlightLoadRequest request,
            IProgress<string>? progress)
        {
            AttemptId = attemptId;
            RequestedUtc = requestedUtc;
            Target = target;
            Sha256 = sha256;
            StartState = startState;
            Evaluator = evaluator;
            Request = request;
            Progress = progress;
            LatestSimRunning = startState.SimRunning;
            LatestDialogMode = startState.DialogMode;
        }

        public Guid AttemptId { get; }
        public DateTimeOffset RequestedUtc { get; }
        public FltFileMetadata Target { get; }
        public string Sha256 { get; }
        public FlightLoadStartState StartState { get; }
        public FlightLoadReadinessEvaluator Evaluator { get; }
        public FlightLoadRequest Request { get; }
        public IProgress<string>? Progress { get; }
        public TaskCompletionSource<FlightLoadResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FlightLoadTimelineEntry> Timeline { get; } = [];
        public FlightLoadPhase Phase { get; set; } = FlightLoadPhase.Preflight;
        public FlightLoadPauseNormalization? PauseNormalization { get; set; }
        public bool LoadIssued { get; set; }
        public DateTimeOffset? RequestIssuedUtc { get; set; }
        public DateTimeOffset? LoadAcceptedUtc { get; set; }
        public DateTimeOffset? FlightStartedUtc { get; set; }
        public DateTimeOffset? ControlOperationalUtc { get; set; }
        public DateTimeOffset? OperationalUtc { get; set; }
        public bool SystemEventSeen { get; set; }
        public bool FlowLoadedSeen { get; set; }
        public bool FlightStartEventSeen { get; set; }
        public bool SimStartEventSeen { get; set; }
        public bool PathStateResponded { get; set; }
        public bool PathConfirmed { get; set; }
        public bool DisconnectedDuringLoad { get; set; }
        public bool ReconnectedDuringLoad { get; set; }
        public bool TargetStateValidated { get; set; }
        public bool ControlOperational { get; set; }
        public bool UsedStableProbeFallback { get; set; }
        public int MaxConsecutiveValidSamples { get; set; }
        public int ConsecutiveOperationalSamples { get; set; }
        public DateTimeOffset? StableOperationalProbeUtc { get; set; }
        public DateTimeOffset LastSystemQueryUtc { get; set; } = DateTimeOffset.MinValue;
        public string? LoadedFilename { get; set; }
        public string? ConfirmedFlightStatePath { get; set; }
        public FlightLoadObservation? TargetMatchedObservation { get; set; }
        public bool? LatestSimRunning { get; set; }
        public bool? LatestDialogMode { get; set; }
        public bool? LatestSimDisabled { get; set; }
        public bool? LatestUserInputEnabled { get; set; }
        public bool? LatestMotionSimulationActive { get; set; }

        public void Advance(FlightLoadPhase phase, string stage, string detail, string? progressMessage = null)
        {
            var next = AdvanceDiagnosticPhase(Phase, phase);
            if (next == Phase && phase != Phase) return;
            if (next > Phase)
            {
                Phase = next;
                Add(stage, detail);
            }
            if (!string.IsNullOrWhiteSpace(progressMessage))
                Progress?.Report(progressMessage);
        }

        public void Add(string stage, string message) => Timeline.Add(new FlightLoadTimelineEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Stage = stage,
            Message = message
        });

        public void AddChanged(string key, string stage, string message)
        {
            if (_lastTimelineByKey.TryGetValue(key, out var previous)
                && string.Equals(previous, message, StringComparison.Ordinal))
                return;
            _lastTimelineByKey[key] = message;
            Add(stage, message);
        }
    }

    private sealed record DiagnosticSystemSnapshot(bool? SimRunning, bool? DialogMode);
}
