# Challenge Lab — MSFS 2024 Prototype

A separate challenge mode for **Microsoft Flight Simulator 2024**: hardcore landings, disaster scenarios (roadmap), and a **JSON-configurable** scoring engine that can reward *safe firm* touchdowns over butter greasers when the situation demands it.

## What you get (v1 prototype)

| Feature | Status |
|---------|--------|
| Modern WPF challenge browser | Yes |
| Optional five-rank classified Career Mode | Prototype |
| **Barcelona Crosswind Final** (A330, LEBL 25L) | Yes |
| Normal + aircraft-generic Free scoring profiles (JSON evaluation keys) | Yes |
| Safe load → final approach (teleport + velocity; no FlightLoad) | Yes |
| Score when GS &lt; 50 knots | Yes |
| Companion HUD with Normal / Free flight modes | Yes |
| Highscores tab | Yes |
| **ACTIONS: experimental Load FLT test harness** | Diagnostic |
| **STORE tab** — save & reload full flight-state snapshots | Yes |
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
4. On **Career**, accept the classified assignment (A330 required), or open **Challenges** and select **Barcelona Crosswind Final** directly.
5. Start the revealed assignment or default challenge — watch the progress bar, then fly the landing.
6. After touchdown, slow below **50 knots**. Score appears on the **Companion HUD** and under the **Session** tab.

For a flight that is already in progress, select **Free** on the HUD instead. Challenge Lab warms nearby MSFS airport/runway facilities in the background, shows the best runway-aligned target as soon as the gear is down (immediately for fixed-gear aircraft), and refines it until evaluation begins. The closest field shown while scanning is reference-only and does not determine the selected runway. Scoring starts five seconds before the detected runway's glideslope reaches 2,000 ft above runway elevation. Free mode does not change the aircraft, time, weather, position, or pause state. **Reacquire** releases the current attempt and immediately detects the best aligned runway again with every nearby airport eligible.

## STORE tab — save & reload flight states

**Save flight state** captures the current moment in one shot: position, attitude, the exact
body-axis speed vector, gear/flaps/spoilers/parking brake, trim, per-tank fuel, engine state,
lights, autopilot (master, FD, autothrottle arm, HDG/NAV/ALT/VS/FLC/APR modes and the selected
heading/altitude/speed/Mach/VS targets), zulu time/date and ambient weather. Snapshots are JSON files in
`%LocalAppData%\ChallengeLab\snapshots\`, named with timestamp + name + nearest airport
(offline OurAirports lookup, e.g. `LSZH Zurich (CH)`), and can be renamed or deleted in the list.

**Load** puts the aircraft back into that state through the same safe-apply pipeline the
challenges use (SET PAUSE + freeze → teleport → velocity inject → gear/flaps settle → verify —
**no FlightLoad**, which crashes MSFS 2024 mid-session). Loading behaves identically whether you
click it while flying, parked, in the ESC menu, or in active pause. The sim ends **PAUSED** with
the state applied — click **Resume now** (or unpause in the sim) when ready; enable
*Auto-resume after load* to skip the hold.

Honest limits (sim API restrictions): you must already be flying the **same aircraft** (the app
never swaps aircraft mid-session); addon avionics/FMS programming and routes are not captured;
autopilot restores through the standard sim interface — addon "managed" FMGC modes are
best-effort and NAV mode needs a flight plan loaded; live weather is rebuilt as a fixed
approximate METAR; complex addons may ignore throttle/engine-state writes.
Gear/flap surface animations may finish moving just after you resume — the handles are correct.

## ACTIONS tab — experimental Load FLT

The **Load FLT** card is a deliberately isolated test harness for the bundled
`data/FltFiles/andi1.flt`. It may be used from the main menu or while already flying the
FLT's **A320neo V2**. It blocks a different active aircraft because a mid-session aircraft
swap through `FlightLoad` has previously crashed MSFS 2024. The call is sent once and is
never retried automatically.

Challenge Lab waits for the SimConnect `FlightLoaded` event, confirms the loaded-flight
system-state path, and then requires three matching telemetry samples. Every terminal result
is written to `%LocalAppData%\ChallengeLab\flight-load-tests\`; use **Open reports** to find
the JSON file after each experiment. Run the manual matrix from `FlightAndWeatherLoading.md`
and keep the reports when describing successes, partial loads, hangs, or reconnects.

The current FLT names `andi1.wpr`, but that file is not bundled and `UseWeatherFile=False`.
Flight-state testing still runs, while the result is correctly marked partial/unverified for
weather. To test the custom profile later, place the real `andi1.wpr` beside the FLT and set
`UseWeatherFile=True`; do not fabricate a replacement preset.

## Career Mode — classified promotion flights

Career is an optional five-stage ladder. The app opens on **Career**, while the ordinary **Challenges** tab remains available at all times and does not affect career progress.

At each rank:

1. Accept a classified assignment knowing only that an **A330** is required and the pass mark is a ranked **80.0%**.
2. The app reveals one random playable landing from the Barcelona, La Paz, and Skiathos pool.
3. That assignment stays locked across app restarts. There is no abandon or reroll, but retries are unlimited.
4. Earn a ranked final score of **at least 80.0% after all configured phase and general penalties** to pass. Lower or unranked results retain the assignment.
5. Passing reveals one future challenge reward and advances Cadet → First Officer → Senior First Officer → Captain → Command Captain.

The fifth pass shows **CAREER COMPLETE**. The five revealed rewards are honest UI previews only—Madeira, Innsbruck, Kai Tak, Paro, and the Arctic ice runway are `available=false` and their simulator scenarios remain future work. Locked rewards stay out of the regular Challenges list; revealed rewards appear there as non-playable **Coming Soon** cards.

Career configuration lives under `career` in `config/catalog.json`. Invalid career configuration disables only Career; normal challenges and scoring still load. Progress is stored immediately and atomically in:

`%LocalAppData%\ChallengeLab\career.json`

If that state is corrupt or references an obsolete assignment, the bad file is preserved beside it with a `corrupt-...` or `obsolete-...` suffix and a fresh career state is created.

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

`config/scoring/profiles/landing-evaluation-key.json` (Normal)

`config/scoring/profiles/free-flight-evaluation-key.json` (Free, generic VS0-based VAPP and no flap-index gate)

Loaded at startup (path from `catalog.json` → `evaluationKey`). Phase weights, metric importance, named composite curves, phase/general penalties, settle GS, contact mapping, and simulation-time analysis windows all live here. The Normal key is v34 and the Free key is v17. Session log confirms load:

```
Evaluation key loaded: landing-evaluation-key v34 · N metrics · Approach 30% + Touchdown 60% + Rollout 10%
  path: ...\config\scoring\profiles\landing-evaluation-key.json
