# Nose-Gear Impact Gate v16 Implementation Plan

Source Request:
Implement a graded Challenge/Career nose-gear impact gate using the aircraft-G transient at verified nose contact, contact-point compression corroboration, persistence/reporting, and full verification while leaving Free Flight unchanged.

Global Constraints:
- Preserve all existing user changes, especially the in-progress glideslope/VASI work.
- Keep the existing operational gates, touchdown guards, no-FlightLoad startup path, WPF code-behind report rendering, OneWay computed bindings, and JsonIgnore computed highscore properties.
- Apply the worst nose-impact multiplier once and round only the final score.

Phase Status Summary:
- P01: planned - Implement and verify the complete v16 nose-gear impact gate

## Phase P01: Implement and verify the complete v16 nose-gear impact gate
Status: implemented
Token Budget: <=120k

Goal:
Ship the configured graded nose-gear impact gate end-to-end across telemetry, scoring, persistence, reports, profiles, and tests.

Scope:
- Add validated noseGearImpact configuration and v16 Challenge/Career defaults.
- Capture bounded contact-point state/compression telemetry and explicit coverage without serializing every probe into traces.
- Detect/debounce nose touchdown and recontact events after accepted main touchdown, analyze robust G delta, correlate compression, and retain the worst event.
- Apply pass/moderate/severe/unranked scoring behavior and persist complete diagnostics.
- Render a report criterion/card and cover the requested automated scenarios.
- Run project and solution verification.

Non-Goals:
- Direct physical force reconstruction in newtons or per-strut G.
- Any Free Flight scoring behavior change.
- Manual simulator flights, which require the user's running MSFS session.

Inputs:
- User-provided Nose-gear impact gate v16 plan.
- AGENTS.md safety and WPF/highscore constraints.
- Existing operational gate implementation and current dirty worktree.

Implementation Steps:
- Inspect current configuration, telemetry marshalling, landing-session lifecycle, gate evaluator, persistence, reporting, and tests.
- Add models/configuration/validation and robust nose-impact analysis.
- Add SimConnect contact-point telemetry and coverage propagation.
- Integrate scoring, diagnostics, persistence, reports, and v16 profile.
- Add and run focused, project, and solution tests; fix regressions without overwriting unrelated changes.

Verification:
- Run ChallengeLab.Core.Tests, ChallengeLab.SimConnect.Tests if present, ChallengeLab.App.Tests if present, and dotnet test for the solution.
- Confirm profile validation/versioning and Free Flight exclusion.
- Inspect the final diff to ensure unrelated glideslope/VASI changes remain intact.

Completion Criteria:
- All requested gate behavior is implemented and automated tests pass.
- The phase plan is marked implemented with the verification summary.
- Any simulator-only manual verification is explicitly handed off rather than claimed.

Execution Log:
- 2026-07-16: implemented - Implemented v16 nose-gear impact telemetry, analysis, graded scoring, persistence/reporting, active-attempt SimConnect probing, and 15 focused/integration tests. Core 213 and App 14 tests pass; SimConnect and full solution build/test pass. Manual A330 flight verification remains a pilot handoff.
- Not started.
