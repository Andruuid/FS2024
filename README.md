# Challenge Lab — MSFS 2024 Prototype

A separate challenge mode for **Microsoft Flight Simulator 2024**: hardcore landings, disaster scenarios (roadmap), and a **JSON-configurable** scoring engine that can reward *safe firm* touchdowns over butter greasers when the situation demands it.

## What you get (v1 prototype)

| Feature | Status |
|---------|--------|
| Modern WPF challenge browser | Yes |
| **Barcelona Crosswind Final** (A330, LEBL 25L) | Yes |
| Single full scoring profile (JSON evaluation key) | Yes |
| Load → final approach (FlightLoad + teleport fallback) | Yes |
| Score when GS &lt; 50 knots | Yes |
| Companion HUD (tips, live stats, results) | Yes |
| Highscores tab | Yes |
| Disasters (Sioux City, Swissair) | UI placeholders |
| Admin UI for criteria | Later (JSON only for now) |

## Requirements

- Windows 10/11
- **Microsoft Flight Simulator 2024**
- **MSFS 2024 SDK** (SimConnect) at `C:\MSFS 2024 SDK` (or adjust project reference paths)
- [.NET 9 SDK](https://dotnet.microsoft.com/download) to build

## Quick start (dev)

```powershell
cd c:\ClaudeCode\FS2024
dotnet build
dotnet test
dotnet run --project src\ChallengeLab.App
```

1. Start **MSFS 2024** (main menu or free flight is fine).
2. Launch **Challenge Lab**.
3. Wait until the status shows **Connected** (or click **Connect**).
4. Select **Barcelona Crosswind Final**.
5. Click **Start Challenge** — watch the progress bar, then fly the landing.
6. After touchdown, slow below **50 knots**. Score appears on the **Companion HUD** and under the **Session** tab.

## Publish (easy install folder)

```powershell
.\scripts\publish.ps1
```

Run `dist\ChallengeLab\ChallengeLab.exe`. No Community package install is required for v1.

## Project layout

```
src/ChallengeLab.App          WPF UI + Companion HUD
src/ChallengeLab.Core         Scoring, config, highscores, FLT generator
src/ChallengeLab.SimConnect   MSFS SimConnect bridge
config/                       Challenge + scoring JSON  ← edit these
flights/                      Optional hand .FLT overrides only
tests/                        Unit tests (no sim required)
```

## Challenge scenario (single JSON source of truth)

Each challenge is **one file**: `config/challenges/<id>.json`.

Edit spawn (lat/lon/alt/heading/**airspeedKts**), aircraft titles, weather, **timeOfDay**, gear/flaps, runway, tips there.

### Safe Start Challenge (MSFS 2024)

**Do not** force an aircraft change mid free-flight via `FlightLoad` — that path can **crash the sim**. Challenge Lab uses a **safe apply** only:

1. Check live aircraft `TITLE` matches `aircraftTitles`
2. Set **time of day** (SimConnect clock)
3. Apply weather (METAR / custom)
4. **Teleport** spawn + IAS (InitPosition) + body velocity inject (while briefly frozen)
5. **Verify** position/altitude/airborne; on failure scoring is **not** armed
6. Gear / flaps → arm scoring

**You must start free flight already in the challenge aircraft** (e.g. A330-200 (RR)):

1. MSFS World Map → select **A330-200 (RR)** → Start free flight  
2. Challenge Lab → Connect → Start Challenge  
3. **Restart** uses the same path (re-teleport to spawn). If verify fails after a crash, try Restart again or slew briefly.

A minimal `.FLT` artifact is still written under `%LocalAppData%\ChallengeLab\generated\` for debugging — it is **not** FlightLoaded mid-session.

- Leave `"flightFile": ""` (default)  
- **`timeOfDay`** — fixed clock every start (default **12:00 local**)

```json
"timeOfDay": {
  "hour": 12,
  "minute": 0,
  "useZuluTime": false
}
```

Session log should show:

```
Safe start: no mid-session FlightLoad (teleport + velocity + verify).
Aircraft OK: 'A330-200 (RR)'
Spawn verified: horiz=… m · altErr=… ft · ias=… kt
```

## Configuring scoring (finetune without code)

**Primary file — edit this, then restart the app:**

`config/scoring/profiles/landing-evaluation-key.json`

Loaded at startup (path from `catalog.json` → `evaluationKey`). Phase weights, metric importance, VS piecewise curves, gear gate, settle GS, and timing windows all live here. Session log confirms load:

```
Evaluation key loaded: landing-evaluation-key v1 · N metrics · Approach 25% + Touchdown 70% + Rollout 5%
  path: ...\config\scoring\profiles\landing-evaluation-key.json
```

### Formula

```
final % = (touchdown × 0.70) + (approach × 0.25) + (rollout × 0.05)
then gear-up gate (if required): final × multiplierOnFail (default 0.1)
```

Within each phase, metrics are weighted by `importancePercent` (renormalized if needed). There is no Easy/Strict split — every metric in the key is always scored.

### Also editable

| File | Purpose |
|------|---------|
| `config/catalog.json` | Points at the evaluation key path |
| `config/challenges/*.json` | **Full scenario** (spawn, IAS, aircraft, weather, gear/flaps, runway) — no parallel .FLT |
| `config/scoring/profiles/landing-evaluation-key.json` | Authoritative scoring, timing, gear, and speed-target settings |

### Metric fields in the evaluation key

- `metric` — e.g. `touchdownVerticalSpeedFpm`, `centerlineDeviationM`
- `evaluator` — `piecewise` | `target` | `band` | `range` | `boolean` | `centerline`
- `importancePercent` — share of that phase (not free points for gear)
- `points` — piecewise curve: `v` = measured value, `s` = **metric score 0–100%** (e.g. `{ "v": -100, "s": 100 }`). Each metric always reports 0–100%; phase weights (`importancePercent`) only blend them.

**Gear** is a safety gate under `gates.gear`, not a phase metric: gear down = no credit; gear up = overall score cut.

## Modes (vision)

1. **Hardcore Landings** — weather, weight, airports; first challenge is Barcelona crosswind.
2. **Disasters** — systems failures (hydraulics, smoke); cards are visible but **Coming soon**.

## Notes / troubleshooting

| Issue | Fix |
|-------|-----|
| Never connects | Start MSFS first; run Challenge Lab as same user; confirm SDK SimConnect.dll next to the exe when published |
| Wrong aircraft dialog | Load the listed plane from World Map free flight, then Start Challenge. Challenge Lab will **not** hot-swap aircraft (prevents CTD). |
| Still in helo after Start | Expected if you were in a helo — dialog should appear; switch to A330 first. |
| Sim crashed after “Waiting for aircraft load” (old builds) | That was mid-session FlightLoad. Use **BUILD 2225+** (safe path, no FlightLoad). Restore CustomFlight if needed: `%AppData%\Microsoft Flight Simulator 2024\MISSIONS\Custom\CustomFlight\` — delete `CustomFlight.FLT` or restore `*.challengelab.bak`. |
| Night / wrong time | `timeOfDay` defaults to noon local. Turn off live time in the sim UI if it overrides. |
| Wrong start speed | Edit `spawn.airspeedKts` in challenge JSON only. |
| Weather not strong | METAR via SimConnect; set a custom wind preset manually if the sim ignores it. |
| Highscores report empty / no metric cards | See **AGENTS.md** — usually `ProgressBar` TwoWay on a read-only property, or list binding/layout. Prefer `Mode=OneWay` on progress bars and code-behind paint for the report panel. |

Highscores are stored in:

`%LocalAppData%\ChallengeLab\highscores.json`

## License / scope

Prototype for personal/experimental use. Not affiliated with Microsoft or Asobo.