```

### Formula

```
subtotal = (raw touchdown × 0.60) + (raw approach × 0.30) + (raw rollout × 0.10)
final % = subtotal × every applicable general penalty; round only the final result
```

Within each phase, metrics use the same literal formula: `metric score × importancePercent / 100`. Validation requires metric and phase weights to total exactly 100. There is no Easy/Strict split — every metric in the key is always scored.

### Touchdown evaluation

The touchdown phase keeps its 60% overall weight and separates the initial impact from flare/float; later recontacts are handled by a penalty-only gate:

- `touchdown_impact` (40% of touchdown) combines the first main-gear contact's official touchdown-normal velocity with a filtered 99th-percentile peak G. The raw G peak is diagnostic only.
- `flare_efficiency` (8%) measures sustained float distance/time and positive-VS duration below flare height. It never reuses IAS inputs.
- `airspeed` (10%) scores IAS versus the touchdown target (VAPP − 5 kt by default). Fast is punished more than slow; there is no separate excess-over-VAPP metric.
- `contact_stability` is a general penalty. The initial landing is the baseline; one valid airborne/recontact cycle (second touchdown) applies ×0.9 to the combined score, while two or more cycles (third touchdown or later) apply ×0.8. One-main-first touchdown and contact chatter are not bounces.
- `stall_warning` and `overspeed_warning` are aircraft-native general penalties. Any MSFS `STALL WARNING` or `OVERSPEED WARNING` activation during the armed attempt applies ×0.9 to the combined score; both warnings stack to ×0.81. Challenge Lab's calculated fast/slow HUD guidance never activates these gates.
- Ground spoilers must deploy on both sides by main TD+2 s (×0.9 to the combined score on failure).
- Manual brake pedals must remain released while the nose gear is airborne; by nose TD+3 s either both pedals must be applied or autobrake must be active (×0.9 to the combined score on failure).
- Heading/altitude hold must be off at or below 2,000 ft RA. AP master/AP1/AP2 and active/armed autothrust must be off at or below 1,000 ft RA; flight directors may remain on (×0.9 to the combined score on failure).
- Normal pause or Active Pause after the controlled start hold and before touchdown applies the general ×0.95 factor.
- Reducing simulation rate below 0.99× before touchdown applies the general ×0.8 factor.
- Each switch from cockpit view (`CAMERA STATE` 2) to exterior or any other non-cockpit camera multiplies the combined score by ×0.97 (stacking) until main-gear touchdown.

Composite components use weighted penalty RMS:

```text
score = 100 × (1 − sqrt(sum(normalizedWeight × (1 − componentScore / 100)²)))
```

Scoring windows use pause-aware `SIMULATION TIME`. Touchdown detection uses mapped indexed `GEAR IS ON GROUND` contacts, and the first updated `PLANE TOUCHDOWN NORMAL VELOCITY` is frozen as the official touchdown VS. Missing required G/contact/official-VS coverage produces an explainable degraded calculation, but the attempt is unranked.

The conventional wheeled fallback maps nose/center `0`, left main `1`, and right main `2`. Taildraggers and incompatible layouts require an explicit challenge `contactMapping`; indices may be 0–15.

```json
"contactMapping": { "leftMainGearIndex": 3, "rightMainGearIndex": 4, "noseGearIndex": 1 }
```

### Challenge-specific scoring profiles

A challenge can deterministically override existing composite parameters and replace individual named curves. Metric IDs, parameter names, and curve names are validated before scoring is armed:

```json
"scoringOverrides": {
  "metrics": [
    {
      "id": "touchdown_impact",
      "params": { "verticalSpeedWeight": 0.5, "peakGWeight": 0.5 },
      "curves": {
        "peakG": [
          { "v": 1.0, "s": 80 },
          { "v": 1.3, "s": 100 },
          { "v": 2.1, "s": 0 }
        ]
      }
    }
  ]
}
```

Every attempt freezes and hashes its complete effective scoring key (including contact mapping). Ranked buckets include challenge ID, key ID, key version, and profile hash, so legacy, v8–v23, and differently tuned profiles cannot mix silently.

### Also editable

| File | Purpose |
|------|---------|
| `config/catalog.json` | Points at the Normal/Free evaluation keys and defines the Career ladder/pools |
| `config/challenges/*.json` | **Full scenario** (spawn, IAS, aircraft, weather, gear/flaps, runway) — no parallel .FLT |
| `config/scoring/profiles/landing-evaluation-key.json` | Authoritative scoring, timing, gear, and speed-target settings |
| `config/scoring/profiles/free-flight-evaluation-key.json` | Free scoring overlay; last-resort 70 kt VAPP when TITLE DB and VS0 both fail |
| `config/scoring/aircraft-vapp-db.json` | Live TITLE → typical VAPP table for Free Flight (edit without recompiling) |

### Metric fields in the evaluation key

- `metric` — scalar source, e.g. `centerlineDeviationM` (not used by typed composites)
- `evaluator` — `piecewise` | `target` | `band` | `range` | `boolean` | `centerline` | `landingImpact` | `flareEfficiency` | `contactStability`
- `importancePercent` — share of that phase (not free points for gear)
- `points` — piecewise curve: `v` = measured value, `s` = **metric score 0–100%** (e.g. `{ "v": -100, "s": 100 }`). Each metric always reports 0–100%; phase weights (`importancePercent`) only blend them.
- `params` — validated scalar settings and composite component weights
- `curves` — named piecewise curves used by a composite evaluator; an override replaces one complete named curve

Penalty configuration in the Normal key:

| Scope | JSON location | Penalties |
|------|---------------|-----------|
| General | `generalPenalties` | contact stability, gear, flaps, spoilers, nose impact, aircraft stall warning, aircraft overspeed warning, automation, braking, runway remaining, reverse thrust, pause usage, simulation rate, cockpit view |

**Contact stability** is under `generalPenalties.contactStability`, not a phase metric: the initial landing earns no credit; valid bounce cycles apply ×0.9 or ×0.8 to the combined score.

**Aircraft speed warnings** are under `generalPenalties.stallWarning` and `generalPenalties.overspeedWarning`, not phase metrics. Any native MSFS warning after arming applies ×0.9 to the combined score; both warnings stack to ×0.81. VAPP/IAS HUD labels are guidance only.

**Gear** is under `generalPenalties.gear`, not a phase metric: gear down = no credit; gear up multiplies the combined score.

## Modes

1. **Career** — optional classified promotion flights using the Normal challenge/scoring pipeline.
2. **Normal HUD mode** — configured Hardcore Landing challenges and future Disaster scenarios.
3. **Free HUD mode** — observes any fixed-wing runway approach, infers the airport/runway locally, and uses the generic scoring profile.
4. **Disasters** — systems failures (hydraulics, smoke); cards are visible but **Coming soon**.

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

Career attempts add optional stage/rank metadata to new highscore rows. Older rows remain readable and display no Career label.

## License / scope

Prototype for personal/experimental use. Not affiliated with Microsoft or Asobo.
