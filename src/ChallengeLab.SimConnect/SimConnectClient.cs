using System.Runtime.InteropServices;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using Microsoft.FlightSimulator.SimConnect;
using MsfsSc = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace ChallengeLab.SimConnect;

/// <summary>
/// Managed SimConnect client for Challenge Lab. Requires a window handle for the message pump.
/// </summary>
public sealed class SimConnectClient : ISimBridge
{
    private const int WmUserSimConnect = 0x0402;

    private Microsoft.FlightSimulator.SimConnect.SimConnect? _sim;
    private IntPtr _hwnd;
    private bool _defsRegistered;
    private bool _eventsMapped;
    private SimConnectionState _state = SimConnectionState.Disconnected;
    private string? _statusMessage = "Not connected";
    private TaskCompletionSource<string?>? _titleTcs;
    private TaskCompletionSource<TelemetryStruct>? _spawnVerifyTcs;

    private enum Definitions
    {
        Telemetry = 1,
        InitPosition = 2,
        AircraftTitle = 3,
        PoseSet = 4,
        VelocitySet = 5
    }

    private enum Requests
    {
        Telemetry = 1,
        AircraftTitle = 2,
        SpawnVerify = 3
    }

    // Soft thresholds for post-spawn verification (mid-session teleport is imperfect).
    private const double MaxHorizontalErrorM = 800;
    private const double MaxAltitudeErrorFeet = 400;
    private const double MinAirspeedFraction = 0.45;
    private const int SpawnPulseCount = 6;

    // Aircraft config settle after teleport (reproducible start; pilot unpauses).
    private const int ConfigPulseCount = 3;
    private const int ConfigPulseDelayMs = 450;
    private const int MinConfigSettleMs = 5000;
    private const int MaxConfigSettleMs = 12000;
    private const int ConfigPollMs = 350;

    private enum Events
    {
        GearDown = 1,
        GearUp = 2,
        FlapsSet = 3,
        PauseOff = 4,
        PauseOn = 5,
        FreezeLatLon = 6,
        FreezeAlt = 7,
        FreezeAtt = 8,
        ClockHoursSet = 10,
        ClockMinutesSet = 11,
        ZuluHoursSet = 12,
        ZuluMinutesSet = 13,
        SpoilersOff = 14,
        SpoilersSet = 15,
        ParkingBrakeSet = 16,
        ActivePauseOn = 17,
        ActivePauseOff = 18
    }

    private enum Groups
    {
        Input = 1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct TelemetryStruct
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Agl;
        public double Heading;
        public double GroundTrack;
        public double Pitch;
        public double Bank;
        public double Airspeed;
        public double GroundVelocity;
        public double VerticalSpeed;
        public double GForce;
        public double SimOnGround;
        public double GearHandle;
        public double FlapsIndex;
        public double WindDir;
        public double WindVel;
        public double RadioHeight;
        public double DesignSpeedVs0;
        public double TotalWeight;
        public double SpoilersHandle;
        public double ParkingBrake;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AircraftTitleStruct
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
    }

    /// <summary>
    /// Direct settable pose. Do NOT include AIRSPEED INDICATED — often not writable and
    /// causes the whole SetDataOnSimObject to fail (position never moves).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct PoseSetStruct
    {
        public double Latitude;
        public double Longitude;
        public double AltitudeFeet;
        public double PitchDeg;
        public double BankDeg;
        public double HeadingTrueDeg;
    }

    /// <summary>
    /// Body-axis linear velocity (m/s) + rotation rates (rad/s).
    /// MSFS body axes: X right, Y up, Z forward.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct VelocitySetStruct
    {
        public double BodyX;
        public double BodyY;
        public double BodyZ;
        public double RotX;
        public double RotY;
        public double RotZ;
    }

    public SimConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public string? StatusMessage
    {
        get => _statusMessage;
        private set => _statusMessage = value;
    }

    public bool IsConnected => State == SimConnectionState.Connected && _sim is not null;

    public event EventHandler<SimConnectionState>? StateChanged;
    public event EventHandler<TelemetrySample>? TelemetryReceived;
    public event EventHandler<string>? LogMessage;

