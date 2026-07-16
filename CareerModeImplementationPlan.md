# Career Mode Implementation Plan

Source Request:
Implement the optional five-stage “Classified Promotion Flights” career ladder, including configuration, local persistence, safe attempt integration, placeholder rewards, WPF UX, tests, and documentation.

Global Constraints:
- Preserve the existing no-FlightLoad challenge start path and touchdown scoring behavior.
- Preserve legacy highscore JSON compatibility and keep computed properties ignored.
- Keep all read-only WPF progress bindings OneWay.
- Verify the complete feature with `dotnet test ChallengeLab.slnx`.

Phase Status Summary:
- P01: implemented - Implement and verify Career Mode end to end

## Phase P01: Implement and Verify Career Mode End to End
Status: implemented
Token Budget: <=120k

Goal:
Deliver the configured, persisted, tested five-rank Career Mode prototype without changing normal challenge or Free Flight behavior.

Scope:
- Add and independently validate career catalog configuration and five unavailable reward challenge files.
- Add career state, result, persistence, and deterministic progression services.
- Add attempt-origin handling and career settlement integration to the existing safe scenario/scoring pipeline.
- Add optional career metadata to highscores while retaining legacy compatibility.
- Add the first-position Career tab, reward ladder, classified/revealed mission states, and career HUD status.
- Add automated tests and README documentation.

Non-Goals:
- Implement reward scenarios, XP, partial credit, rank loss, rerolls, reset controls, accounts, cloud sync, prestige, or a separate scoring engine.

Inputs:
- User-provided Career Mode plan.
- `AGENTS.md` safety and WPF guidance.
- Existing catalog/config loader, `MainViewModel`, highscore store, challenge cards, and companion HUD.

Implementation Steps:
- Model and validate career configuration independently from evaluation-key loading.
- Implement durable career progression and atomic state persistence with corrupt-state recovery.
- Add placeholder configs and catalog references.
- Integrate Career commands, presentation state, attempt origins, settlement, highscore metadata, and HUD status.
- Build the Career WPF tab and update challenge filtering/navigation.
- Add core and app regression tests, update README, and fix all failures.

Verification:
- Run targeted Career tests while implementing.
- Run `dotnet test ChallengeLab.slnx` and require all tests to pass.
- Inspect WPF bindings for OneWay progress values and ensure the project builds without binding/source errors.

Completion Criteria:
- Career begins at Cadet, keeps one accepted mission across restarts, advances only on matching ranked scores of at least 80.0%, unlocks five ordered placeholder rewards, and ends at Career Complete.
- Normal Challenges and Free Flight remain usable when Career is disabled or ignored.
- Full solution tests pass.

Execution Log:
- 2026-07-16: implemented - Implemented configuration, atomic persistence/recovery, deterministic progression, safe attempt-origin integration, highscore metadata, Career and HUD UX, five reward placeholders, tests, docs, and BUILD 2229. dotnet test ChallengeLab.slnx passed: 155 Core + 11 App.
- Initial state: not started.
