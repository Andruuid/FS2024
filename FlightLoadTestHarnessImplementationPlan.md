# Instrumented Load FLT Test Harness Implementation Plan

Source Request:
Implement an experimental Actions-tab button that loads `data/FltFiles/andi1.flt` through SimConnect `FlightLoad`, validates the result, and persists a diagnostic JSON report without changing the safe challenge or snapshot pipelines.

Global Constraints:
- Keep each phase under about 150k tokens of expected agent work.
- Preserve existing user changes and keep `LoadScenarioAsync` / `RestoreSnapshotAsync` free of `FlightLoad`.
- Never auto-retry `FlightLoad`, and never permit a mid-session aircraft mismatch.
- Verify each phase before marking it implemented.

Phase Status Summary:
- P01: planned - FLT metadata, validation, and report domain
- P02: planned - Isolated SimConnect FlightLoad lifecycle
- P03: planned - Actions UI, assets, and full verification

## Phase P01: FLT Metadata, Validation, and Report Domain
Status: implemented
Token Budget: <=80k

Goal:
Provide testable Core models and services for parsing FLT expectations, deciding whether a load is safe, evaluating readiness, and atomically persisting attempt reports.

Scope:
- Parse `[Sim.0]`, `[SimVars.0]`, and `[Weather]`, including legacy/non-UTF8 degree markers.
- Model load request/result/outcome, initial/final observations, validation issues, event timeline, and weather status.
- Implement the main-menu/same-aircraft safety policy and three-consecutive-sample readiness evaluator.
- Implement atomic per-attempt JSON storage under a caller-provided directory.
- Add the new `ISimBridge.LoadFlightFileAsync` contract with a default unsupported implementation.

Non-Goals:
- Calling SimConnect `FlightLoad`.
- WPF commands or layout.
- Supplying or fabricating `andi1.wpr`.

Inputs:
- `data/FltFiles/andi1.flt`
- `src/ChallengeLab.Core/Models/TelemetrySample.cs`
- `src/ChallengeLab.SimConnect/ISimBridge.cs`
- Existing atomic storage conventions in `SnapshotStore`.

Implementation Steps:
- Add the flight-loading domain namespace and parser.
- Add safety and readiness policies with explicit tolerances and outcome construction.
- Add report serialization/storage.
- Extend the bridge interface without breaking existing fake implementations.
- Add focused Core tests using both the real FLT and synthetic encodings/states.

Verification:
- `dotnet test tests/ChallengeLab.Core.Tests/ChallengeLab.Core.Tests.csproj --no-restore --nologo`
- `dotnet build src/ChallengeLab.SimConnect/ChallengeLab.SimConnect.csproj --no-restore --nologo`

Completion Criteria:
- The real FLT parses as A320neo V2 near LSZH at about 4,464 ft and 236 kt, airborne.
- Safety/readiness/report persistence tests pass.
- Existing bridges still compile.

Execution Log:
- 2026-07-21: implemented - Added FLT parser, load/report models, safety and readiness policies, atomic report store, bridge contract, and 11 tests. Core 455/455 passed; SimConnect project built cleanly.
- Not started.

## Phase P02: Isolated SimConnect FlightLoad Lifecycle
Status: implemented
Token Budget: <=100k

Goal:
Implement one-shot, event-correlated `FlightLoad` execution with reconnect tolerance, system-state confirmation, and post-load telemetry validation.

Scope:
- Subscribe to `FlightLoaded`, flow events when available, and system-state responses.
- Detect main-menu versus active-flight state and enforce the P01 safety policy.
- Call `FlightLoad` exactly once, track disconnect/reconnect, and wait up to 180 seconds.
- Correlate the loaded path and require three valid post-load telemetry samples.
- Return honest success/partial/blocked/timeout/failure results and diagnostic timeline entries.
- Reset pending operation state safely during cleanup without changing challenge/snapshot loading.

