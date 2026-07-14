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
    private SimConnectionState _state = SimConnectionState.Disconnected;
    private string? _statusMessage = "Not connected";

    private enum Definitions
    {
        Telemetry = 1,
        InitPosition = 2
    }

    private enum Requests
    {
        Telemetry = 1
    }

    private enum Events
    {
        GearDown = 1,
        GearUp = 2,
        FlapsSet = 3,
        PauseOff = 4,
        PauseOn = 5,
        FreezeLat = 6,
        FreezeLon = 7,
        FreezeAlt = 8,
        FreezeAtt = 9
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

        progress?.Report("Preparing scenario…");
        await Task.Delay(200, ct);

        var loadedFlight = false;
        if (!string.IsNullOrWhiteSpace(flightFileAbsolutePath) && File.Exists(flightFileAbsolutePath))
        {
            progress?.Report("Loading flight file…");
            try
            {
                var fullPath = Path.GetFullPath(flightFileAbsolutePath);
                Log($"FlightLoad path: {fullPath} (exists={File.Exists(fullPath)})");
                _sim.FlightLoad(fullPath);
                loadedFlight = true;
                Log($"FlightLoad requested: {fullPath}");
                // Real FLT loads can take a few seconds
                await Task.Delay(5000, ct);
            }
            catch (Exception ex)
            {
                Log($"FlightLoad failed: {ex.Message}. Falling back to teleport.");
            }
        }
        else
        {
            Log($"Flight file missing: '{flightFileAbsolutePath}' — will teleport only.");
        }

        progress?.Report("Applying weather…");
        ApplyWeather(challenge.Weather);
        await Task.Delay(500, ct);

        // Always apply spawn from challenge JSON (sourced from the same FLT SimVars for tests).
        // This is the reliable path when FlightLoad is ignored on the world map / wrong session state.
        progress?.Report(loadedFlight ? "Fine-tuning position…" : "Positioning aircraft (teleport)…");
        Teleport(challenge.Spawn);
        await Task.Delay(800, ct);

        progress?.Report("Configuring gear and flaps…");
        ConfigureAircraft(challenge.AircraftSetup);
        await Task.Delay(400, ct);

        progress?.Report("Challenge armed.");
        Log("Scenario load complete.");
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

            if (setup.Unpause)
            {
                _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.PauseOff, 0,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }
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

            var init = new SIMCONNECT_DATA_INITPOSITION
            {
                Latitude = spawn.Latitude,
                Longitude = spawn.Longitude,
                Altitude = spawn.AltitudeFeet,
                Pitch = spawn.PitchDeg,
                Bank = spawn.BankDeg,
                Heading = spawn.HeadingDeg,
                OnGround = 0,
                Airspeed = (uint)Math.Max(0, Math.Round(spawn.AirspeedKts))
            };

            _sim.SetDataOnSimObject(
                Definitions.InitPosition,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                init);

            Log($"Teleport lat={spawn.Latitude:F5} lon={spawn.Longitude:F5} alt={spawn.AltitudeFeet:F0} hdg={spawn.HeadingDeg:F0}");
        }
        catch (Exception ex)
        {
            Log($"Teleport: {ex.Message}");
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
        Log($"SimConnect exception: {data.dwException} (send {data.dwSendID}, index {data.dwIndex})");
    }

    private void OnRecvSimobjectData(Microsoft.FlightSimulator.SimConnect.SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
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

        _defsRegistered = true;
    }

    private void EnsureEvents()
    {
        if (_sim is null) return;
        try
        {
            _sim.MapClientEventToSimEvent(Events.GearDown, "GEAR_DOWN");
            _sim.MapClientEventToSimEvent(Events.GearUp, "GEAR_UP");
            _sim.MapClientEventToSimEvent(Events.FlapsSet, "FLAPS_SET");
            _sim.MapClientEventToSimEvent(Events.PauseOff, "PAUSE_OFF");
            _sim.MapClientEventToSimEvent(Events.PauseOn, "PAUSE_ON");
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearDown, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearUp, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.FlapsSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.PauseOff, false);
            _sim.SetNotificationGroupPriority(Groups.Input, MsfsSc.SIMCONNECT_GROUP_PRIORITY_HIGHEST);
        }
        catch
        {
            // may already be mapped
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
    }

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