    public void Connect(IntPtr windowHandle)
    {
        if (IsConnected || State == SimConnectionState.Connecting) return;

        _hwnd = windowHandle;
        State = SimConnectionState.Connecting;
        StatusMessage = "Connecting to MSFS…";

        try
        {
            CleanupSim();
            _sim = new Microsoft.FlightSimulator.SimConnect.SimConnect(
                "ChallengeLab",
                windowHandle,
                (uint)WmUserSimConnect,
                null,
                0);

            _sim.OnRecvOpen += OnRecvOpen;
            _sim.OnRecvQuit += OnRecvQuit;
            _sim.OnRecvException += OnRecvException;
            _sim.OnRecvSimobjectData += OnRecvSimobjectData;

            StatusMessage = "Waiting for sim handshake…";
            Log("SimConnect open requested.");
        }
        catch (COMException ex)
        {
            CleanupSim();
            State = SimConnectionState.SimNotRunning;
            StatusMessage = "MSFS is not running or SimConnect is unavailable.";
            Log($"Connect failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            CleanupSim();
            State = SimConnectionState.Error;
            StatusMessage = ex.Message;
            Log($"Connect error: {ex}");
        }
    }

    public void Disconnect()
    {
        CleanupSim();
        State = SimConnectionState.Disconnected;
        StatusMessage = "Disconnected";
    }

    public void ReceiveMessage()
    {
        try
        {
            _sim?.ReceiveMessage();
        }
        catch (Exception ex)
        {
            Log($"ReceiveMessage: {ex.Message}");
            if (State == SimConnectionState.Connected)
            {
                CleanupSim();
                State = SimConnectionState.Disconnected;
                StatusMessage = "Connection lost";
            }
        }
    }

    public async Task<SpawnApplyResult> LoadScenarioAsync(
        ChallengeConfig challenge,
        string flightFileAbsolutePath,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsConnected || _sim is null)
            throw new InvalidOperationException("Not connected to the simulator.");

        // flightFileAbsolutePath is retained for API compatibility / optional future
        // same-aircraft situation loads. We intentionally do NOT call FlightLoad mid free-flight:
        // cross-aircraft FlightLoad of CustomFlight/autosave templates has crashed MSFS 2024.
        _ = flightFileAbsolutePath;

        progress?.Report("Preparing scenario…");
        await Task.Delay(150, ct);

        // --- Gate: correct aircraft already loaded (no mid-session aircraft swap) ---
        progress?.Report("Checking aircraft…");
        var actualTitle = await RequestAircraftTitleAsync(ct);
        Log($"Safe apply (no FlightLoad). Current TITLE='{actualTitle ?? "(unknown)"}'");

        if (challenge.AircraftTitles.Count > 0 &&
            actualTitle is not null &&
            !AircraftTitleMatches(actualTitle, challenge.AircraftTitles))
        {
            Log($"Aircraft mismatch — aborting load (no FlightLoad). sim='{actualTitle}' expected [{string.Join(", ", challenge.AircraftTitles)}]");
            throw new AircraftMismatchException(actualTitle, challenge.AircraftTitles);
        }

        if (actualTitle is null && challenge.AircraftTitles.Count > 0)
            Log("TITLE unavailable — continuing with spawn/time (cannot verify aircraft).");
        else if (actualTitle is not null)
            Log($"Aircraft OK: '{actualTitle}'");

        // --- Safe apply: time + weather + teleport + config (no FlightLoad) ---
        //
        // MSFS pause modes (must not be mixed up):
        // 1) Flying — sim running, pilot in control.
        // 2) ESC / menu pause — Escape opens menu; Resume to fly again. (what pilots mean by "paused")
        // 3) Active pause — separate "freeze plane, world keeps going" mode (PAUSE key). NOT (2).
        // 4) SET PAUSE ON/OFF (SimConnect PAUSE_ON/OFF) — full sim pause we control programmatically;
        //    end state when Unpause=false so pilot must resume before flying (same intent as (2)).
        //
        // Restart must behave identically for (1) and (2). We cannot close the ESC menu via
        // SimConnect; we pin the aircraft with FREEZE + SET PAUSE and always finish the same way.
        SpawnApplyResult spawnResult = SpawnApplyResult.Fail("Spawn did not complete.");
        progress?.Report("Setting time of day…");
        try
        {
            // Identical entry whether currently flying or already ESC-/SET-paused.
            await NormalizeLoadEntryAsync(ct);
            ApplyTimeOfDay(challenge.TimeOfDay);
            await Task.Delay(200, ct);

            progress?.Report("Applying weather…");
            ApplyWeather(challenge.Weather);
            await Task.Delay(250, ct);

            progress?.Report("Positioning aircraft…");
            spawnResult = await ApplySpawnAsync(challenge.Spawn, ct);

            if (spawnResult.Success)
            {
                await ConfigureAndSettleAsync(challenge.AircraftSetup, progress, ct);
            }
            else
            {
                progress?.Report("Positioning failed…");
                Log($"Spawn apply failed: {spawnResult.Message}");
            }
        }
        finally
        {
            // SET PAUSE before unfreeze so we never get a free-flight frame mid-Restart.
            if (challenge.AircraftSetup.Unpause)
            {
                EnsureActivePauseOff(); // third state only — do not leave active-pause on
                PauseSim(false);
            }
            else
            {
                // Same end state for Restart-from-flying and Restart-from-ESC-pause:
                // SET PAUSE ON (not active pause). Pilot resumes when ready.
                ForceSetPauseOn();
            }

            // Never leave FREEZE stuck (plane unresponsive after pilot resumes).
            FreezePose(false);
        }

        if (spawnResult.Success)
        {
            progress?.Report(
                challenge.AircraftSetup.Unpause
                    ? "Challenge armed."
                    : "Ready — sim PAUSED (resume to fly). Not active pause.");
            var tod = challenge.TimeOfDay ?? new TimeOfDayConfig();
            Log(
                $"Scenario load complete (safe path). Spawn IAS {challenge.Spawn.AirspeedKts:0} kt · " +
                $"alt {challenge.Spawn.AltitudeFeet:0} ft · hdg {challenge.Spawn.HeadingDeg:0}° · " +
                $"time {tod.Hour:00}:{tod.Minute:00} {(tod.UseZuluTime ? "Z" : "local")} · ac '{actualTitle ?? "?"}' · " +
                $"verify horiz={spawnResult.HorizontalErrorM:0} m altErr={spawnResult.AltErrorFeet:0} ft ias={spawnResult.AirspeedKts:0} · " +
                $"setPause={!challenge.AircraftSetup.Unpause}.");
        }

        return spawnResult;
    }

