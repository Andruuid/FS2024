# Flight and Weather Loading in Microsoft Flight Simulator 2024

## Purpose

This document summarizes what we established about:

- creating and shipping custom weather presets
- using `.WPR` files
- saving and loading `.FLT` files
- loading flights from a C# / SimConnect add-on
- what is confirmed
- what is only partially supported
- what still needs experiments
- recommended architecture for a prototype and production add-on

The main use case is a separate C# challenge application for Microsoft Flight Simulator 2024, with scenarios such as hard landings, turbulence, disasters, failures, and scored approaches.

---

# 1. Turbulence in FS2024

## Confirmed

FS2024 does not expose a simple user-facing turbulence intensity slider in the normal custom weather UI.

The weather UI may expose:

- wind direction
- wind speed
- gust direction
- some gust-related controls
- cloud layers
- precipitation
- storm conditions

But the `0–360°` gust control is only a direction. It is not a turbulence strength control.

The simulator generates turbulence indirectly from the weather model, for example through:

- wind
- gusts
- clouds
- convection
- thunderstorms
- terrain interaction
- mountain waves
- atmospheric instability

There is also a global realism/assistance setting that changes how strongly turbulence affects the aircraft. That setting does not create turbulence by itself.

## Important conclusion

There is no known clean API like:

```csharp
SetTurbulenceStrength(0.8f);
```

There is also no known writable SimConnect variable that directly sets turbulence intensity.

---

# 2. Can turbulence be controlled through SimConnect?

## Confirmed

Standard SimConnect does not appear to expose a documented writable variable or event for directly setting atmospheric turbulence strength.

There is no known official call such as:

```csharp
simConnect.SetWeatherTurbulence(...);
```

or:

```csharp
simConnect.LoadWeatherPreset(...);
```

## Possible workaround

A C# add-on could simulate turbulence by applying disturbances to the aircraft, for example:

- vertical acceleration
- roll impulses
- yaw impulses
- pitch disturbances
- control input noise
- force or moment injection, if available and stable

Conceptually:

```csharp
verticalImpulse = Noise(time * frequencyA) * intensity;
rollImpulse     = Noise(time * frequencyB) * intensity;
yawImpulse      = Noise(time * frequencyC) * intensity;
```

## Risks

This can feel artificial and may conflict with:

- autopilot
- autothrust
- Airbus flight-envelope protection
- fly-by-wire logic
- add-on aircraft internal systems
- multiplayer or replay systems

## Status

This is experimental and must be tested per aircraft.

---

# 3. Custom weather presets

## Confirmed

Custom weather presets can be stored as `.WPR` files.

A weather preset can define weather conditions such as:

- cloud layers
- wind layers
- gusts
- precipitation
- pressure
- temperature
- storm conditions

Some properties may exist in the file format even if the normal UI does not expose them clearly.

## Example package layout

```text
andi-hardcore-landings-weather/
├── manifest.json
├── layout.json
└── WeatherPresets/
    ├── HL_Clear.WPR
    ├── HL_LightTurbulence.WPR
    ├── HL_ModerateTurbulence.WPR
    ├── HL_StrongTurbulence.WPR
    ├── HL_SevereTurbulence.WPR
    ├── HL_Crosswind15.WPR
    ├── HL_Crosswind25.WPR
    ├── HL_Storm.WPR
    ├── HL_MountainWave.WPR
    └── HL_MicroburstTest.WPR
```

## Shipping the presets

The normal deployment method is a standard FS2024 Community package.

The package is copied to the user's Community folder.

You do not need to modify the main FS2024 installation folder.

You do not need to search through the protected simulator installation for built-in weather files.

## Simple deployment

The easiest distribution format is:

```text
HardcoreLandingsWeather.zip
└── andi-hardcore-landings-weather/
    ├── manifest.json
    ├── layout.json
    └── WeatherPresets/
```

The user extracts the package folder into the Community folder.

## More polished deployment

A custom installer can:

1. detect Steam or Microsoft Store installation
2. find the Community folder
3. copy the package there
4. optionally verify installation

This is optional. It is not required for an MVP.

---

# 4. Community folder deployment

## Confirmed

A normal add-on should be installed as a Community package.

The safest deployment options are:

### Option A: manual ZIP installation

Lowest complexity.

The user copies one folder to Community.

### Option B: installer asks for Community folder