Non-Goals:
- WPF UI or report storage.
- Retrying failed loads.
- Weather cloud/turbulence verification.

Inputs:
- P01 flight-loading models and policies.
- Existing SimConnect window message pump, reconnect timer, telemetry stream, title cache, and cleanup logic.

Implementation Steps:
- Add isolated request IDs/event IDs and pending-load state.
- Register/unregister system and flow event handlers with every connection.
- Implement preflight state/title queries and `LoadFlightFileAsync`.
- Feed event, state, connection, and telemetry observations into the pending attempt.
- Ensure normal cleanup completes or preserves the attempt appropriately for reconnection.

Verification:
- `dotnet build src/ChallengeLab.SimConnect/ChallengeLab.SimConnect.csproj --no-restore --nologo`
- `rg -n "FlightLoad" src/ChallengeLab.SimConnect` confirms the call exists only in the diagnostic loader and comments/tests, never in challenge/snapshot execution.
- Run the Core suite again for readiness/safety regressions.

Completion Criteria:
- The bridge returns structured outcomes for all terminal paths.
- One user request can issue at most one `FlightLoad` call.
- Reconnection and telemetry observations can finish a pending attempt.
- Safe scenario and snapshot methods remain unchanged.

Execution Log:
- 2026-07-21: implemented - Added isolated one-shot FlightLoad coordinator with safe preflight, FlightLoaded/flow/system-state correlation, polling fallback, reconnect tracking, 180s cap, telemetry readiness, and structured terminal outcomes. SimConnect builds cleanly; Core 455/455 pass; sole FlightLoad API call is in the diagnostic partial.
- Not started.

## Phase P03: Actions UI, Assets, and Full Verification
Status: implemented
Token Budget: <=80k

Goal:
Expose the fixed experimental load action, persist every result, and provide a repeatable operator workflow with regression coverage.

Scope:
- Copy `data/FltFiles/**/*` into app output.
- Resolve the bundled `andi1.flt` and report the missing/inactive `andi1.wpr` honestly.
- Add Actions-tab card, CTD warning, target summary, `Load FLT`, and `Open reports folder`.
- Stop/clear active scoring observation before a confirmed load and restart observation afterward.
- Persist every terminal attempt atomically and show its report path/outcome.
- Extend fake bridge and XAML/command tests.
- Document the test matrix and report location in the README.

Non-Goals:
- Creating a weather preset.
- Making FlightLoad part of challenge start, restart, or Store restore.
- Automatically running the live simulator test matrix.

Inputs:
- P01/P02 APIs.
- Existing Actions tab and `MainViewModel` action exclusivity.
- `FlightAndWeatherLoading.md` and `AGENTS.md` safety notes.

Implementation Steps:
- Add asset copy and deterministic runtime path resolution.
- Add command/state/report-folder properties and confirmation/status flow.
- Add the XAML card and warning text.
- Extend tests for command exclusivity, success/failure/report handling, bindings, and safety regressions.
- Close the running app, run the full suite, rebuild, and verify the bundled asset layout.

Verification:
- `dotnet test ChallengeLab.slnx --no-restore --nologo`
- `dotnet build ChallengeLab.slnx --no-restore --nologo`
- Inspect output for `data/FltFiles/andi1.flt` and confirm no fabricated WPR exists.
- Confirm `git diff --check` and review the final diff.

Completion Criteria:
- The Actions tab exposes the fixed diagnostic load and reports folder.
- Every attempted bridge result is persisted and surfaced to the operator.
- Missing weather remains a visible `Unavailable/Unverified` condition.
- All automated tests pass and existing safe loaders remain FlightLoad-free.

Execution Log:
- 2026-07-21: implemented - Added bundled FLT asset copy, BUILD 2255 Actions card/confirmation/status/report-folder flow, scoring observation reset/restart, atomic report persistence, README guidance, and UI/regression tests. Full suite passes: Core 455, App 119; build clean; diff check clean; output contains only andi1.flt and no fabricated WPR.
- Not started.
