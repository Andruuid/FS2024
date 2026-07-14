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

    private enum Definitions
    {
        Telemetry = 1,
        InitPosition = 2,
        AircraftTitle = 3,
        PoseSet = 4
    }

    private enum Requests
    {
        Telemetry = 1,
        AircraftTitle = 2
    }

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
        ZuluMinutesSet = 13
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

    public async Task LoadScenarioAsync(
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

        // --- Safe apply: time + weather + teleport + gear (no FlightLoad) ---
        // Always unpause/unfreeze in finally so the plane is never left stuck.
        progress?.Report("Setting time of day…");
        try
        {
            PauseSim(true);
            await Task.Delay(150, ct);
            ApplyTimeOfDay(challenge.TimeOfDay);
            await Task.Delay(200, ct);

            progress?.Report("Applying weather…");
            ApplyWeather(challenge.Weather);
            await Task.Delay(250, ct);

            progress?.Report("Positioning aircraft…");
            await ApplySpawnAsync(challenge.Spawn, ct);

            progress?.Report("Configuring gear and flaps…");
            ConfigureAircraft(challenge.AircraftSetup);
            await Task.Delay(250, ct);
        }
        finally
        {
            // Critical: never leave freeze/pause stuck — that looks like "Start does nothing".
            FreezePose(false);
            if (challenge.AircraftSetup.Unpause)
                PauseSim(false);
        }

        progress?.Report("Challenge armed.");
        var tod = challenge.TimeOfDay ?? new TimeOfDayConfig();
        Log(
            $"Scenario load complete (safe path). Spawn IAS {challenge.Spawn.AirspeedKts:0} kt · " +
            $"alt {challenge.Spawn.AltitudeFeet:0} ft · hdg {challenge.Spawn.HeadingDeg:0}° · " +
            $"time {tod.Hour:00}:{tod.Minute:00} {(tod.UseZuluTime ? "Z" : "local")} · ac '{actualTitle ?? "?"}'.");
    }

    /// <summary>
    /// Apply spawn without relying on freeze (freeze can stick and make Start look like a no-op).
    /// InitPosition + direct lat/lon/alt/heading, repeated while paused.
    /// </summary>
    private async Task ApplySpawnAsync(SpawnConfig spawn, CancellationToken ct)
    {
        EnsureEvents();
        EnsureDefinitions();

        if (Math.Abs(spawn.Latitude) < 1e-8 && Math.Abs(spawn.Longitude) < 1e-8)
            Log("WARNING: spawn lat/lon are ~0 — check challenge JSON loaded correctly.");

        Log(
            $"ApplySpawn target lat={spawn.Latitude:F5} lon={spawn.Longitude:F5} " +
            $"alt={spawn.AltitudeFeet:F0} ft hdg={spawn.HeadingDeg:F1}° pitch={spawn.PitchDeg:F2} " +
            $"bank={spawn.BankDeg:F2} ias={spawn.AirspeedKts:F0}");

        PauseSim(true);
        await Task.Delay(100, ct);

        // Multiple applies — single InitPosition is often ignored for alt/heading in MSFS 2024.
        for (var i = 1; i <= 4; i++)
        {
            Teleport(spawn);
            SetPoseDirect(spawn);
            ReceiveMessage(); // keep SimConnect queue moving during load
            await Task.Delay(280, ct);
            Log($"ApplySpawn pulse {i}/4 done");
        }
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

            // FLAPS_SET uses 0–16383
            var flaps = (uint)Math.Clamp(setup.FlapsHandleIndex * (16383 / 4), 0, 16383);
            _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.FlapsSet, flaps,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

            // Unpause is applied by LoadScenarioAsync after spawn airspeed is set.
        }
        catch (Exception ex)
        {
            Log($"ConfigureAircraft: {ex.Message}");
        }
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

        _sim.RegisterDataDefineStruct<TelemetryStruct>(Definitions.Telemetry);

        _sim.AddToDataDefinition(Definitions.InitPosition, "Initial Position", null,
            SIMCONNECT_DATATYPE.INITPOSITION, 0, MsfsSc.SIMCONNECT_UNUSED);

        _sim.AddToDataDefinition(Definitions.AircraftTitle, "TITLE", null,
            SIMCONNECT_DATATYPE.STRING256, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.RegisterDataDefineStruct<AircraftTitleStruct>(Definitions.AircraftTitle);

        // Direct pose write — lat/lon/alt/attitude/IAS (backup when InitPosition is flaky).
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
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearDown, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearUp, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.FlapsSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.PauseOff, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.PauseOn, false);
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