Moderate complexity.

The installer stores the selected path and installs future updates there.

### Option C: automatic path detection

Higher complexity.

The installer detects:

- Steam package path
- Microsoft Store / Xbox package path
- custom Packages path
- Community folder location

## Recommendation

For the first prototype:

- use a ZIP
- document where the Community folder is
- avoid writing registry detection or complex install logic immediately

For a commercial version:

- use a real installer
- detect the Community folder
- verify that `manifest.json` and `layout.json` are present
- show a repair option

---

# 5. `.FLT` files

## Confirmed

A `.FLT` file stores a saved flight or scenario state.

It may include:

- aircraft position
- altitude
- heading
- airspeed
- weather reference
- date and time
- fuel
- some aircraft state
- flight plan references
- camera or world state
- mission-related fields

## Important limitation

A `.FLT` file is not guaranteed to capture every internal state of a complex aircraft.

This is especially important for:

- default A320neo V2
- iniBuilds aircraft
- FlyByWire aircraft
- PMDG aircraft
- other complex add-ons

Some aircraft maintain important state in:

- WASM modules
- local variables
- custom JavaScript systems
- aircraft-specific save systems
- internal databases
- FMS state not represented in the generic `.FLT`

Therefore, loading a `.FLT` may restore position but leave the aircraft partially inconsistent.

---

# 6. Saving a `.FLT` in FS2024

## Confirmed method in Developer Mode

The working route is:

```text
Debug
  └── Aircraft
       └── FLT Files
            └── Export Current FLT
```

In the screenshot, the `Aircraft` submenu is under the `Debug` menu.

The export option appears only after opening that submenu.

## Practical workflow

1. Start a flight.
2. Put the aircraft in the desired position.
3. Configure altitude, speed, heading, fuel, weather, and systems.
4. Open Developer Mode.
5. Open:
   `Debug → Aircraft → FLT Files`
6. Click:
   `Export Current FLT`
7. Save the file.
8. Open the resulting `.FLT` in a text editor.
9. Clean up or modify values as needed.
10. Test reloading it.

## UI regression

FS2024 no longer provides the same obvious user-facing save-flight workflow that older simulators had.

The EFB primarily exposes flight-plan saving, usually `.PLN`, not full flight-state saving.

There have been workarounds involving typing a `.flt` extension into file dialogs, but this should not be treated as a stable product workflow.

## Recommendation

Use Developer Mode export for building scenario templates.

---

# 7. `AsoboReport-RunningSession.txt`

## Confirmed

This file is not part of the saved flight.

It is a diagnostic report created by FS2024.

It may contain:

- simulator build number
- session ID
- CPU and GPU
- driver version
- memory use
- loaded packages
- loaded WASM modules
- SimConnect clients
- current aircraft
- current coordinates
- recent simulator state transitions
- crash or hang-detector data
- call stacks
- renderer information

In the observed example, it reported:

```text
Where="HangDetector"
Code=0xC0000194
```

The report also showed that Developer Mode was active and that the current aircraft was the A320neo V2.

## Why it may appear during save/export

The simulator may temporarily become unresponsive while:

- opening the Windows file dialog
- saving
- loading
- teleporting
- switching between world map and flight
- processing a large aircraft state

The hang detector can generate a report even if the simulator does not fully crash.

## What to do with it

Do not include it in the add-on.

Safe action:

```text
Keep:
- MyChallenge.flt
- required .wpr files
- package metadata

Ignore/delete:
- AsoboReport-RunningSession.txt
- crash reports
- hang reports
- unrelated diagnostics
```

## Privacy warning

These reports can expose:

- installed add-ons
- hardware
- driver version
- coordinates
- session IDs
- aircraft
- package names

Do not publish them casually.

---

# 8. Referencing a weather preset from a `.FLT`

## Expected pattern

A `.FLT` can reference a weather file with a weather section similar to:

```ini
[Weather]
UseLiveWeather=False
UseWeatherFile=True
WeatherPresetFile=.\WeatherPresets\HL_StrongTurbulence.WPR
WeatherCanBeLive=False
```

## Recommended folder structure

```text
Challenge01/
├── Challenge01.FLT
└── WeatherPresets/
    └── HL_StrongTurbulence.WPR
```

Using a relative path is safer than relying on a global absolute path.

## What must be tested

Test whether FS2024 resolves the path correctly when the `.FLT` is:

- loaded directly from disk
- loaded through SimConnect
- included in a Community package
- launched as part of an activity
- moved to another PC
- installed in a different Community folder

---

# 9. Loading `.FLT` files from C# / SimConnect

## Likely available API

The SimConnect API traditionally includes flight load/save functions.

In C#, the wrapper may expose something similar to:

```csharp
simConnect.FlightLoad(fltPath);
```

and possibly:

```csharp
simConnect.FlightSave(...);
```

## Critical warning

The existence of the API does not guarantee reliable FS2024 behavior.

Potential problems:

- the file loads only partially
- the aircraft is teleported but systems are wrong
- weather is not applied
- the world map or loading screen interrupts the process
- the simulator hangs
- add-on aircraft internal state is lost
- the call works for one aircraft but not another
- absolute paths work but packaged relative paths do not
- the load works only when already in a flight
- the load fails when called from the main menu
- the call must be delayed until the simulator is ready

## Status

This must be treated as experimental until verified in the actual prototype.

---

# 10. Experiments to run in Codex

## Experiment 1: basic `.FLT` load

Goal:

- connect to FS2024
- call `FlightLoad`
- load a simple flight
- confirm position, altitude, heading, and aircraft

Test matrix:

```text
Aircraft:
- Cessna 172
- default A320neo V2
- another default aircraft

State:
- from main menu
- from an active free flight
- while paused
- while unpaused
```

Record:

- whether the method returns success
- whether the simulator changes state
- loading duration
- whether SimConnect disconnects
- whether the aircraft appears correctly
- whether weather changes
- whether avionics remain functional

---

## Experiment 2: weather loading through `.FLT`

Create three `.WPR` files:

```text
HL_Clear.WPR
HL_Windy.WPR
HL_Storm.WPR
```

Create three `.FLT` files referencing them.

Verify:

- cloud layers
- wind
- gusts
- precipitation
- pressure
- visibility
- turbulence behavior
- whether the correct preset is selected after load

---

## Experiment 3: packaged relative paths

Create a Community package:

```text
andi-flight-test/
├── manifest.json
├── layout.json
└── Scenario/
    ├── TestFlight.FLT
    └── WeatherPresets/
        └── HL_Storm.WPR
```

Test whether:

```ini
WeatherPresetFile=.\WeatherPresets\HL_Storm.WPR
```

works when loading the `.FLT` through:

- Developer Mode
- SimConnect
- direct file selection
- packaged activity launch

---

## Experiment 4: complex aircraft consistency

For the A320neo V2, compare before and after loading:

- battery
- engines
- fuel pumps
- hydraulics
- electrical buses
- autopilot
- autothrust
- flight directors
- gear
- flaps
- spoilers
- brakes
- FMS route
- selected altitude
- selected heading
- selected speed
- LS/ILS state
- radio frequencies
- approach mode
- flight phase

Record which values survive and which do not.

---

## Experiment 5: repeated load reliability

Load the same `.FLT`:

- 10 times
- 50 times
- after restarting the simulator
- after changing aircraft
- after changing airport
- with the C# app already connected
- with the C# app reconnecting after load

Measure:

- failures
- hangs
- crashes
- wrong aircraft state
- wrong weather
- SimConnect disconnects
- time to ready state

---

## Experiment 6: loading delay and readiness detection

Build a state machine:

```text
Disconnected
Connecting
Connected
WaitingForFlight
LoadRequested
SimulatorLoading
WaitingForAircraftReady
ScenarioReady
Failed
```

Possible readiness checks:

- aircraft title available
- latitude and longitude valid
- altitude stable
- `SIM ON GROUND` readable
- `PLANE LATITUDE` readable
- aircraft engine state readable
- no loading screen
- SimConnect heartbeat active
- several consecutive frames with valid data

---

## Experiment 7: fallback when `.FLT` loading fails

Fallback workflow:

1. user manually loads the aircraft and airport
2. add-on verifies aircraft and location
3. add-on teleports the aircraft if necessary
4. add-on applies fuel and failures
5. user selects the packaged weather preset manually
6. add-on starts scoring

This is less elegant but more reliable.

---

# 11. Recommended MVP architecture

## External C# app

Responsibilities:

- challenge selection
- SimConnect connection
- aircraft validation
- location validation
- scoring
- event triggers
- failures
- telemetry
- result report
- optional flight loading
- fallback instructions

