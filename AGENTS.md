# Challenge Lab — agent notes

## STORE tab — flight-state snapshots (BUILD 2238+)

Save/restore full flight state (position, exact body velocities, gear/flaps/spoilers,
trim, fuel, engines, lights, autopilot modes + targets, zulu time, ambient weather →
fixed METAR). Autopilot restore = values first, then modes (`ApplyAutopilotSnapshotAsync`);
toggle-only FD/A-THR fire on read-back mismatch; addon FMGC managed modes best-effort. Restore goes
through the **same safe-apply pipeline as challenges** — `RestoreSnapshotAsync` in
`SimConnectClient.Snapshot.cs` mirrors `LoadScenarioAsync` (entry normalized via
`NormalizeLoadEntryAsync`, TITLE gate, pause+freeze hold, release ordering copied).
**Never FlightLoad snapshots** — same CTD rule as below. Snapshot files:
`%LocalAppData%\ChallengeLab\snapshots\` (`SnapshotStore`, atomic writes, rename keeps
stamp+guid). Default end state after Load = SET PAUSE + "Resume when ready" (user choice).
Ground restores use INITPOSITION `OnGround=1` (the plain `Teleport` hardcodes air).

## Start Challenge / MSFS CTD (critical)

### Do NOT mid-session FlightLoad for aircraft swap
Forcing H125→A330 via `SimConnect_FlightLoad` of CustomFlight or a patched full autosave **crashed MSFS 2024** during “Waiting for aircraft load…”.

### Safe path (BUILD 2228+)
1. Read `TITLE` — if not in `aircraftTitles`, show guidance and **abort** (no FlightLoad).
2. Apply time + weather + **teleport + body velocity inject** (freeze only during apply; always unfreeze in finally).
3. **Verify** lat/alt/on-ground/IAS; on failure **do not Arm** scoring (message the user).
4. Gear / flaps only after verify success.
5. User must already be in the challenge aircraft (World Map free flight).

### Touchdown guards
- Post-arm grace (`timing.postArmIgnoreSeconds`) seeds ground state — no instant TD on runway after Restart.
- Require airborne samples above `minAirborneAglFeet` before a ground edge counts as touchdown.
- VS curve: hard landings ≤ −2000 fpm score 0% firmness (not clamped at 70%).

### Related files
- `SimConnectClient.LoadScenarioAsync` — no FlightLoad; returns `SpawnApplyResult`
- `LandingSession` — post-arm grace + airborne gate
- `FltScenarioBuilder` — minimal FLT artifacts only; never overwrite CustomFlight on Start
- `AircraftMismatchException` — wrong aircraft UX

## Highscores landing report (critical WPF lesson)

### Symptom
- Highscore row selected; header + primary VS card showed correct data.
- Full metric list / status text stayed blank (empty yellow/teal boxes).
- Data **was** present in `%LocalAppData%\ChallengeLab\highscores.json` (`Criteria[]` full).

### Root causes (both real)

1. **`ProgressBar.Value` defaults to TwoWay binding**  
   Bound to a **read-only** property (`LandingReport.VerticalSpeedScorePercent`).  
   WPF throws:  
   `A TwoWay or OneWayToSource binding cannot work on the read-only property '…'`.  
   That exception aborted report rebuild and left UI half-painted.

2. **Fragile list layout / binding**  
   DockPanel + ListBox / nested `LandingReport.Metrics` paths were unreliable for showing many cards.  
   Empty-looking “status” bars were also **cyan text on cyan background** (invisible).

### Fix pattern (do this next time)

```xml
<!-- ALWAYS OneWay on ProgressBar when source is get-only / computed -->
<ProgressBar Value="{Binding SomeReadOnlyPercent, Mode=OneWay, TargetNullValue=0, FallbackValue=0}"/>
```

```csharp
// Prefer painting complex report panels in code-behind after selection:
// - Set TextBlock.Text directly
// - Build metric cards into a named StackPanel (MetricsHost.Children)
// Avoid relying solely on ItemsControl + nested ViewModel paths for critical UI
```

```csharp
// When rebuilding lists for ItemsControl, assign a NEW ObservableCollection
// instead of only Clear()/Add() if the UI doesn't refresh:
ReportMetrics = new ObservableCollection<T>(items);
```

```xml
<!-- Never use same color for text and background (e.g. cyan on cyan) -->
```

### Related files
- `src/ChallengeLab.App/Views/MainWindow.xaml` — highscores report panel  
- `src/ChallengeLab.App/Views/MainWindow.xaml.cs` — `PaintReportPanel()`, selection handler  
- `src/ChallengeLab.App/ViewModels/MainViewModel.cs` — `RebuildLandingReport()`  
- `src/ChallengeLab.App/ViewModels/LandingReportViewModel.cs`  
- `src/ChallengeLab.Core/Highscores/HighscoreStore.cs` — `[JsonIgnore]` on computed props  

### Do not re-serialize computed properties
`HasDetail`, `CriteriaForReport`, `VerticalSpeedDisplay` must stay `[JsonIgnore]` so JSON load/save doesn’t confuse the store.

### Verify after UI changes
1. Rebuild + restart (window title can carry a build tag).  
2. Click a highscore that has `Criteria` in JSON.  
3. Confirm full metric cards + explanations scroll into view.  
4. No binding exceptions in the report status area.
