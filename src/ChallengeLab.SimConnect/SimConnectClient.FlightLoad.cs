using System.Security.Cryptography;
using ChallengeLab.Core.FlightLoading;
using ChallengeLab.Core.Models;
using Microsoft.FlightSimulator.SimConnect;

namespace ChallengeLab.SimConnect;

/// <summary>
/// Experimental ACTIONS-tab FLT loader. This file is deliberately isolated from the safe
/// challenge/snapshot apply pipelines. It never retries FlightLoad and never permits an
/// active cross-aircraft load.
/// </summary>
public sealed partial class SimConnectClient
{
    private DiagnosticFlightLoadOperation? _diagnosticFlightLoad;
    private TaskCompletionSource<SimulatorModeResponse?>? _diagnosticSimStateTcs;
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

        if (_diagnosticFlightLoad is not null)
            return ImmediateResult(FlightLoadOutcome.Blocked, request.FlightFilePath,
                "Another diagnostic FLT load is already running.", requestedUtc, attemptId);
        if (!IsConnected || _sim is null)
            return ImmediateResult(FlightLoadOutcome.Failed, request.FlightFilePath,
                "Not connected to the simulator.", requestedUtc, attemptId);

        try
        {
            progress?.Report("Reading FLT metadata…");
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

            progress?.Report("Checking simulator state…");
            var mode = await RequestDiagnosticSimulatorModeAsync(ct);
            var currentTitle = mode == FlightLoadSimulatorMode.ActiveFlight
                ? await RequestAircraftTitleAsync(ct)
                : null;
            var initialObservation = mode == FlightLoadSimulatorMode.ActiveFlight
                ? _latestDiagnosticObservation
                : null;
            startState = new FlightLoadStartState
            {
                SimulatorMode = mode,
                AircraftTitle = currentTitle,
                Observation = initialObservation
            };

            var safety = FlightLoadSafetyPolicy.Evaluate(target, mode, currentTitle);
            if (!safety.Allowed)
            {
                Log($"Diagnostic FlightLoad BLOCKED: {safety.Reason}");
                return BuildImmediateResult(
                    FlightLoadOutcome.Blocked, target, sha256, startState,
                    safety.Reason, requestedUtc, attemptId);
            }

            var timeout = request.Timeout <= TimeSpan.Zero
                ? TimeSpan.FromSeconds(180)
                : request.Timeout > TimeSpan.FromSeconds(180)
                    ? TimeSpan.FromSeconds(180)
                    : request.Timeout;
            var operation = new DiagnosticFlightLoadOperation(
                attemptId,
                requestedUtc,
                target,
                sha256,
                startState,
                new FlightLoadReadinessEvaluator(target, request.RequiredConsecutiveSamples),
                progress);
            operation.Add("Preflight", safety.Reason);
            if (target.WeatherStatus == FlightLoadWeatherStatus.NotRequested)
                operation.Add("Weather", $"FLT names '{target.WeatherPresetFile ?? "no preset"}' but UseWeatherFile=False.");
            else if (target.WeatherStatus == FlightLoadWeatherStatus.DependencyMissing)
                operation.Add("Weather", $"Referenced preset is missing: {target.WeatherPresetAbsolutePath}");

            _diagnosticFlightLoad = operation;
            progress?.Report("Sending one experimental FlightLoad request…");
            operation.Add("Request", $"FlightLoad('{target.FilePath}')");
            operation.LoadIssued = true;

            // The managed API returns void. Success is established only from later events,
            // system state and validated telemetry.
            _sim.FlightLoad(target.FilePath);
            Log($"Diagnostic FlightLoad sent once: {target.FilePath}");

            var deadline = requestedUtc + timeout;
            while (!operation.Completion.Task.IsCompleted)
            {
                ct.ThrowIfCancellationRequested();
                ReceiveMessage();

                if (IsConnected
                    && DateTimeOffset.UtcNow - operation.LastPathQueryUtc >= TimeSpan.FromSeconds(1))
                    RequestDiagnosticFlightPathState();

                if (operation.Evaluator.IsReady)
                {
                    if (operation.PathConfirmed)
                        CompleteReadyOperation(operation, fullyCorrelated: operation.SystemEventSeen);
                    else if (operation.LoadedSignalUtc is { } loadedUtc
                             && DateTimeOffset.UtcNow - loadedUtc >= TimeSpan.FromSeconds(5))
                        CompleteReadyOperation(operation, fullyCorrelated: false);
                }

                if (DateTimeOffset.UtcNow >= deadline)
                {
                    CompleteTimedOutOperation(operation);
                    break;
                }

                await Task.Delay(100, ct);
            }

            return await operation.Completion.Task;
        }
        catch (OperationCanceledException)
        {
            if (_diagnosticFlightLoad is { } operation)
            {
                CompleteOperation(operation, FlightLoadOutcome.Failed, "Diagnostic FLT load was cancelled.");
                return await operation.Completion.Task;
            }

            return BuildImmediateResult(
                FlightLoadOutcome.Failed, target, sha256, startState,
                "Diagnostic FLT load was cancelled.", requestedUtc, attemptId, request.FlightFilePath);
        }
        catch (Exception ex)
        {
            Log($"Diagnostic FlightLoad error: {ex}");
            if (_diagnosticFlightLoad is { } operation)
            {
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

    private async Task<FlightLoadSimulatorMode> RequestDiagnosticSimulatorModeAsync(CancellationToken ct)
    {
        if (_sim is null) return FlightLoadSimulatorMode.Unknown;
        var tcs = new TaskCompletionSource<SimulatorModeResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diagnosticSimStateTcs = tcs;
        _sim.RequestSystemState(Requests.DiagnosticSimState, "Sim");

        try
        {
            for (var i = 0; i < 60 && !tcs.Task.IsCompleted; i++)
            {
                ct.ThrowIfCancellationRequested();
                ReceiveMessage();
                await Task.Delay(50, ct);
            }

            var response = tcs.Task.IsCompleted ? await tcs.Task : null;
            return response is null
                ? FlightLoadSimulatorMode.Unknown
                : response.InActiveSimulation
                    ? FlightLoadSimulatorMode.ActiveFlight
                    : FlightLoadSimulatorMode.MainMenu;
        }
        finally
        {
            if (ReferenceEquals(_diagnosticSimStateTcs, tcs))
                _diagnosticSimStateTcs = null;
        }
    }

    private void EnsureDiagnosticFlightLoadSubscriptions()
    {
        if (_sim is null || _diagnosticFlightLoadSubscriptionsReady) return;
        try
        {
            _sim.SubscribeToSystemEvent(Events.DiagnosticFlightLoaded, "FlightLoaded");
        }
        catch (Exception ex)
        {
            // The system-state polling fallback below can still correlate the load.
            Log($"Diagnostic FlightLoaded subscription unavailable: {ex.Message}");
        }
        try
        {
            _sim.SubscribeToFlowEvent();
        }
        catch (Exception ex)
        {
            // Flow events are supplementary; FlightLoaded + system state remain authoritative.
            Log($"Diagnostic flow-event subscription unavailable: {ex.Message}");
        }

        _diagnosticFlightLoadSubscriptionsReady = true;
    }

    private void OnRecvDiagnosticEventFilename(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_EVENT_FILENAME data)
    {
        if ((Events)data.uEventID != Events.DiagnosticFlightLoaded || _diagnosticFlightLoad is not { } operation)
            return;

        var path = data.szFileName?.Trim();
        if (!string.IsNullOrWhiteSpace(path) && !PathsMatch(operation.Target.FilePath, path))
        {
            operation.Add("Correlation", $"Ignored FlightLoaded event for different file: {path}");
            return;
        }
        operation.SystemEventSeen = true;
        operation.LoadedFilename = path;
        operation.Add("FlightLoaded", string.IsNullOrWhiteSpace(path) ? "Event received without a filename." : path);
        MarkDiagnosticLoadedSignal(operation, path, "FlightLoaded event");
        RequestDiagnosticFlightPathState();
    }

    private void OnRecvDiagnosticFlowEvent(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_FLOW_EVENT data)
    {
        if (_diagnosticFlightLoad is not { } operation) return;
        if (data.FlowEvent == SIMCONNECT_FLOW_EVENT.FLT_LOAD)
        {
            operation.Add("Flow", $"FLT_LOAD {data.FltPath}".Trim());
            operation.Progress?.Report("Simulator is loading the FLT…");
            return;
        }

        if (data.FlowEvent != SIMCONNECT_FLOW_EVENT.FLT_LOADED) return;
        if (!string.IsNullOrWhiteSpace(data.FltPath)
            && !PathsMatch(operation.Target.FilePath, data.FltPath))
        {
            operation.Add("Correlation", $"Ignored FLT_LOADED flow event for different file: {data.FltPath}");
            return;
        }
        operation.FlowLoadedSeen = true;
        if (string.IsNullOrWhiteSpace(operation.LoadedFilename))
            operation.LoadedFilename = data.FltPath?.Trim();
        operation.Add("Flow", $"FLT_LOADED {data.FltPath}".Trim());
        MarkDiagnosticLoadedSignal(operation, data.FltPath, "FLT_LOADED flow event");
        RequestDiagnosticFlightPathState();
    }

    private void OnRecvDiagnosticSystemState(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_SYSTEM_STATE data)
    {
        if (data.dwRequestID == (uint)Requests.DiagnosticSimState)
        {
            _diagnosticSimStateTcs?.TrySetResult(new SimulatorModeResponse(data.dwInteger != 0));
            return;
        }

        if (data.dwRequestID != (uint)Requests.DiagnosticFlightLoadedState
            || _diagnosticFlightLoad is not { } operation)
            return;

        var path = data.szString?.Trim();
        operation.ConfirmedFlightStatePath = path;
        operation.PathStateResponded = true;
        operation.PathConfirmed = PathsMatch(operation.Target.FilePath, path);
        operation.Add("SystemState",
            operation.PathConfirmed
                ? $"FlightLoaded confirmed: {path}"
                : $"FlightLoaded path mismatch: {path ?? "(empty)"}");
        if (operation.PathConfirmed)
            MarkDiagnosticLoadedSignal(operation, path, "FlightLoaded system state");
    }

    private void RequestDiagnosticFlightPathState()
    {
        if (_sim is null || _diagnosticFlightLoad is null) return;
        try
        {
            _diagnosticFlightLoad.LastPathQueryUtc = DateTimeOffset.UtcNow;
            _sim.RequestSystemState(Requests.DiagnosticFlightLoadedState, "FlightLoaded");
        }
        catch (Exception ex)
        {
            _diagnosticFlightLoad.Add("SystemState", $"FlightLoaded query failed: {ex.Message}");
        }
    }

    private void MarkDiagnosticLoadedSignal(
        DiagnosticFlightLoadOperation operation,
        string? path,
        string source)
    {
        if (!string.IsNullOrWhiteSpace(path) && !PathsMatch(operation.Target.FilePath, path))
        {
            operation.Add("Correlation", $"Ignored {source} for different file: {path}");
            return;
        }

        if (operation.LoadedSignalUtc is not null) return;
        var now = DateTimeOffset.UtcNow;
        operation.LoadedSignalUtc = now;
        operation.Evaluator.MarkFlightLoaded(now);
        operation.Progress?.Report("FLT loaded; validating aircraft state…");
    }

    private void ObserveDiagnosticFlightLoadTelemetry(TelemetrySample sample)
    {
        var observation = FlightLoadObservation.FromTelemetry(sample);
        _latestDiagnosticObservation = observation;
        if (_diagnosticFlightLoad is not { LoadedSignalUtc: not null } operation) return;

        var wasReady = operation.Evaluator.IsReady;
        operation.Evaluator.Observe(observation);
        if (!wasReady && operation.Evaluator.IsReady)
        {
            operation.Add("Readiness",
                $"{operation.Evaluator.ConsecutiveValidSamples} consecutive target-matching samples.");
            operation.Progress?.Report("Target state validated; confirming loaded path…");
        }
    }

    private void OnDiagnosticFlightLoadConnectionOpened()
    {
        if (_diagnosticFlightLoad is not { LoadIssued: true } operation) return;
        if (operation.DisconnectedDuringLoad)
        {
            operation.ReconnectedDuringLoad = true;
            operation.Add("Connection", "SimConnect reconnected during the load attempt.");
            operation.Progress?.Report("Reconnected; confirming the loaded FLT…");
        }
        RequestDiagnosticFlightPathState();
    }

    private void OnDiagnosticFlightLoadConnectionClosing()
    {
        _diagnosticSimStateTcs?.TrySetResult(null);
        if (_diagnosticFlightLoad is not { LoadIssued: true } operation
            || operation.Completion.Task.IsCompleted
            || operation.DisconnectedDuringLoad)
            return;

        operation.DisconnectedDuringLoad = true;
        operation.Add("Connection", "SimConnect disconnected during the load attempt; waiting for auto-reconnect.");
        operation.Progress?.Report("Connection lost during load; waiting for auto-reconnect…");
    }

    private void ObserveDiagnosticFlightLoadException(SIMCONNECT_RECV_EXCEPTION data)
    {
        if (_diagnosticFlightLoad is not { } operation) return;
        operation.Add("SimConnectException",
            $"Code {data.dwException}, send {data.dwSendID}, index {data.dwIndex}");
        if (!operation.SystemEventSeen && !operation.FlowLoadedSeen && data.dwException is 20 or 23)
            CompleteOperation(operation, FlightLoadOutcome.Failed,
                $"SimConnect rejected the FLT load (exception {data.dwException}).");
    }

    private void CompleteReadyOperation(DiagnosticFlightLoadOperation operation, bool fullyCorrelated)
    {
        var weatherReady = operation.Target.WeatherStatus == FlightLoadWeatherStatus.DependencyAvailable;
        var outcome = fullyCorrelated && weatherReady
            ? FlightLoadOutcome.Succeeded
            : FlightLoadOutcome.PartialSuccess;
        var message = outcome == FlightLoadOutcome.Succeeded
            ? "FLT event, loaded path and target telemetry were verified. Confirm weather visually in MSFS."
            : !weatherReady
                ? "Flight state loaded and validated, but the custom weather dependency is inactive or missing."
                : "Flight state loaded and validated, but event/path correlation was incomplete.";
        CompleteOperation(operation, outcome, message);
    }

    private void CompleteTimedOutOperation(DiagnosticFlightLoadOperation operation)
    {
        if (operation.Evaluator.IsReady || operation.SystemEventSeen || operation.PathConfirmed || operation.FlowLoadedSeen)
        {
            CompleteOperation(operation, FlightLoadOutcome.PartialSuccess,
                "The simulator changed flight state, but full event/path/telemetry validation did not complete before timeout.");
            return;
        }

        CompleteOperation(operation, FlightLoadOutcome.TimedOut,
            "No correlated FlightLoaded state and valid target telemetry were received before timeout.");
    }

    private void CompleteOperation(
        DiagnosticFlightLoadOperation operation,
        FlightLoadOutcome outcome,
        string message)
    {
        if (operation.Completion.Task.IsCompleted) return;
        operation.Add("Result", $"{outcome}: {message}");
        var final = operation.Evaluator.LastObservation ?? _latestDiagnosticObservation;
        var issues = new List<string>(operation.Evaluator.LastValidation?.Issues ?? Array.Empty<string>());
        if (operation.Target.WeatherStatus == FlightLoadWeatherStatus.NotRequested)
            issues.Add("UseWeatherFile=False; the named custom weather preset was not requested by the FLT.");
        else if (operation.Target.WeatherStatus == FlightLoadWeatherStatus.DependencyMissing)
            issues.Add($"Weather preset is missing: {operation.Target.WeatherPresetAbsolutePath}");
        if (!operation.PathConfirmed)
            issues.Add("The FlightLoaded system-state path was not confirmed.");

        operation.Completion.TrySetResult(new FlightLoadResult
        {
            AttemptId = operation.AttemptId,
            RequestedUtc = operation.RequestedUtc,
            CompletedUtc = DateTimeOffset.UtcNow,
            Outcome = outcome,
            FlightFilePath = operation.Target.FilePath,
            FlightFileSha256 = operation.Sha256,
            Target = operation.Target,
            StartState = operation.StartState,
            FinalObservation = final,
            FlightLoadedEventReceived = operation.SystemEventSeen,
            LoadedFilename = operation.LoadedFilename,
            ConfirmedFlightStatePath = operation.ConfirmedFlightStatePath,
            DisconnectedDuringLoad = operation.DisconnectedDuringLoad,
            ReconnectedDuringLoad = operation.ReconnectedDuringLoad,
            ConsecutiveValidSamples = operation.Evaluator.ConsecutiveValidSamples,
            ElapsedSeconds = (DateTimeOffset.UtcNow - operation.RequestedUtc).TotalSeconds,
            Message = message,
            ValidationIssues = issues.Distinct(StringComparer.Ordinal).ToArray(),
            Timeline = operation.Timeline.ToArray(),
            Weather = BuildWeatherAssessment(operation.Target, operation.StartState.Observation, final)
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
        FlightFilePath = target?.FilePath ?? fallbackPath ?? "",
        FlightFileSha256 = sha256,
        Target = target,
        StartState = startState,
        Message = message,
        ElapsedSeconds = (DateTimeOffset.UtcNow - requestedUtc).TotalSeconds,
        ValidationIssues = [message],
        Timeline = [new FlightLoadTimelineEntry { Stage = outcome.ToString(), Message = message }],
        Weather = target is null ? null : BuildWeatherAssessment(target, startState?.Observation, null)
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
        PresetFile = target.WeatherPresetFile,
        PresetAbsolutePath = target.WeatherPresetAbsolutePath,
        PresetExists = target.WeatherPresetExists,
        InitialWindDirectionDeg = initial?.WindDirectionDeg,
        InitialWindVelocityKts = initial?.WindVelocityKts,
        FinalWindDirectionDeg = final?.WindDirectionDeg,
        FinalWindVelocityKts = final?.WindVelocityKts
    };

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

    private sealed class DiagnosticFlightLoadOperation
    {
        public DiagnosticFlightLoadOperation(
            Guid attemptId,
            DateTimeOffset requestedUtc,
            FltFileMetadata target,
            string sha256,
            FlightLoadStartState startState,
            FlightLoadReadinessEvaluator evaluator,
            IProgress<string>? progress)
        {
            AttemptId = attemptId;
            RequestedUtc = requestedUtc;
            Target = target;
            Sha256 = sha256;
            StartState = startState;
            Evaluator = evaluator;
            Progress = progress;
        }

        public Guid AttemptId { get; }
        public DateTimeOffset RequestedUtc { get; }
        public FltFileMetadata Target { get; }
        public string Sha256 { get; }
        public FlightLoadStartState StartState { get; }
        public FlightLoadReadinessEvaluator Evaluator { get; }
        public IProgress<string>? Progress { get; }
        public TaskCompletionSource<FlightLoadResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<FlightLoadTimelineEntry> Timeline { get; } = [];
        public bool LoadIssued { get; set; }
        public bool SystemEventSeen { get; set; }
        public bool FlowLoadedSeen { get; set; }
        public bool PathStateResponded { get; set; }
        public bool PathConfirmed { get; set; }
        public bool DisconnectedDuringLoad { get; set; }
        public bool ReconnectedDuringLoad { get; set; }
        public DateTimeOffset? LoadedSignalUtc { get; set; }
        public DateTimeOffset LastPathQueryUtc { get; set; } = DateTimeOffset.MinValue;
        public string? LoadedFilename { get; set; }
        public string? ConfirmedFlightStatePath { get; set; }

        public void Add(string stage, string message) => Timeline.Add(new FlightLoadTimelineEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Stage = stage,
            Message = message
        });
    }

    private sealed record SimulatorModeResponse(bool InActiveSimulation);
}