    /// <summary>
    /// Normalize entry for Restart/Start so flying and ESC-/SET-paused take the same path.
    /// Pins pose with FREEZE + SET PAUSE ON. Clears active pause only as a third-state safety
    /// (active pause ≠ ESC menu pause).
    /// </summary>
    private async Task NormalizeLoadEntryAsync(CancellationToken ct)
    {
        EnsureEvents();
        EnsureActivePauseOff();
        ForceSetPauseOn();
        FreezePose(true);
        await Task.Delay(200, ct);
        // Re-assert: MSFS can drop the first PAUSE_ON if ESC menu or active-pause was active.
        ForceSetPauseOn();
        await Task.Delay(150, ct);
        Log("Load entry normalized: SET PAUSE ON + pose frozen (ESC vs flying entry unified; active-pause cleared if any).");
    }

    /// <summary>
    /// Assert SimConnect SET PAUSE ON (PAUSE_ON). Not active pause, not ESC menu UI.
    /// Safe when already SET-paused, ESC-paused, or flying.
    /// </summary>
    private void ForceSetPauseOn()
    {
        EnsureActivePauseOff();
        PauseSim(true);
        PauseSim(true); // second pulse; PAUSE_ON is set-style, not toggle
    }

    /// <summary>
    /// Active pause is a third MSFS mode (freeze plane / world keeps going). Clear it so it
    /// cannot block SET PAUSE or leave the aircraft in a hybrid freeze. Does not open/close ESC.
    /// </summary>
    private void EnsureActivePauseOff()
    {
        if (_sim is null) return;
        try
        {
            EnsureEvents();
            _sim.TransmitClientEvent(
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                Events.ActivePauseOff,
                0,
                Groups.Input,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
        catch (Exception ex)
        {
            Log($"EnsureActivePauseOff: {ex.Message}");
        }
    }

    /// <summary>
    /// Mid-session airborne spawn: pause + freeze, InitPosition + pose + body velocity,
    /// verify, one retry on failure. Always unfreezes via caller finally.
    /// </summary>
    private async Task<SpawnApplyResult> ApplySpawnAsync(SpawnConfig spawn, CancellationToken ct)
    {
        EnsureEvents();
        EnsureDefinitions();

        if (Math.Abs(spawn.Latitude) < 1e-8 && Math.Abs(spawn.Longitude) < 1e-8)
            Log("WARNING: spawn lat/lon are ~0 — check challenge JSON loaded correctly.");

        Log(
            $"ApplySpawn target lat={spawn.Latitude:F5} lon={spawn.Longitude:F5} " +
            $"alt={spawn.AltitudeFeet:F0} ft hdg={spawn.HeadingDeg:F1}° pitch={spawn.PitchDeg:F2} " +
            $"bank={spawn.BankDeg:F2} ias={spawn.AirspeedKts:F0}");

        // Entry may already be SET-paused+frozen from NormalizeLoadEntryAsync; re-assert anyway.
        ForceSetPauseOn();
        FreezePose(true);
        await Task.Delay(120, ct);

        await PulseSpawnAsync(spawn, SpawnPulseCount, ct);
        await Task.Delay(350, ct);

        var result = await VerifySpawnAsync(spawn, ct);
        if (result.Success)
            return result;

        Log($"Spawn verify failed (attempt 1): {result.Message} — retrying pulses…");
        await PulseSpawnAsync(spawn, SpawnPulseCount, ct);
        await Task.Delay(400, ct);
        result = await VerifySpawnAsync(spawn, ct);
        if (!result.Success)
            Log($"Spawn verify failed (attempt 2): {result.Message}");
        return result;
    }

    private async Task PulseSpawnAsync(SpawnConfig spawn, int pulses, CancellationToken ct)
    {
        for (var i = 1; i <= pulses; i++)
        {
            Teleport(spawn);
            SetPoseDirect(spawn);
            SetVelocityForSpawn(spawn);
            ReceiveMessage();
            await Task.Delay(280, ct);
            Log($"ApplySpawn pulse {i}/{pulses} done");
        }
    }

    /// <summary>
    /// Zero rotation rates and inject body-forward speed matching spawn IAS.
    /// Body axes: X right, Y up, Z forward (m/s).
    /// </summary>
    private void SetVelocityForSpawn(SpawnConfig spawn)
    {
        if (_sim is null) return;

        try
        {
            EnsureDefinitions();
            // knots → m/s; keep vertical/lateral near zero so residual freefall energy dies.
            var speedMs = Math.Max(0, spawn.AirspeedKts) * 0.514444;
            var pitchRad = spawn.PitchDeg * Math.PI / 180.0;
            // Small vertical component from pitch only (pitch convention: +nose up).
            var bodyZ = speedMs * Math.Cos(pitchRad);
            var bodyY = speedMs * Math.Sin(pitchRad);

            var vel = new VelocitySetStruct
            {
                BodyX = 0,
                BodyY = bodyY,
                BodyZ = bodyZ,
                RotX = 0,
                RotY = 0,
                RotZ = 0
            };

            _sim.SetDataOnSimObject(
                Definitions.VelocitySet,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                vel);

            Log($"VelocitySet bodyZ={bodyZ:F1} m/s bodyY={bodyY:F1} m/s (ias {spawn.AirspeedKts:F0} kt)");
        }
        catch (Exception ex)
        {
            Log($"SetVelocityForSpawn: {ex.Message}");
        }
    }

    private async Task<SpawnApplyResult> VerifySpawnAsync(SpawnConfig spawn, CancellationToken ct)
    {
        var sample = await RequestSpawnSnapshotAsync(ct);
        if (sample is null)
            return SpawnApplyResult.Fail("Could not read back aircraft position after teleport.");

        var horizM = HaversineMeters(spawn.Latitude, spawn.Longitude, sample.Value.Latitude, sample.Value.Longitude);
        var altErr = Math.Abs(sample.Value.Altitude - spawn.AltitudeFeet);
        var onGround = sample.Value.SimOnGround > 0.5;
        var ias = sample.Value.Airspeed;
        var minIas = spawn.AirspeedKts * MinAirspeedFraction;

        Log(
            $"Spawn verify: lat={sample.Value.Latitude:F5} lon={sample.Value.Longitude:F5} " +
            $"alt={sample.Value.Altitude:F0} ias={ias:F0} onGround={onGround} " +
            $"horizErr={horizM:F0} m altErr={altErr:F0} ft");

        if (horizM > MaxHorizontalErrorM)
        {
            return SpawnApplyResult.Fail(
                $"Aircraft still {horizM:0} m from spawn (limit {MaxHorizontalErrorM:0} m). Restart after crash may need a brief pause — try again.",
                altErr, horizM, ias, onGround, sample.Value.Latitude, sample.Value.Longitude, sample.Value.Altitude);
        }

        if (altErr > MaxAltitudeErrorFeet)
        {
            return SpawnApplyResult.Fail(
                $"Altitude error {altErr:0} ft (limit {MaxAltitudeErrorFeet:0} ft). Aircraft may still be on the ground.",
                altErr, horizM, ias, onGround, sample.Value.Latitude, sample.Value.Longitude, sample.Value.Altitude);
        }

        // Spawn altitudes are mid-final (~thousands of feet) — must not remain on ground.
        if (onGround && spawn.AltitudeFeet > 500)
        {
            return SpawnApplyResult.Fail(
                "Aircraft still reports on ground after airborne spawn. Try Restart once more, or slew briefly then Restart.",
                altErr, horizM, ias, onGround: true, sample.Value.Latitude, sample.Value.Longitude, sample.Value.Altitude);
        }

        if (spawn.AirspeedKts >= 80 && ias < minIas)
        {
            return SpawnApplyResult.Fail(
                $"Airspeed too low after spawn ({ias:0} kt, expected ≥ {minIas:0} kt).",
                altErr, horizM, ias, onGround, sample.Value.Latitude, sample.Value.Longitude, sample.Value.Altitude);
        }

        return SpawnApplyResult.Ok(
            "Spawn verified.",
            altErr, horizM, ias,
            sample.Value.Latitude, sample.Value.Longitude, sample.Value.Altitude,
            onGround);
    }

    private async Task<TelemetryStruct?> RequestSpawnSnapshotAsync(CancellationToken ct)
    {
        if (_sim is null) return null;

        try
        {
            EnsureDefinitions();
            var tcs = new TaskCompletionSource<TelemetryStruct>(TaskCreationOptions.RunContinuationsAsynchronously);
            _spawnVerifyTcs = tcs;

            _sim.RequestDataOnSimObject(
                Requests.SpawnVerify,
                Definitions.Telemetry,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.ONCE,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0, 0, 0);

            for (var i = 0; i < 40 && !tcs.Task.IsCompleted; i++)
            {
                ct.ThrowIfCancellationRequested();
                ReceiveMessage();
                await Task.Delay(50, ct);
            }

            if (!tcs.Task.IsCompleted)
            {
                Log("Spawn verify telemetry request timed out.");
                _spawnVerifyTcs = null;
                return null;
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            Log($"RequestSpawnSnapshot: {ex.Message}");
            _spawnVerifyTcs = null;
            return null;
        }
    }

    private static double HaversineMeters(double lat1, double lon1, double lat2, double lon2)
    {
        const double r = 6371000.0;
        var p1 = lat1 * Math.PI / 180.0;
        var p2 = lat2 * Math.PI / 180.0;
        var dp = (lat2 - lat1) * Math.PI / 180.0;
        var dl = (lon2 - lon1) * Math.PI / 180.0;
        var a = Math.Sin(dp / 2) * Math.Sin(dp / 2) +
                Math.Cos(p1) * Math.Cos(p2) * Math.Sin(dl / 2) * Math.Sin(dl / 2);
        return 2 * r * Math.Asin(Math.Sqrt(a));
    }

    public void ApplyTimeOfDay(TimeOfDayConfig? timeOfDay)
    {
        if (_sim is null) return;

        var tod = timeOfDay ?? new TimeOfDayConfig();
        var hour = (uint)Math.Clamp(tod.Hour, 0, 23);
        var minute = (uint)Math.Clamp(tod.Minute, 0, 59);

        try
        {
            EnsureEvents();
            if (tod.UseZuluTime)
            {
                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.ZuluHoursSet, hour,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.ZuluMinutesSet, minute,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
            else
            {
                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.ClockHoursSet, hour,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.ClockMinutesSet, minute,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }

            Log($"Time of day set: {hour:00}:{minute:00} {(tod.UseZuluTime ? "Zulu" : "local")}");
        }
        catch (Exception ex)
        {
            Log($"ApplyTimeOfDay: {ex.Message}");
        }
    }

    /// <summary>
    /// True if the live TITLE matches any configured title (substring either way, case-insensitive).
    /// </summary>
    internal static bool AircraftTitleMatches(string? actualTitle, IReadOnlyList<string> expectedTitles)
    {
        if (expectedTitles.Count == 0)
            return true; // nothing configured → don't block
        if (string.IsNullOrWhiteSpace(actualTitle))
            return false;

        foreach (var expected in expectedTitles)
        {
            if (string.IsNullOrWhiteSpace(expected))
                continue;
            if (actualTitle.Contains(expected, StringComparison.OrdinalIgnoreCase) ||
                expected.Contains(actualTitle, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private async Task<string?> RequestAircraftTitleAsync(CancellationToken ct)
    {
        if (_sim is null) return null;

        try
        {
            EnsureDefinitions();
            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _titleTcs = tcs;

            _sim.RequestDataOnSimObject(
                Requests.AircraftTitle,
                Definitions.AircraftTitle,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_PERIOD.ONCE,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0, 0, 0);

            // Pump SimConnect ourselves — WndProc alone can miss packets during await on some hosts.
            for (var i = 0; i < 40 && !tcs.Task.IsCompleted; i++)
            {
                ct.ThrowIfCancellationRequested();
                ReceiveMessage();
                await Task.Delay(50, ct);
            }

            if (!tcs.Task.IsCompleted)
            {
                Log("Aircraft TITLE request timed out.");
                _titleTcs = null;
                return null;
            }

            return await tcs.Task;
        }
        catch (Exception ex)
        {
            Log($"RequestAircraftTitle: {ex.Message}");
            _titleTcs = null;
            return null;
        }
    }

    private void PauseSim(bool pause)
    {
        if (_sim is null) return;
        try
        {
            EnsureEvents();
            // PAUSE_ON / PAUSE_OFF are set-style (not toggle) — safe from any prior state.
            _sim.TransmitClientEvent(
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                pause ? Events.PauseOn : Events.PauseOff,
                0,
                Groups.Input,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
        catch (Exception ex)
        {
            Log($"PauseSim({pause}): {ex.Message}");
        }
    }

    public void ConfigureAircraft(AircraftSetupConfig setup)
    {
        if (_sim is null) return;

        try
        {
            EnsureEvents();
            _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                setup.GearDown ? Events.GearDown : Events.GearUp, 0,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

            // FLAPS_SET uses 0–16383 (index 0 = clean; ~4096 per step for 0–4).
            var flapsIndex = Math.Clamp(setup.FlapsHandleIndex, 0, 5);
            var flaps = (uint)Math.Clamp(flapsIndex * (16383 / 4), 0, 16383);
            _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.FlapsSet, flaps,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

            if (setup.SpoilersRetracted)
            {
                _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersOff, 0,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersSet, 0,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }

            // PARKING_BRAKE_SET: 1 = on, 0 = off (preferred over toggle).
            _sim.TransmitClientEvent(
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                Events.ParkingBrakeSet,
                setup.ParkingBrakeOn ? 1u : 0u,
                Groups.Input,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
        catch (Exception ex)
        {
            Log($"ConfigureAircraft: {ex.Message}");
        }
    }

    /// <summary>
    /// Apply gear/flaps/spoilers/parking brake with systems allowed to run under FREEZE.
    /// SET PAUSE ON freezes surface travel, so we temporarily PAUSE_OFF while holding FREEZE.
    /// Always ends on SET PAUSE ON so Restart-from-flying and Restart-from-ESC-pause match.
    /// </summary>
    private async Task ConfigureAndSettleAsync(
        AircraftSetupConfig setup,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Configuring gear, flaps, spoilers…");
        var started = DateTimeOffset.UtcNow;

        // Pin pose; release SET PAUSE so hydraulics/surfaces can move.
        // (Active pause is unrelated — only cleared so it cannot block surface motion.)
        FreezePose(true);
        EnsureActivePauseOff();
        PauseSim(false);
        await Task.Delay(120, ct);
        Log("Config settle: systems running under FREEZE (SET PAUSE off; not active pause).");

        for (var i = 1; i <= ConfigPulseCount; i++)
        {
            ConfigureAircraft(setup);
            // Keep pin in case resume/ESC interactions cleared freeze on some builds.
            FreezePose(true);
            Log($"Config pulse {i}/{ConfigPulseCount} " +
                $"(gear={(setup.GearDown ? "down" : "up")} flaps={setup.FlapsHandleIndex} " +
                $"spoilersIn={setup.SpoilersRetracted} parkBrake={setup.ParkingBrakeOn})");
            await Task.Delay(ConfigPulseDelayMs, ct);
        }

        progress?.Report("Waiting for aircraft to settle (min 5s)…");

        var matched = false;
        string? lastDetail = null;
        while ((DateTimeOffset.UtcNow - started).TotalMilliseconds < MaxConfigSettleMs)
        {
            FreezePose(true);
            var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            var sample = await RequestSpawnSnapshotAsync(ct);
            if (sample is not null)
            {
                matched = ConfigMatches(setup, sample.Value, out lastDetail);
                if (matched && elapsed >= MinConfigSettleMs)
                {
                    Log($"Config settled OK after {elapsed:0} ms — {lastDetail}");
                    break;
                }
            }

            if (matched && (DateTimeOffset.UtcNow - started).TotalMilliseconds >= MinConfigSettleMs)
                break;

            await Task.Delay(ConfigPollMs, ct);
        }

        var remaining = MinConfigSettleMs - (DateTimeOffset.UtcNow - started).TotalMilliseconds;
        if (remaining > 0)
        {
            progress?.Report("Waiting for aircraft to settle (min 5s)…");
            await Task.Delay((int)Math.Ceiling(remaining), ct);
            var sample = await RequestSpawnSnapshotAsync(ct);
            if (sample is not null)
                matched = ConfigMatches(setup, sample.Value, out lastDetail);
        }

        // Final config pulse + SET PAUSE ON — same end state for every Restart entry path.
        ConfigureAircraft(setup);
        ForceSetPauseOn();
        await Task.Delay(100, ct);
        ForceSetPauseOn();

        if (matched)
        {
            progress?.Report("Config ready — sim PAUSED (resume to fly). Not active pause.");
            Log($"Config verify OK: {lastDetail}");
        }
        else
        {
            // Soft-fail: do not block restart forever (aircraft-specific surface quirks).
            progress?.Report("Config timeout — sim PAUSED. Check gear/flaps/spoilers, then resume.");
            Log($"Config verify soft-fail after settle: {lastDetail ?? "no telemetry"} " +
                $"(gear want={(setup.GearDown ? "down" : "up")} flaps={setup.FlapsHandleIndex} " +
                $"spoilersIn={setup.SpoilersRetracted} parkBrake={setup.ParkingBrakeOn})");
        }
    }

    private static bool ConfigMatches(
        AircraftSetupConfig setup,
        TelemetryStruct t,
        out string detail)
    {
        var gearDown = t.GearHandle > 0.5;
        var flaps = (int)Math.Round(t.FlapsIndex);
        // Normalize spoiler handle to 0–1 (sim may report position or percent).
        var spoiler01 = t.SpoilersHandle > 1.5 ? t.SpoilersHandle / 100.0 : t.SpoilersHandle;
        var spoilersOut = spoiler01 > 0.05;
        var parkOn = t.ParkingBrake > 0.5;

        var gearOk = gearDown == setup.GearDown;
        var flapsOk = flaps == Math.Clamp(setup.FlapsHandleIndex, 0, 5);
        var spoilersOk = !setup.SpoilersRetracted || !spoilersOut;
        var brakeOk = setup.ParkingBrakeOn == parkOn;

        detail =
            $"gear={(gearDown ? "down" : "up")}({(gearOk ? "ok" : "want " + (setup.GearDown ? "down" : "up"))}) " +
            $"flaps={flaps}({(flapsOk ? "ok" : "want " + setup.FlapsHandleIndex)}) " +
            $"spoilers={t.SpoilersHandle:0.##}({(spoilersOk ? "ok" : "want in")}) " +
            $"park={(parkOn ? "on" : "off")}({(brakeOk ? "ok" : "want " + (setup.ParkingBrakeOn ? "on" : "off"))})";

        return gearOk && flapsOk && spoilersOk && brakeOk;
    }

    public void ApplyWeather(WeatherConfig weather)
    {
        if (_sim is null) return;

        try
        {
            if (weather.UseLiveWeather)
            {
                // Leave live weather alone
                return;
            }

            _sim.WeatherSetModeCustom();
            _sim.WeatherSetModeGlobal();

            // Build a simple METAR-ish observation if not provided
            var metar = weather.Metar;
            if (string.IsNullOrWhiteSpace(metar))
            {
                var dir = ((int)Math.Round(weather.WindDirectionDeg / 10.0) * 10).ToString("000");
                var spd = ((int)Math.Round(weather.WindVelocityKts)).ToString("00");
                var gust = weather.GustKts > weather.WindVelocityKts
                    ? $"G{(int)Math.Round(weather.GustKts):00}"
                    : "";
                metar = $"GLOB 010000Z {dir}{spd}{gust}KT 9999 FEW030 15/10 Q1013";
            }

            _sim.WeatherSetObservation(0, metar);
            Log($"Weather set: {metar}");
        }
        catch (Exception ex)
        {
            Log($"ApplyWeather: {ex.Message}");
        }
    }

    public void Teleport(SpawnConfig spawn)
    {
        if (_sim is null) return;

        try
        {
            EnsureDefinitions();

            // SIMCONNECT_DATA_INITPOSITION: altitude feet MSL, heading degrees, airspeed knots.
            var ias = (uint)Math.Max(0, Math.Round(spawn.AirspeedKts));
            var init = new SIMCONNECT_DATA_INITPOSITION
            {
                Latitude = spawn.Latitude,
                Longitude = spawn.Longitude,
                Altitude = spawn.AltitudeFeet,
                Pitch = spawn.PitchDeg,
                Bank = spawn.BankDeg,
                Heading = spawn.HeadingDeg,
                OnGround = 0,
                Airspeed = ias
            };

            _sim.SetDataOnSimObject(
                Definitions.InitPosition,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                init);

            Log(
                $"Teleport (InitPosition) lat={spawn.Latitude:F5} lon={spawn.Longitude:F5} " +
                $"alt={spawn.AltitudeFeet:F0} hdg={spawn.HeadingDeg:F0} ias={ias} kt");
        }
        catch (Exception ex)
        {
            Log($"Teleport: {ex.Message}");
        }
    }

    /// <summary>
    /// Set lat/lon/alt/attitude via individual simvars (backup when InitPosition is flaky).
    /// </summary>
    private void SetPoseDirect(SpawnConfig spawn)
    {
        if (_sim is null) return;

        try
        {
            EnsureDefinitions();
            var pose = new PoseSetStruct
            {
                Latitude = spawn.Latitude,
                Longitude = spawn.Longitude,
                AltitudeFeet = spawn.AltitudeFeet,
                PitchDeg = spawn.PitchDeg,
                BankDeg = spawn.BankDeg,
                HeadingTrueDeg = spawn.HeadingDeg
            };

            _sim.SetDataOnSimObject(
                Definitions.PoseSet,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                pose);

            Log(
                $"PoseSet lat={pose.Latitude:F5} lon={pose.Longitude:F5} " +
                $"alt={pose.AltitudeFeet:F0} hdg={pose.HeadingTrueDeg:F0}");
        }
        catch (Exception ex)
        {
            Log($"SetPoseDirect: {ex.Message}");
        }
    }

    private void FreezePose(bool freeze)
    {
        if (_sim is null) return;
        try
        {
            EnsureEvents();
            var v = freeze ? 1u : 0u;
            _sim.TransmitClientEvent(
                MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.FreezeLatLon, v,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            _sim.TransmitClientEvent(
                MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.FreezeAlt, v,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            _sim.TransmitClientEvent(
                MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.FreezeAtt, v,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            if (freeze)
                Log($"FreezePose({freeze})");
        }
        catch (Exception ex)
        {
            Log($"FreezePose: {ex.Message}");
        }
    }

    public void Dispose() => Disconnect();

    private void OnRecvOpen(Microsoft.FlightSimulator.SimConnect.SimConnect sender, SIMCONNECT_RECV_OPEN data)
    {
        Log($"Connected to {data.szApplicationName}");
        StatusMessage = $"Connected — {data.szApplicationName}";
        State = SimConnectionState.Connected;
        EnsureDefinitions();
        EnsureEvents();
        StartTelemetry();
    }

    private void OnRecvQuit(Microsoft.FlightSimulator.SimConnect.SimConnect sender, SIMCONNECT_RECV data)
    {
        Log("Simulator quit.");
        CleanupSim();
        State = SimConnectionState.Disconnected;
        StatusMessage = "Simulator closed";
    }

    private void OnRecvException(Microsoft.FlightSimulator.SimConnect.SimConnect sender, SIMCONNECT_RECV_EXCEPTION data)
    {
        var name = data.dwException switch
        {
            9 => "EVENT_ID_DUPLICATE",
            14 => "WEATHER_INVALID_METAR",
            20 => "DATA_ERROR (bad FlightLoad file type?)",
            23 => "LOAD_FLIGHTPLAN_FAILED",
            28 => "DEFINITION_ERROR",
            29 => "DUPLICATE_ID",
            _ => data.dwException.ToString()
        };
        Log($"SimConnect exception: {name} (code {data.dwException}, send {data.dwSendID}, index {data.dwIndex})");
    }

    private void OnRecvSimobjectData(Microsoft.FlightSimulator.SimConnect.SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID == (uint)Requests.AircraftTitle)
        {
            try
            {
                var title = data.dwData[0] is AircraftTitleStruct s
                    ? s.Title
                    : data.dwData[0]?.ToString();
                _titleTcs?.TrySetResult(string.IsNullOrWhiteSpace(title) ? null : title.Trim());
            }
            catch (Exception ex)
            {
                Log($"Title parse: {ex.Message}");
                _titleTcs?.TrySetResult(null);
            }
            finally
            {
                _titleTcs = null;
            }
            return;
        }

        if (data.dwRequestID == (uint)Requests.SpawnVerify)
        {
            try
            {
                var t = (TelemetryStruct)data.dwData[0];
                _spawnVerifyTcs?.TrySetResult(t);
            }
            catch (Exception ex)
            {
                Log($"Spawn verify parse: {ex.Message}");
                _spawnVerifyTcs?.TrySetException(ex);
            }
            finally
            {
                _spawnVerifyTcs = null;
            }
            return;
        }

        if (data.dwRequestID != (uint)Requests.Telemetry) return;

        try
        {
            var t = (TelemetryStruct)data.dwData[0];
            var sample = new TelemetrySample
            {
                Timestamp = DateTimeOffset.UtcNow,
                Latitude = t.Latitude,
                Longitude = t.Longitude,
                AltitudeFeet = t.Altitude,
                AglFeet = t.Agl,
                HeadingTrueDeg = t.Heading,
                GroundTrackTrueDeg = t.GroundTrack,
                PitchDeg = t.Pitch,
                BankDeg = t.Bank,
                AirspeedKts = t.Airspeed,
                GroundSpeedKts = t.GroundVelocity,
                VerticalSpeedFpm = t.VerticalSpeed,
                GForce = t.GForce,
                SimOnGround = t.SimOnGround > 0.5,
                GearHandlePosition = t.GearHandle,
                FlapsHandleIndex = (int)Math.Round(t.FlapsIndex),
                WindDirectionDeg = t.WindDir,
                WindVelocityKts = t.WindVel,
                RadioHeightFeet = t.RadioHeight,
                DesignSpeedVs0Kts = t.DesignSpeedVs0,
                TotalWeightLbs = t.TotalWeight > 0 ? t.TotalWeight : null
            };
            TelemetryReceived?.Invoke(this, sample);
        }
        catch (Exception ex)
        {
            Log($"Telemetry parse: {ex.Message}");
        }
    }

    private void EnsureDefinitions()
    {
        if (_sim is null || _defsRegistered) return;

        _sim.AddToDataDefinition(Definitions.Telemetry, "PLANE LATITUDE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "PLANE LONGITUDE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "PLANE ALTITUDE", "feet",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "PLANE ALT ABOVE GROUND", "feet",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "PLANE HEADING DEGREES TRUE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        // Direction of CG motion over ground (not fuselage crab heading)
        _sim.AddToDataDefinition(Definitions.Telemetry, "GPS GROUND TRUE TRACK", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "PLANE PITCH DEGREES", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "PLANE BANK DEGREES", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AIRSPEED INDICATED", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "GROUND VELOCITY", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "VERTICAL SPEED", "feet per minute",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "G FORCE", "GForce",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "SIM ON GROUND", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "GEAR HANDLE POSITION", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "FLAPS HANDLE INDEX", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AMBIENT WIND DIRECTION", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AMBIENT WIND VELOCITY", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "RADIO HEIGHT", "feet",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "DESIGN SPEED VS0", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "TOTAL WEIGHT", "pounds",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "SPOILERS HANDLE POSITION", "position",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "BRAKE PARKING POSITION", "position",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);

        _sim.RegisterDataDefineStruct<TelemetryStruct>(Definitions.Telemetry);

        _sim.AddToDataDefinition(Definitions.InitPosition, "Initial Position", null,
            SIMCONNECT_DATATYPE.INITPOSITION, 0, MsfsSc.SIMCONNECT_UNUSED);

        _sim.AddToDataDefinition(Definitions.AircraftTitle, "TITLE", null,
            SIMCONNECT_DATATYPE.STRING256, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.RegisterDataDefineStruct<AircraftTitleStruct>(Definitions.AircraftTitle);

        // Direct pose write — lat/lon/alt/attitude (backup when InitPosition is flaky).
        _sim.AddToDataDefinition(Definitions.PoseSet, "PLANE LATITUDE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.PoseSet, "PLANE LONGITUDE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.PoseSet, "PLANE ALTITUDE", "feet",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.PoseSet, "PLANE PITCH DEGREES", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.PoseSet, "PLANE BANK DEGREES", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.PoseSet, "PLANE HEADING DEGREES TRUE", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.RegisterDataDefineStruct<PoseSetStruct>(Definitions.PoseSet);

        // Body velocity kill + forward inject (do not mix into PoseSet).
        _sim.AddToDataDefinition(Definitions.VelocitySet, "VELOCITY BODY X", "meters per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.VelocitySet, "VELOCITY BODY Y", "meters per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.VelocitySet, "VELOCITY BODY Z", "meters per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.VelocitySet, "ROTATION VELOCITY BODY X", "radians per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.VelocitySet, "ROTATION VELOCITY BODY Y", "radians per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.VelocitySet, "ROTATION VELOCITY BODY Z", "radians per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.RegisterDataDefineStruct<VelocitySetStruct>(Definitions.VelocitySet);

        _defsRegistered = true;
    }

    private void EnsureEvents()
    {
        if (_sim is null || _eventsMapped) return;
        try
        {
            _sim.MapClientEventToSimEvent(Events.GearDown, "GEAR_DOWN");
            _sim.MapClientEventToSimEvent(Events.GearUp, "GEAR_UP");
            _sim.MapClientEventToSimEvent(Events.FlapsSet, "FLAPS_SET");
            _sim.MapClientEventToSimEvent(Events.PauseOff, "PAUSE_OFF");
            _sim.MapClientEventToSimEvent(Events.PauseOn, "PAUSE_ON");
            _sim.MapClientEventToSimEvent(Events.FreezeLatLon, "FREEZE_LATITUDE_LONGITUDE_SET");
            _sim.MapClientEventToSimEvent(Events.FreezeAlt, "FREEZE_ALTITUDE_SET");
            _sim.MapClientEventToSimEvent(Events.FreezeAtt, "FREEZE_ATTITUDE_SET");
            _sim.MapClientEventToSimEvent(Events.ClockHoursSet, "CLOCK_HOURS_SET");
            _sim.MapClientEventToSimEvent(Events.ClockMinutesSet, "CLOCK_MINUTES_SET");
            _sim.MapClientEventToSimEvent(Events.ZuluHoursSet, "ZULU_HOURS_SET");
            _sim.MapClientEventToSimEvent(Events.ZuluMinutesSet, "ZULU_MINUTES_SET");
            _sim.MapClientEventToSimEvent(Events.SpoilersOff, "SPOILERS_OFF");
            _sim.MapClientEventToSimEvent(Events.SpoilersSet, "SPOILERS_SET");
            _sim.MapClientEventToSimEvent(Events.ParkingBrakeSet, "PARKING_BRAKE_SET");
            _sim.MapClientEventToSimEvent(Events.ActivePauseOn, "ACTIVE_PAUSE_ON");
            _sim.MapClientEventToSimEvent(Events.ActivePauseOff, "ACTIVE_PAUSE_OFF");
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearDown, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearUp, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.FlapsSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.PauseOff, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.PauseOn, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.SpoilersOff, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.SpoilersSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.ParkingBrakeSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.ActivePauseOff, false);
            _sim.SetNotificationGroupPriority(Groups.Input, MsfsSc.SIMCONNECT_GROUP_PRIORITY_HIGHEST);
            _eventsMapped = true;
        }
        catch (Exception ex)
        {
            Log($"EnsureEvents: {ex.Message}");
        }
    }

    private void StartTelemetry()
    {
        if (_sim is null) return;
        _sim.RequestDataOnSimObject(
            Requests.Telemetry,
            Definitions.Telemetry,
            MsfsSc.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.VISUAL_FRAME,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
            0, 0, 0);
    }

    private void CleanupSim()
    {
        if (_sim is null) return;
        try
        {
            _sim.OnRecvOpen -= OnRecvOpen;
            _sim.OnRecvQuit -= OnRecvQuit;
            _sim.OnRecvException -= OnRecvException;
            _sim.OnRecvSimobjectData -= OnRecvSimobjectData;
            _sim.Dispose();
        }
        catch { /* ignore */ }
        _sim = null;
        _defsRegistered = false;
        _eventsMapped = false;
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