## Community package

Responsibilities:

- `.WPR` weather presets
- optional `.FLT` templates
- optional missions or activities
- package metadata
- icons and thumbnails
- any in-sim assets

## Do not depend on one mechanism

Recommended loading sequence:

```text
1. Try automatic .FLT load.
2. Wait for simulator readiness.
3. Validate aircraft, position, and weather.
4. If validation fails, show manual fallback.
5. Continue challenge only after validation passes.
```

---

# 12. Recommended production architecture

## Scenario definition

Use JSON:

```json
{
  "id": "lszh-a320-severe-turbulence",
  "displayName": "A320 Severe Turbulence into Zurich",
  "aircraft": {
    "titleContains": "A320neo"
  },
  "flightFile": "Flights/LSZH_A320_Severe.FLT",
  "weatherPreset": "WeatherPresets/HL_SevereTurbulence.WPR",
  "fallback": {
    "airport": "LSZH",
    "runway": "28",
    "altitudeFeet": 5000,
    "headingDegrees": 280
  }
}
```

## Loader strategy

```csharp
public enum ScenarioLoadMode
{
    AutomaticFlightLoad,
    ManualSetup,
    TeleportFallback
}
```

## Validation result

```csharp
public sealed record ScenarioValidationResult(
    bool AircraftMatches,
    bool PositionMatches,
    bool AltitudeMatches,
    bool WeatherAppearsLoaded,
    bool SystemsReady,
    string? FailureReason);
```

---

# 13. What is certain

The following points are sufficiently solid to build around:

- `.WPR` files can be shipped as part of a Community package.
- The package belongs in the Community folder.
- The main FS installation should not be modified.
- A ZIP-based install is enough for an MVP.
- `.FLT` files can be exported through Developer Mode.
- The path is:
  `Debug → Aircraft → FLT Files → Export Current FLT`
- `AsoboReport-RunningSession.txt` is diagnostic junk, not scenario content.
- SimConnect does not provide a clean documented turbulence-strength setter.
- Complex aircraft may not restore perfectly from generic `.FLT` files.
- Weather and flight loading must be validated after loading.

---

# 14. What is uncertain

The following must be verified experimentally:

- whether `FlightLoad` works reliably in FS2024
- whether it works from C#
- whether it works from the main menu
- whether it works only during an active flight
- whether `.WPR` references resolve from packaged relative paths
- whether weather is fully applied when loading a `.FLT`
- whether A320neo V2 internal state survives
- whether SimConnect disconnects during loading
- whether repeated loads cause hangs
- whether Marketplace packaging allows the desired structure
- whether a packaged activity is more reliable than external `.FLT` loading

---

# 15. Recommended next Codex task

Implement a small standalone test application before integrating anything into the real add-on.

The test app should:

1. connect to SimConnect
2. display current aircraft and position
3. provide a file picker for `.FLT`
4. call `FlightLoad`
5. log every state transition
6. reconnect automatically if disconnected
7. validate aircraft and position after load
8. print a clear pass/fail report
9. store logs in JSON
10. never assume that a successful API call means a successful scenario load

Suggested project name:

```text
Fs2024FlightLoadLab
```

Suggested output:

```json
{
  "flightFile": "LSZH_A320_Test.FLT",
  "loadRequestedUtc": "2026-07-21T12:00:00Z",
  "simConnectDisconnected": true,
  "reconnected": true,
  "aircraftMatched": true,
  "positionMatched": true,
  "weatherMatched": false,
  "readyAfterSeconds": 18.4,
  "result": "PartialSuccess"
}
```

---

# 16. Final recommendation

Build the system so that automatic `.FLT` loading is a convenience, not a single point of failure.

The robust design is:

```text
Community package
    ├── weather presets
    ├── optional flight templates
    └── optional missions

External C# app
    ├── tries automatic loading
    ├── validates the result
    ├── reconnects after simulator transitions
    ├── falls back to manual setup
    └── handles scoring and challenge logic
```

The likely best path is:

1. ship `.WPR` presets normally
2. export `.FLT` templates with Developer Mode
3. test `FlightLoad` in a dedicated lab project
4. validate every loaded scenario
5. support a manual fallback
6. only later decide whether automatic loading is reliable enough for production

Do not build the entire product around assumptions that have not yet survived repeated FS2024 testing.
