# Challenge Lab — MSFS 2024 Prototype

A separate challenge mode for **Microsoft Flight Simulator 2024**: hardcore landings, disaster scenarios (roadmap), and a **JSON-configurable** scoring engine that can reward *safe firm* touchdowns over butter greasers when the situation demands it.

## What you get (v1 prototype)

| Feature | Status |
|---------|--------|
| Modern WPF challenge browser | Yes |
| **Barcelona Crosswind Final** (A330, LEBL 25L) | Yes |
| Easy / Strict difficulty | Yes |
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
4. Select **Barcelona Crosswind Final**, choose **Easy** or **Strict**.
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
src/ChallengeLab.Core         Scoring, config, highscores
src/ChallengeLab.SimConnect   MSFS SimConnect bridge
config/                       Challenge + scoring JSON
flights/                      .FLT scenario files
tests/                        Unit tests (no sim required)
```

## Configuring scoring

Edit:

- `config/challenges/barcelona-crosswind-final.json` — spawn, weather, runway, tips
- `config/scoring/profiles/hardcore-crosswind-landing.json` — weighted criteria

Each criterion has:

- `metric` — e.g. `touchdownVerticalSpeedFpm`, `centerlineDeviationM`
- `evaluator` — `band` | `range` | `target` | `boolean`
- `levels` — `easy` / `strict` (Strict evaluates all; Easy only those tagged for easy)
- `weight` — relative importance

**Band evaluator** (crosswind firmness): peak score inside `peakMin`…`peakMax` (e.g. −180…−80 fpm); butter and hard landings both score lower.

## Modes (vision)

1. **Hardcore Landings** — weather, weight, airports; first challenge is Barcelona crosswind.
2. **Disasters** — systems failures (hydraulics, smoke); cards are visible but **Coming soon**.

## Notes / troubleshooting

| Issue | Fix |
|-------|-----|
| Never connects | Start MSFS first; run Challenge Lab as same user; confirm SDK SimConnect.dll next to the exe when published |
| Wrong aircraft | Config lists `A330-200 (RR)` (from this machine’s CustomFlight). Change `aircraftTitles` / FLT `[Sim.0]` if your title differs |
| FlightLoad fails | App teleports using spawn lat/lon/alt/heading from challenge JSON |
| Weather not strong | METAR is applied via SimConnect; if the sim ignores it, set a custom wind preset manually once as fallback |
| Highscores report empty / no metric cards | See **AGENTS.md** — usually `ProgressBar` TwoWay on a read-only property, or list binding/layout. Prefer `Mode=OneWay` on progress bars and code-behind paint for the report panel. |

Highscores are stored in:

`%LocalAppData%\ChallengeLab\highscores.json`

## License / scope

Prototype for personal/experimental use. Not affiliated with Microsoft or Asobo.
