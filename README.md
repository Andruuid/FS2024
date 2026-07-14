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

Edit spawn (lat/lon/alt/heading/**airspeedKts**), aircraft titles, weather, gear/flaps, runway, tips there.  
At start the app **generates a minimal `.FLT`** from that JSON (under `%LocalAppData%\ChallengeLab\generated\`), then FlightLoads it and teleports again so JSON numbers win.

- Leave `"flightFile": ""` (default) — always generate from JSON  
- Set `flightFile` only for rare hand-crafted overrides  
- Change `spawn.airspeedKts` → restart challenge (no `.FLT` edit)

Session log shows:

```
Generated .FLT from challenge JSON → ...
Spawn from JSON: IAS 170 kt · ...
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
| Wrong aircraft | Config lists `A330-200 (RR)` (from this machine’s CustomFlight). Change `aircraftTitles` / FLT `[Sim.0]` if your title differs |
| FlightLoad fails | App still teleports using spawn from challenge JSON (IAS included) |
| Wrong start speed | Edit only `spawn.airspeedKts` in the challenge JSON; leave `flightFile` empty so a matching .FLT is generated |
| Weather not strong | METAR is applied via SimConnect; if the sim ignores it, set a custom wind preset manually once as fallback |
| Highscores report empty / no metric cards | See **AGENTS.md** — usually `ProgressBar` TwoWay on a read-only property, or list binding/layout. Prefer `Mode=OneWay` on progress bars and code-behind paint for the report panel. |

Highscores are stored in:

`%LocalAppData%\ChallengeLab\highscores.json`

## License / scope

Prototype for personal/experimental use. Not affiliated with Microsoft or Asobo.
