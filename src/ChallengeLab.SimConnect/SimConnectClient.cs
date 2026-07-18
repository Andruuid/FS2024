using System.Runtime.InteropServices;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;
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
    private string? _cachedAircraftTitle;
    private TaskCompletionSource<TelemetryStruct>? _spawnVerifyTcs;
    private TaskCompletionSource<IReadOnlyList<AirportFacility>>? _airportCatalogTcs;
    private readonly SortedDictionary<uint, List<AirportFacility>> _airportCatalogPackets = new();
    private IReadOnlyList<AirportFacility>? _airportCatalogCache;
    private readonly Dictionary<string, AirportRunwayFacility> _airportDetailCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, FacilityRequestContext> _facilityRequests = new();
    private uint _nextFacilityRequestId = 1000;
    private readonly object _pauseStateLock = new();
    private uint _pauseStateFlags;
    private long _pauseGeneration;
    private bool _pauseStateKnown;
    private bool _pauseWasActive;
    private readonly object _contactPointLock = new();
    private bool _contactPointTelemetryEnabled;
    private double _latestContactPointSimulationTime = double.NaN;
    private bool _latestContactPointAvailable;
    private ContactPointTelemetryStruct _latestContactPointData;

    private enum Definitions
    {
        Telemetry = 1,
        InitPosition = 2,
        AircraftTitle = 3,
        PoseSet = 4,
        VelocitySet = 5,
        ContactPoints = 6,
        AirportFacility = 100
    }

    private enum Requests
    {
        Telemetry = 1,
        AircraftTitle = 2,
        SpawnVerify = 3,
        ContactPoints = 4,
        /// <summary>Low-rate continuous TITLE stream so Free Flight always knows the live type.</summary>
        AircraftTitleStream = 5,
        Airports = 100
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
        ActivePauseOff = 18,
        ZuluDaySet = 19,
        ZuluYearSet = 20,
        SpoilersArmSet = 21,
        SpoilersOn = 22,
        GearSet = 23,
        PauseStateEx1 = 24
    }

    private enum Groups
    {
        Input = 1
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct TelemetryStruct
    {
        public double SimulationTime;
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public double Agl;
        public double Heading;
        public double Pitch;
        public double Bank;
        public double Airspeed;
        public double GroundVelocity;
        public double VerticalSpeed;
        public double TouchdownNormalVelocity;
        public double GForce;
        public double SimOnGround;
        public double GearOnGround0;
        public double GearOnGround1;
        public double GearOnGround2;
        public double GearOnGround3;
        public double GearOnGround4;
        public double GearOnGround5;
        public double GearOnGround6;
        public double GearOnGround7;
        public double GearOnGround8;
        public double GearOnGround9;
        public double GearOnGround10;
        public double GearOnGround11;
        public double GearOnGround12;
        public double GearOnGround13;
        public double GearOnGround14;
        public double GearOnGround15;
        public double GearHandle;
        public double IsGearRetractable;
        public double IsGearWheels;
        public double IsGearFloats;
        public double IsTailDragger;
        public double FlapsIndex;
        public double FlapsHandlePositionCount;
        public double SpoilersAvailable;
        public double AutopilotAvailable;
        public double ThrottleLowerLimit;
        public double WindDir;
        public double WindVel;
        public double RadioHeight;
        public double DesignSpeedVs0;
        public double StallWarning;
        public double TotalWeight;
        public double SpoilersHandle;
        public double SpoilersLeft;
        public double SpoilersRight;
        public double ParkingBrake;
        public double BrakeLeft;
        public double BrakeRight;
        public double AutoBrakesActive;
        public double SimulationRate;
        public double CameraState;
        public double AutopilotHeadingLock;
        public double AutopilotAltitudeLock;
        public double AutopilotMaster;
        public double AutopilotThrottleArm;
        public double AutopilotManagedThrottleActive;
        public double IniAp1On;
        public double IniAp2On;
        public double IniAthrLight;
        public double IniAthrModeActive;
        public double IniAutothrottleArmed;
        public double IniBrakePedalLeft;
        public double IniBrakePedalRight;
        public double EngineCount;
        public double EngineCombustion1;
        public double EngineCombustion2;
        public double EngineCombustion3;
        public double EngineCombustion4;
        public double ReverseThrustEngaged1;
        public double ReverseThrustEngaged2;
        public double ReverseThrustEngaged3;
        public double ReverseThrustEngaged4;
        public double ReverseNozzle1;
        public double ReverseNozzle2;
        public double ReverseNozzle3;
        public double ReverseNozzle4;
        public double ThrottleLever1;
        public double ThrottleLever2;
        public double ThrottleLever3;
        public double ThrottleLever4;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct ContactPointTelemetryStruct
    {
        public double SimulationTime;
        public double ContactPointOnGround0;
        public double ContactPointOnGround1;
        public double ContactPointOnGround2;
        public double ContactPointOnGround3;
        public double ContactPointOnGround4;
        public double ContactPointOnGround5;
        public double ContactPointOnGround6;
        public double ContactPointOnGround7;
        public double ContactPointOnGround8;
        public double ContactPointOnGround9;
        public double ContactPointOnGround10;
        public double ContactPointOnGround11;
        public double ContactPointOnGround12;
        public double ContactPointOnGround13;
        public double ContactPointOnGround14;
        public double ContactPointOnGround15;
        public double ContactPointCompression0;
        public double ContactPointCompression1;
        public double ContactPointCompression2;
        public double ContactPointCompression3;
        public double ContactPointCompression4;
        public double ContactPointCompression5;
        public double ContactPointCompression6;
        public double ContactPointCompression7;
        public double ContactPointCompression8;
        public double ContactPointCompression9;
        public double ContactPointCompression10;
        public double ContactPointCompression11;
        public double ContactPointCompression12;
        public double ContactPointCompression13;
        public double ContactPointCompression14;
        public double ContactPointCompression15;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AircraftTitleStruct
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Title;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RunwayFacilityStruct
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public float Heading;
        public float Length;
        public float Width;
        public int Surface;
        public int PrimaryNumber;
        public int PrimaryDesignator;
        public int SecondaryNumber;
        public int SecondaryDesignator;
        public byte PrimaryClosed;
        public byte SecondaryClosed;
        public byte PrimaryLanding;
        public byte SecondaryLanding;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct AirportDetailFacilityStruct
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Country;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct PavementFacilityStruct
    {
        public float Length;
        public float Width;
        public int Enable;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct RunwayStartFacilityStruct
    {
        public double Latitude;
        public double Longitude;
        public double Altitude;
        public float Heading;
        public int Number;
        public int Designator;
        public int Type;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct VasiFacilityStruct
    {
        public int Type;
        public float BiasX;
        public float BiasZ;
        public float Spacing;
        public float Angle;
    }

    private sealed class FacilityRequestContext
    {
        public required AirportFacility Airport { get; init; }
        public string Country { get; set; } = "";
        public TaskCompletionSource<AirportRunwayFacility> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<RunwayFacilityDraft> Runways { get; } = new();
        public List<RunwayStartFacility> Starts { get; } = new();
        /// <summary>Maps facility UniqueRequestId of a RUNWAY packet to its draft index.</summary>
        public Dictionary<uint, int> RunwayIndexByUniqueId { get; } = new();
    }

    /// <summary>Mutable runway draft so VASI child packets can attach angles.</summary>
    private sealed class RunwayFacilityDraft
    {
        public required double CenterLatitude { get; init; }
        public required double CenterLongitude { get; init; }
        public required double AltitudeMeters { get; init; }
        public required double HeadingTrueDeg { get; init; }
        public required double LengthMeters { get; init; }
        public required double WidthMeters { get; init; }
        public required int Surface { get; init; }
        public required int PrimaryNumber { get; init; }
        public required int PrimaryDesignator { get; init; }
        public required int SecondaryNumber { get; init; }
        public required int SecondaryDesignator { get; init; }
        public required bool PrimaryClosed { get; init; }
        public required bool SecondaryClosed { get; init; }
        public required bool PrimaryLandingAllowed { get; init; }
        public required bool SecondaryLandingAllowed { get; init; }
        /// <summary>Order: primary L, primary R, secondary L, secondary R.</summary>
        public double?[] VasiAnglesDeg { get; } = new double?[4];
        /// <summary>Order: primary L, primary R, secondary L, secondary R.</summary>
        public RunwayVisualSlopeFacility?[] VisualSlopeSystems { get; } =
            new RunwayVisualSlopeFacility?[4];
        public int VasiCount { get; set; }
        public RunwayPavementFacility? PrimaryThreshold { get; set; }
        public RunwayPavementFacility? SecondaryThreshold { get; set; }
        public int ThresholdCount { get; set; }

        public RunwayFacility ToFacility() => new(
            CenterLatitude,
            CenterLongitude,
            AltitudeMeters,
            HeadingTrueDeg,
            LengthMeters,
            WidthMeters,
            Surface,
            PrimaryNumber,
            PrimaryDesignator,
            SecondaryNumber,
            SecondaryDesignator,
            PrimaryClosed,
            SecondaryClosed,
            PrimaryLandingAllowed,
            SecondaryLandingAllowed,
            VasiAnglesDeg.ToList(),
            PrimaryThreshold,
            SecondaryThreshold,
            VisualSlopeSystems.ToList());
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
            _sim.OnRecvEvent += OnRecvEvent;
            _sim.OnRecvAirportList += OnRecvAirportList;
            _sim.OnRecvFacilityData += OnRecvFacilityData;
            _sim.OnRecvFacilityDataEnd += OnRecvFacilityDataEnd;

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

    public async Task<IReadOnlyList<AirportFacility>> GetAirportsAsync(CancellationToken ct = default)
    {
        if (_airportCatalogCache is not null)
            return _airportCatalogCache;
        if (!IsConnected || _sim is null)
            throw new InvalidOperationException("Not connected to the simulator.");

        if (_airportCatalogTcs is null)
        {
            _airportCatalogPackets.Clear();
            _airportCatalogTcs = new TaskCompletionSource<IReadOnlyList<AirportFacility>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Log("Free mode: requesting worldwide airport catalog from SimConnect...");
            _sim.RequestAllFacilities(SIMCONNECT_FACILITY_LIST_TYPE.AIRPORT, Requests.Airports);
        }

        try
        {
            return await _airportCatalogTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException)
        {
            _airportCatalogTcs = null;
            _airportCatalogPackets.Clear();
            throw new TimeoutException("Timed out while loading the SimConnect airport catalog.");
        }
    }

    public async Task<AirportRunwayFacility> GetAirportRunwaysAsync(
        AirportFacility airport,
        CancellationToken ct = default)
    {
        if (!IsConnected || _sim is null)
            throw new InvalidOperationException("Not connected to the simulator.");

        var cacheKey = FacilityCacheKey(airport);
        if (_airportDetailCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var existing = _facilityRequests.Values.FirstOrDefault(x =>
            FacilityCacheKey(x.Airport).Equals(cacheKey, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
            return await existing.Completion.Task.WaitAsync(TimeSpan.FromSeconds(12), ct);

        var requestId = _nextFacilityRequestId++;
        var context = new FacilityRequestContext { Airport = airport };
        _facilityRequests[requestId] = context;
        try
        {
            _sim.RequestFacilityData(
                Definitions.AirportFacility,
                (Requests)requestId,
                airport.Icao,
                airport.Region);
            return await context.Completion.Task.WaitAsync(TimeSpan.FromSeconds(12), ct);
        }
        catch
        {
            _facilityRequests.Remove(requestId);
            throw;
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
        // 1) Flying — sim running.
        // 2) ESC / menu pause — Escape → Resume (what pilots mean by "paused").
        // 3) Active pause — separate mode; clear if stuck, never use as our end state.
        // 4) SET PAUSE ON/OFF — SimConnect full pause; our controlled "wait then resume".
        //
        // CRITICAL stability rule (ground→air Restart):
        // Never release SET PAUSE or FREEZE mid-load. Releasing pause for "config settle"
        // while freeze is imperfect causes the classic: flash airborne → fall back to runway.
        // Yellow "Ready to Fly" is World-Map/FlightLoad UI only; mid free-flight FlightLoad
        // of CustomFlight/aircraft swap CTDs MSFS 2024 — we approximate with SET PAUSE hold.
        SpawnApplyResult spawnResult = SpawnApplyResult.Fail("Spawn did not complete.");
        try
        {
            // Identical entry whether currently flying or already ESC-/SET-paused.
            await NormalizeLoadEntryAsync(ct);

            progress?.Report("Applying weather…");
            ApplyWeather(challenge.Weather);
            await Task.Delay(250, ct);

            progress?.Report("Positioning aircraft…");
            spawnResult = await ApplySpawnAsync(challenge.Spawn, ct);

            // CRITICAL: apply clock AFTER teleport. CLOCK_*_SET is local to the aircraft
            // position's timezone. Setting local time before SLLP spawn (e.g. still at
            // Barcelona) locks Zulu to Europe, then after jump La Paz local is ~4–6 h earlier
            // → night on a morning challenge.
            if (spawnResult.Success)
            {
                progress?.Report("Setting time of day…");
                ApplyTimeOfDay(challenge.TimeOfDay);
                await Task.Delay(200, ct);

                await ConfigureAndSettleAsync(challenge.AircraftSetup, challenge.Spawn, progress, ct);

                // Re-pin spawn after config so a ground restart cannot leave residual sink.
                progress?.Report("Stabilizing spawn…");
                await RePinSpawnAsync(challenge.Spawn, ct);

                // Re-assert clock after config/settle (some aircraft loads reset the clock).
                ApplyTimeOfDay(challenge.TimeOfDay);

                spawnResult = await VerifySpawnAsync(challenge.Spawn, ct);
                if (!spawnResult.Success)
                    Log($"Post-config re-verify: {spawnResult.Message}");
            }
            else
            {
                progress?.Report("Positioning failed…");
                Log($"Spawn apply failed: {spawnResult.Message}");
            }
        }
        finally
        {
            // Hold SET PAUSE first, re-assert pose once more, only then release FREEZE.
            // Order matters: unfreeze-before-pause = freefall / snap-to-ground.
            if (challenge.AircraftSetup.Unpause)
            {
                EnsureActivePauseOff();
                // Keep freeze until after a final pose write, then unpause.
                try
                {
                    if (challenge.Spawn is not null)
                    {
                        Teleport(challenge.Spawn);
                        SetPoseDirect(challenge.Spawn);
                        SetVelocityForSpawn(challenge.Spawn);
                    }
                }
                catch { /* best effort */ }

                FreezePose(false);
                PauseSim(false);
            }
            else
            {
                ForceSetPauseOn();
                try
                {
                    if (challenge.Spawn is not null)
                    {
                        Teleport(challenge.Spawn);
                        SetPoseDirect(challenge.Spawn);
                        SetVelocityForSpawn(challenge.Spawn);
                    }
                }
                catch { /* best effort */ }

                ForceSetPauseOn();
                // Release FREEZE only while SET PAUSE is on — aircraft stays put until resume.
                FreezePose(false);
                ForceSetPauseOn();
            }
        }

        if (spawnResult.Success)
        {
            progress?.Report(
                challenge.AircraftSetup.Unpause
                    ? "Challenge armed."
                    : "Ready — PAUSED in air. Resume when ready to fly.");
            var tod = challenge.TimeOfDay ?? new TimeOfDayConfig();
            Log(
                $"Scenario load complete (safe path). Spawn IAS {challenge.Spawn.AirspeedKts:0} kt · " +
                $"alt {challenge.Spawn.AltitudeFeet:0} ft · hdg {challenge.Spawn.HeadingDeg:0}° · " +
                $"time {tod.Hour:00}:{tod.Minute:00} {(tod.UseZuluTime ? "Z" : "local")} · ac '{actualTitle ?? "?"}' · " +
                $"verify horiz={spawnResult.HorizontalErrorM:0} m altErr={spawnResult.AltErrorFeet:0} ft ias={spawnResult.AirspeedKts:0} · " +
                $"setPause={!challenge.AircraftSetup.Unpause} onGround={spawnResult.ReportedOnGround}.");
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

            // Optional calendar (affects sun season). Day/year are Zulu-calendar events in MSFS.
            if (tod.DayOfYear is >= 1 and <= 366)
            {
                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.ZuluDaySet, (uint)tod.DayOfYear.Value,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }

            if (tod.Year is > 1900 and < 2200)
            {
                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.ZuluYearSet, (uint)tod.Year.Value,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }

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
                // Local clock relative to the *current* aircraft position timezone.
                // Call only after spawn teleport to the challenge airport.
                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.ClockHoursSet, hour,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.ClockMinutesSet, minute,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }

            var dayPart = tod.DayOfYear is >= 1 and <= 366 ? $" day={tod.DayOfYear}" : "";
            var yearPart = tod.Year is > 1900 and < 2200 ? $" year={tod.Year}" : "";
            Log(
                $"Time of day set: {hour:00}:{minute:00} {(tod.UseZuluTime ? "Zulu" : "local")}" +
                $"{dayPart}{yearPart}");
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

    /// <inheritdoc />
    public void ResumeFlight()
    {
        if (_sim is null)
        {
            Log("ResumeFlight: not connected.");
            return;
        }

        try
        {
            EnsureEvents();
            // Ensure FREEZE is off so resume is real flight, not a frozen hover.
            FreezePose(false);
            EnsureActivePauseOff();
            PauseSim(false);
            PauseSim(false); // second pulse — some hosts drop the first PAUSE_OFF
            Log("ResumeFlight: FREEZE off · SET PAUSE OFF (Go).");
        }
        catch (Exception ex)
        {
            Log($"ResumeFlight: {ex.Message}");
        }
    }

    public void ConfigureAircraft(AircraftSetupConfig setup)
    {
        if (_sim is null) return;

        try
        {
            EnsureEvents();
            CommandGear(setup.GearDown);

            // FLAPS_SET uses 0–16383 (index 0 = clean; ~4096 per step for 0–4).
            var flapsIndex = Math.Clamp(setup.FlapsHandleIndex, 0, 5);
            var flaps = (uint)Math.Clamp(flapsIndex * (16383 / 4), 0, 16383);
            _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.FlapsSet, flaps,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

            if (setup.SpoilersRetracted)
                CommandSpoilersFullyRetracted();

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
    /// Gear handle: GEAR_SET (0 up / 1 down) plus discrete UP/DOWN pulses.
    /// A330 often ignores a single GEAR_UP after a prior landing while SET-paused.
    /// </summary>
    private void CommandGear(bool gearDown)
    {
        if (_sim is null) return;
        EnsureEvents();
        // GEAR_SET: 0 = up, 1 = down (boolean handle).
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.GearSet, gearDown ? 1u : 0u,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER,
            gearDown ? Events.GearDown : Events.GearUp, 0,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        // Second discrete pulse — some hosts drop the first under pause.
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER,
            gearDown ? Events.GearDown : Events.GearUp, 0,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    /// <summary>
    /// Force speedbrake lever + panels to full extend (16K). Used only for desync resync.
    /// </summary>
    private void CommandSpoilersFullyExtended()
    {
        if (_sim is null) return;
        EnsureEvents();
        // SPOILERS_SET is 0–16383 (full = 16383), same family as flaps handle.
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersSet, 16383u,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersOn, 0,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    /// <summary>
    /// Force retract / disarm (keyboard "Speedbrakes Retract" equivalent + set 0).
    /// </summary>
    private void CommandSpoilersFullyRetracted()
    {
        if (_sim is null) return;
        EnsureEvents();
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersArmSet, 0,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersOff, 0,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersSet, 0,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        // Second pulse — some airframes only honor lever after OFF.
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersOff, 0,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        _sim.TransmitClientEvent(
            MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersSet, 0,
            Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
    }

    /// <summary>
    /// Cycle full extend → full retract to clear lever/surface desync (common after
    /// prior full spoilers then retract; pilot manual fix: nudge out then full in).
    /// Only when challenge wants spoilers retracted at start.
    /// </summary>
    private async Task ResyncSpoilersCycleAsync(CancellationToken ct)
    {
        if (_sim is null) return;

        Log("Spoiler resync: FULL EXTEND → FULL RETRACT (clear lever/surface desync)");
        CommandSpoilersFullyExtended();
        await Task.Delay(400, ct);

        CommandSpoilersFullyRetracted();
        await Task.Delay(300, ct);

        // One more retract burst — matches "press Speedbrakes Retract a few times".
        CommandSpoilersFullyRetracted();
        await Task.Delay(200, ct);
        Log("Spoiler resync: retract cycle done");
    }

    /// <summary>
    /// Apply gear/flaps/spoilers/parking brake while remaining SET-paused + FREEZE pinned.
    /// Do NOT release pause here — that caused airborne→runway snaps after ground Restart.
    /// Surface motion may complete after the pilot resumes; we still command handles now.
    /// </summary>
    private async Task ConfigureAndSettleAsync(
        AircraftSetupConfig setup,
        SpawnConfig spawn,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Configuring gear, flaps, spoilers…");
        var started = DateTimeOffset.UtcNow;

        EnsureActivePauseOff();
        ForceSetPauseOn();
        FreezePose(true);
        await Task.Delay(120, ct);
        Log("Config settle: SET PAUSE ON + FREEZE held (no mid-config unpause).");

        // Clear spoiler lever↔surface desync before normal config (A330: stowed look but
        // sim thinks handle is out until you cycle full extend → retract).
        if (setup.SpoilersRetracted)
        {
            progress?.Report("Resyncing speedbrakes (extend → retract)…");
            Teleport(spawn);
            SetPoseDirect(spawn);
            SetVelocityForSpawn(spawn);
            ForceSetPauseOn();
            FreezePose(true);
            await ResyncSpoilersCycleAsync(ct);
            ForceSetPauseOn();
            FreezePose(true);
        }

        for (var i = 1; i <= ConfigPulseCount; i++)
        {
            // Re-pin between pulses so residual ground physics cannot drag us down.
            Teleport(spawn);
            SetPoseDirect(spawn);
            SetVelocityForSpawn(spawn);
            ConfigureAircraft(setup);
            ForceSetPauseOn();
            FreezePose(true);
            Log($"Config pulse {i}/{ConfigPulseCount} " +
                $"(gear={(setup.GearDown ? "down" : "up")} flaps={setup.FlapsHandleIndex} " +
                $"spoilersIn={setup.SpoilersRetracted} parkBrake={setup.ParkingBrakeOn})");
            await Task.Delay(ConfigPulseDelayMs, ct);
        }

        progress?.Report("Waiting for aircraft to settle (min 5s)…");

        var matched = false;
        string? lastDetail = null;
        var poll = 0;
        while ((DateTimeOffset.UtcNow - started).TotalMilliseconds < MaxConfigSettleMs)
        {
            poll++;
            ForceSetPauseOn();
            FreezePose(true);
            // Keep pose/velocity alive while handles settle (prevents runway snap).
            if (poll % 2 == 0)
            {
                SetPoseDirect(spawn);
                SetVelocityForSpawn(spawn);
            }

            var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            var sample = await RequestSpawnSnapshotAsync(ct);
            if (sample is not null)
            {
                matched = ConfigMatches(setup, sample.Value, out lastDetail);
                var altErr = Math.Abs(sample.Value.Altitude - spawn.AltitudeFeet);
                if (sample.Value.SimOnGround > 0.5 || altErr > MaxAltitudeErrorFeet)
                {
                    Log(
                        $"Config settle drift: onGround={sample.Value.SimOnGround > 0.5} " +
                        $"altErr={altErr:0} — re-pinning");
                    Teleport(spawn);
                    SetPoseDirect(spawn);
                    SetVelocityForSpawn(spawn);
                    FreezePose(true);
                }

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

        ConfigureAircraft(setup);
        ForceSetPauseOn();
        FreezePose(true);

        if (matched)
        {
            progress?.Report("Config ready — holding PAUSED in air…");
            Log($"Config verify OK: {lastDetail}");
        }
        else
        {
            // Soft-fail: do not block restart forever (aircraft-specific surface quirks).
            progress?.Report("Config timeout — holding PAUSED in air. Check surfaces after resume.");
            Log($"Config verify soft-fail after settle: {lastDetail ?? "no telemetry"} " +
                $"(gear want={(setup.GearDown ? "down" : "up")} flaps={setup.FlapsHandleIndex} " +
                $"spoilersIn={setup.SpoilersRetracted} parkBrake={setup.ParkingBrakeOn})");
        }
    }

    /// <summary>Final airborne re-pin under freeze+pause before we release freeze.</summary>
    private async Task RePinSpawnAsync(SpawnConfig spawn, CancellationToken ct)
    {
        ForceSetPauseOn();
        FreezePose(true);
        await PulseSpawnAsync(spawn, pulses: 3, ct);
        await Task.Delay(200, ct);
        ForceSetPauseOn();
        FreezePose(true);
    }

    private static bool ConfigMatches(
        AircraftSetupConfig setup,
        TelemetryStruct t,
        out string detail)
    {
        var gearDown = t.GearHandle > 0.5;
        var flaps = (int)Math.Round(t.FlapsIndex);
        // Wing surface deflection is ground truth (handle alone misreads Airbus "armed").
        var surface01 = Math.Max(
            SpawnReadiness.NormalizeSpoiler01(t.SpoilersLeft),
            SpawnReadiness.NormalizeSpoiler01(t.SpoilersRight));
        var spoilersOut = surface01 > 0.15;
        var parkOn = t.ParkingBrake > 0.5;

        var gearOk = gearDown == setup.GearDown;
        var flapsOk = flaps == Math.Clamp(setup.FlapsHandleIndex, 0, 5);
        var spoilersOk = !setup.SpoilersRetracted || !spoilersOut;
        var brakeOk = setup.ParkingBrakeOn == parkOn;

        detail =
            $"gear={(gearDown ? "down" : "up")}({(gearOk ? "ok" : "want " + (setup.GearDown ? "down" : "up"))}) " +
            $"flaps={flaps}({(flapsOk ? "ok" : "want " + setup.FlapsHandleIndex)}) " +
            $"spoilers=surf{surface01:0%}({(spoilersOk ? "ok" : "want in")}) " +
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
        bool contactPointsEnabled;
        lock (_contactPointLock)
            contactPointsEnabled = _contactPointTelemetryEnabled;
        if (contactPointsEnabled)
            SetNoseGearImpactTelemetryEnabled(true);
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

    private void OnRecvEvent(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_EVENT data)
    {
        if ((Events)data.uEventID != Events.PauseStateEx1)
            return;

        lock (_pauseStateLock)
        {
            var flags = data.dwData;
            var active = flags != 0;
            if (active && !_pauseWasActive)
                _pauseGeneration++;
            _pauseStateFlags = flags;
            _pauseWasActive = active;
            _pauseStateKnown = true;
        }
    }

    private void OnRecvAirportList(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_AIRPORT_LIST data)
    {
        if (data.dwRequestID != (uint)Requests.Airports || _airportCatalogTcs is null)
            return;

        try
        {
            var packet = new List<AirportFacility>((int)data.dwArraySize);
            foreach (var item in data.rgData)
            {
                if (item is not SIMCONNECT_DATA_FACILITY_AIRPORT airport
                    || string.IsNullOrWhiteSpace(airport.Ident)
                    || !double.IsFinite(airport.Latitude)
                    || !double.IsFinite(airport.Longitude))
                    continue;

                packet.Add(new AirportFacility(
                    airport.Ident.Trim(),
                    airport.Region?.Trim() ?? "",
                    airport.Latitude,
                    airport.Longitude,
                    airport.Altitude));
            }

            _airportCatalogPackets[data.dwEntryNumber] = packet;
            var expectedPackets = Math.Max(1u, data.dwOutOf);
            if (_airportCatalogPackets.Count < expectedPackets)
                return;

            var catalog = _airportCatalogPackets
                .OrderBy(x => x.Key)
                .SelectMany(x => x.Value)
                .DistinctBy(a => $"{a.Icao}|{a.Region}|{a.Latitude:F6}|{a.Longitude:F6}",
                    StringComparer.OrdinalIgnoreCase)
                .ToList();
            _airportCatalogCache = catalog;
            _airportCatalogPackets.Clear();
            var completion = _airportCatalogTcs;
            _airportCatalogTcs = null;
            completion.TrySetResult(catalog);
            Log($"Free mode: airport catalog ready ({catalog.Count:N0} airports).");
        }
        catch (Exception ex)
        {
            var completion = _airportCatalogTcs;
            _airportCatalogTcs = null;
            _airportCatalogPackets.Clear();
            completion?.TrySetException(ex);
            Log($"Airport catalog parse failed: {ex.Message}");
        }
    }

    private void OnRecvFacilityData(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_FACILITY_DATA data)
    {
        if (!_facilityRequests.TryGetValue(data.UserRequestId, out var context)
            || data.Data.Length == 0)
            return;

        try
        {
            switch ((SIMCONNECT_FACILITY_DATA_TYPE)data.Type)
            {
                case SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT
                    when data.Data[0] is AirportDetailFacilityStruct airport:
                    context.Country = airport.Country?.Trim() ?? "";
                    break;

                case SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY
                    when data.Data[0] is RunwayFacilityStruct runway:
                {
                    var draft = new RunwayFacilityDraft
                    {
                        CenterLatitude = runway.Latitude,
                        CenterLongitude = runway.Longitude,
                        AltitudeMeters = runway.Altitude,
                        HeadingTrueDeg = runway.Heading,
                        LengthMeters = runway.Length,
                        WidthMeters = runway.Width,
                        Surface = runway.Surface,
                        PrimaryNumber = runway.PrimaryNumber,
                        PrimaryDesignator = runway.PrimaryDesignator,
                        SecondaryNumber = runway.SecondaryNumber,
                        SecondaryDesignator = runway.SecondaryDesignator,
                        PrimaryClosed = runway.PrimaryClosed != 0,
                        SecondaryClosed = runway.SecondaryClosed != 0,
                        PrimaryLandingAllowed = runway.PrimaryLanding != 0,
                        SecondaryLandingAllowed = runway.SecondaryLanding != 0
                    };
                    var index = context.Runways.Count;
                    context.Runways.Add(draft);
                    context.RunwayIndexByUniqueId[data.UniqueRequestId] = index;
                    break;
                }

                case SIMCONNECT_FACILITY_DATA_TYPE.PAVEMENT
                    when data.Data[0] is PavementFacilityStruct pavement:
                {
                    if (!context.RunwayIndexByUniqueId.TryGetValue(data.ParentUniqueRequestId, out var runwayIndex)
                        || runwayIndex < 0
                        || runwayIndex >= context.Runways.Count)
                        break;

                    var draft = context.Runways[runwayIndex];
                    var threshold = new RunwayPavementFacility(
                        double.IsFinite(pavement.Length) ? Math.Max(0, pavement.Length) : 0,
                        double.IsFinite(pavement.Width) ? Math.Max(0, pavement.Width) : 0,
                        pavement.Enable != 0);
                    if (draft.ThresholdCount == 0)
                        draft.PrimaryThreshold = threshold;
                    else if (draft.ThresholdCount == 1)
                        draft.SecondaryThreshold = threshold;
                    draft.ThresholdCount++;
                    break;
                }

                case SIMCONNECT_FACILITY_DATA_TYPE.VASI
                    when data.Data[0] is VasiFacilityStruct vasi:
                {
                    if (!context.RunwayIndexByUniqueId.TryGetValue(data.ParentUniqueRequestId, out var runwayIndex)
                        || runwayIndex < 0
                        || runwayIndex >= context.Runways.Count)
                        break;

                    var draft = context.Runways[runwayIndex];
                    // Definition order: PRIMARY_LEFT, PRIMARY_RIGHT, SECONDARY_LEFT, SECONDARY_RIGHT.
                    var slot = draft.VasiCount;
                    if (slot is >= 0 and < 4)
                    {
                        // TYPE 0 = NONE in the SDK; ignore empty / nonsense angles.
                        if (vasi.Type != 0 && double.IsFinite(vasi.Angle) && vasi.Angle is >= 1.5f and <= 10f)
                            draft.VasiAnglesDeg[slot] = vasi.Angle;
                        if (vasi.Type != 0
                            && double.IsFinite(vasi.BiasX)
                            && double.IsFinite(vasi.BiasZ)
                            && double.IsFinite(vasi.Spacing)
                            && double.IsFinite(vasi.Angle))
                        {
                            draft.VisualSlopeSystems[slot] = new RunwayVisualSlopeFacility(
                                vasi.Type,
                                vasi.BiasX,
                                vasi.BiasZ,
                                vasi.Spacing,
                                vasi.Angle);
                        }
                        draft.VasiCount = slot + 1;
                    }

                    break;
                }

                case SIMCONNECT_FACILITY_DATA_TYPE.START
                    when data.Data[0] is RunwayStartFacilityStruct start:
                    context.Starts.Add(new RunwayStartFacility(
                        start.Latitude,
                        start.Longitude,
                        start.Altitude,
                        start.Heading,
                        start.Number,
                        start.Designator,
                        start.Type));
                    break;
            }
        }
        catch (Exception ex)
        {
            context.Completion.TrySetException(ex);
            _facilityRequests.Remove(data.UserRequestId);
            Log($"Facility data parse failed for {context.Airport.Icao}: {ex.Message}");
        }
    }

    private void OnRecvFacilityDataEnd(
        Microsoft.FlightSimulator.SimConnect.SimConnect sender,
        SIMCONNECT_RECV_FACILITY_DATA_END data)
    {
        if (!_facilityRequests.Remove(data.RequestId, out var context))
            return;

        var runways = context.Runways.Select(r => r.ToFacility()).ToList();
        var airport = string.IsNullOrWhiteSpace(context.Country)
            ? context.Airport
            : context.Airport with { Country = context.Country };
        var detail = new AirportRunwayFacility(
            airport,
            runways,
            context.Starts.ToList());
        _airportDetailCache[FacilityCacheKey(context.Airport)] = detail;
        context.Completion.TrySetResult(detail);
        var vasiHits = runways.Count(r =>
            r.VasiAnglesDeg is not null && r.VasiAnglesDeg.Any(a => a is > 0));
        Log($"Free mode: {context.Airport.Icao} facility data — " +
            $"{detail.Runways.Count} runways, {detail.Starts.Count} starts" +
            (vasiHits > 0 ? $", VASI angles on {vasiHits} runway(s)." : "."));
    }

    private void OnRecvSimobjectData(Microsoft.FlightSimulator.SimConnect.SimConnect sender, SIMCONNECT_RECV_SIMOBJECT_DATA data)
    {
        if (data.dwRequestID is (uint)Requests.AircraftTitle or (uint)Requests.AircraftTitleStream)
        {
            try
            {
                var title = data.dwData[0] is AircraftTitleStruct s
                    ? s.Title
                    : data.dwData[0]?.ToString();
                var normalized = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
                if (!string.Equals(_cachedAircraftTitle, normalized, StringComparison.Ordinal))
                {
                    _cachedAircraftTitle = normalized;
                    if (normalized is not null)
                        Log($"Aircraft TITLE: '{normalized}'");
                }

                if (data.dwRequestID == (uint)Requests.AircraftTitle)
                    _titleTcs?.TrySetResult(normalized);
            }
            catch (Exception ex)
            {
                Log($"Title parse: {ex.Message}");
                if (data.dwRequestID == (uint)Requests.AircraftTitle)
                    _titleTcs?.TrySetResult(null);
            }
            finally
            {
                if (data.dwRequestID == (uint)Requests.AircraftTitle)
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

        if (data.dwRequestID == (uint)Requests.ContactPoints)
        {
            try
            {
                var contact = (ContactPointTelemetryStruct)data.dwData[0];
                lock (_contactPointLock)
                {
                    if (_contactPointTelemetryEnabled)
                    {
                        _latestContactPointSimulationTime = contact.SimulationTime;
                        _latestContactPointData = contact;
                        _latestContactPointAvailable = true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Contact-point telemetry parse: {ex.Message}");
            }
            return;
        }

        if (data.dwRequestID != (uint)Requests.Telemetry) return;

        try
        {
            var t = (TelemetryStruct)data.dwData[0];
            bool pauseStateAvailable;
            bool normalPauseActive;
            bool activePauseActive;
            long pauseGeneration;
            lock (_pauseStateLock)
            {
                pauseStateAvailable = _pauseStateKnown;
                normalPauseActive = (_pauseStateFlags & (1u | 2u | 8u)) != 0;
                activePauseActive = (_pauseStateFlags & 4u) != 0;
                pauseGeneration = _pauseGeneration;
            }
            var gForceAvailable = double.IsFinite(t.GForce);
            IReadOnlyDictionary<int, bool>? contactPointOnGround = null;
            IReadOnlyDictionary<int, double>? contactPointCompression = null;
            var contactPointTelemetryAvailable = false;
            ContactPointTelemetryStruct contactPointData = default;
            lock (_contactPointLock)
            {
                if (_contactPointTelemetryEnabled
                    && double.IsFinite(_latestContactPointSimulationTime)
                    && Math.Abs(t.SimulationTime - _latestContactPointSimulationTime) <= 0.5
                    && _latestContactPointAvailable
                    && (t.Agl <= 100 || t.SimOnGround > 0.5))
                {
                    contactPointData = _latestContactPointData;
                    contactPointTelemetryAvailable = true;
                }
            }
            if (contactPointTelemetryAvailable)
            {
                contactPointOnGround = ContactPointOnGround(contactPointData);
                contactPointCompression = ContactPointCompression(contactPointData);
            }
            var sample = new TelemetrySample
            {
                Timestamp = DateTimeOffset.UtcNow,
                SimulationTimeSeconds = t.SimulationTime,
                Latitude = t.Latitude,
                Longitude = t.Longitude,
                AltitudeFeet = t.Altitude,
                AglFeet = t.Agl,
                HeadingTrueDeg = t.Heading,
                PitchDeg = t.Pitch,
                BankDeg = t.Bank,
                AirspeedKts = t.Airspeed,
                GroundSpeedKts = t.GroundVelocity,
                VerticalSpeedFpm = t.VerticalSpeed,
                TouchdownNormalVelocityFps = double.IsFinite(t.TouchdownNormalVelocity)
                    ? t.TouchdownNormalVelocity
                    : null,
                GForce = gForceAvailable ? t.GForce : 1.0,
                GForceAvailable = gForceAvailable,
                SimOnGround = t.SimOnGround > 0.5,
                GearOnGroundByIndex = new Dictionary<int, bool>
                {
                    [0] = t.GearOnGround0 > 0.5, [1] = t.GearOnGround1 > 0.5,
                    [2] = t.GearOnGround2 > 0.5, [3] = t.GearOnGround3 > 0.5,
                    [4] = t.GearOnGround4 > 0.5, [5] = t.GearOnGround5 > 0.5,
                    [6] = t.GearOnGround6 > 0.5, [7] = t.GearOnGround7 > 0.5,
                    [8] = t.GearOnGround8 > 0.5, [9] = t.GearOnGround9 > 0.5,
                    [10] = t.GearOnGround10 > 0.5, [11] = t.GearOnGround11 > 0.5,
                    [12] = t.GearOnGround12 > 0.5, [13] = t.GearOnGround13 > 0.5,
                    [14] = t.GearOnGround14 > 0.5, [15] = t.GearOnGround15 > 0.5
                },
                ContactPointOnGroundByIndex = contactPointOnGround,
                ContactPointCompressionByIndex = contactPointCompression,
                ContactPointTelemetryAvailable = contactPointTelemetryAvailable,
                GearHandlePosition = t.GearHandle,
                IsGearRetractable = t.IsGearRetractable > 0.5,
                IsGearWheels = t.IsGearWheels > 0.5,
                IsGearFloats = t.IsGearFloats > 0.5,
                IsTailDragger = t.IsTailDragger > 0.5,
                FlapsHandleIndex = (int)Math.Round(t.FlapsIndex),
                FlapsHandlePositionCount = double.IsFinite(t.FlapsHandlePositionCount)
                    && t.FlapsHandlePositionCount >= 0
                    ? (int)Math.Round(t.FlapsHandlePositionCount)
                    : null,
                SpoilersAvailable = double.IsFinite(t.SpoilersAvailable)
                    ? t.SpoilersAvailable > 0.5
                    : null,
                AutopilotAvailable = double.IsFinite(t.AutopilotAvailable)
                    ? t.AutopilotAvailable > 0.5
                    : null,
                ThrottleLowerLimitPercent = double.IsFinite(t.ThrottleLowerLimit)
                    ? t.ThrottleLowerLimit
                    : null,
                SpoilersHandlePosition = t.SpoilersHandle,
                // Max panel deflection is ground truth; handle alone mis-reports "armed" as out.
                SpoilersSurfacePosition = Math.Max(t.SpoilersLeft, t.SpoilersRight),
                SpoilersLeftPosition = t.SpoilersLeft,
                SpoilersRightPosition = t.SpoilersRight,
                ManualBrakeLeftPosition = ResolveManualBrakePosition(
                    t.BrakeLeft, t.IniBrakePedalLeft, t.AutoBrakesActive > 0.5),
                ManualBrakeRightPosition = ResolveManualBrakePosition(
                    t.BrakeRight, t.IniBrakePedalRight, t.AutoBrakesActive > 0.5),
                AutoBrakesActive = double.IsFinite(t.AutoBrakesActive)
                    ? t.AutoBrakesActive > 0.5
                    : null,
                EngineCount = t.EngineCount is >= 1 and <= 4 && double.IsFinite(t.EngineCount)
                    ? (int)Math.Round(t.EngineCount)
                    : null,
                EngineCombustionByIndex = new Dictionary<int, bool>
                {
                    [1] = t.EngineCombustion1 > 0.5, [2] = t.EngineCombustion2 > 0.5,
                    [3] = t.EngineCombustion3 > 0.5, [4] = t.EngineCombustion4 > 0.5
                },
                ReverseThrustEngagedByIndex = new Dictionary<int, bool>
                {
                    [1] = t.ReverseThrustEngaged1 > 0.5, [2] = t.ReverseThrustEngaged2 > 0.5,
                    [3] = t.ReverseThrustEngaged3 > 0.5, [4] = t.ReverseThrustEngaged4 > 0.5
                },
                ReverseNozzlePositionByIndex = new Dictionary<int, double>
                {
                    [1] = NormalizeUnitPosition(t.ReverseNozzle1), [2] = NormalizeUnitPosition(t.ReverseNozzle2),
                    [3] = NormalizeUnitPosition(t.ReverseNozzle3), [4] = NormalizeUnitPosition(t.ReverseNozzle4)
                },
                ThrottleLeverPositionPercentByIndex = new Dictionary<int, double>
                {
                    [1] = t.ThrottleLever1, [2] = t.ThrottleLever2,
                    [3] = t.ThrottleLever3, [4] = t.ThrottleLever4
                },
                WindDirectionDeg = t.WindDir,
                WindVelocityKts = t.WindVel,
                RadioHeightFeet = t.RadioHeight,
                RadioHeightAvailable = true,
                AutopilotHeadingHoldActive = t.AutopilotHeadingLock > 0.5,
                AutopilotAltitudeHoldActive = t.AutopilotAltitudeLock > 0.5,
                AutopilotMasterActive = t.AutopilotMaster > 0.5,
                AutopilotChannel1Active = t.AutopilotMaster > 0.5 || t.IniAp1On > 0.5,
                AutopilotChannel2Active = t.IniAp2On > 0.5,
                AutothrustActive = t.AutopilotManagedThrottleActive > 0.5
                                   || t.IniAthrLight > 0.5
                                   || t.IniAthrModeActive > 0.5,
                AutothrustArmed = t.AutopilotThrottleArm > 0.5
                                  || t.IniAutothrottleArmed > 0.5,
                SimulationRate = t.SimulationRate > 0 && double.IsFinite(t.SimulationRate)
                    ? t.SimulationRate
                    : null,
                CameraState = t.CameraState > 0 && double.IsFinite(t.CameraState)
                    ? (int)Math.Round(t.CameraState)
                    : null,
                PauseStateAvailable = pauseStateAvailable,
                NormalPauseActive = normalPauseActive,
                ActivePauseActive = activePauseActive,
                PauseGeneration = pauseGeneration,
                AircraftTitle = _cachedAircraftTitle,
                DesignSpeedVs0Kts = t.DesignSpeedVs0,
                StallWarningActive = t.StallWarning > 0.5,
                TotalWeightLbs = t.TotalWeight > 0 ? t.TotalWeight : null
            };
            TelemetryReceived?.Invoke(this, sample);
        }
        catch (Exception ex)
        {
            Log($"Telemetry parse: {ex.Message}");
        }
    }

    private static double ResolveManualBrakePosition(
        double standardPosition,
        double a330PedalPosition,
        bool autobrakeActive)
    {
        var pedal = NormalizeUnitPosition(a330PedalPosition);
        if (autobrakeActive)
            return pedal;
        return Math.Max(pedal, Math.Clamp(standardPosition / 32768.0, 0, 1));
    }

    private static double NormalizeUnitPosition(double raw)
    {
        if (!double.IsFinite(raw) || raw <= 0) return 0;
        if (raw <= 1) return raw;
        if (raw <= 100) return raw / 100.0;
        if (raw <= 16384) return raw / 16384.0;
        return Math.Clamp(raw / 32768.0, 0, 1);
    }

    private static IReadOnlyDictionary<int, bool> ContactPointOnGround(
        ContactPointTelemetryStruct t) => new Dictionary<int, bool>
    {
        [0] = t.ContactPointOnGround0 > 0.5, [1] = t.ContactPointOnGround1 > 0.5,
        [2] = t.ContactPointOnGround2 > 0.5, [3] = t.ContactPointOnGround3 > 0.5,
        [4] = t.ContactPointOnGround4 > 0.5, [5] = t.ContactPointOnGround5 > 0.5,
        [6] = t.ContactPointOnGround6 > 0.5, [7] = t.ContactPointOnGround7 > 0.5,
        [8] = t.ContactPointOnGround8 > 0.5, [9] = t.ContactPointOnGround9 > 0.5,
        [10] = t.ContactPointOnGround10 > 0.5, [11] = t.ContactPointOnGround11 > 0.5,
        [12] = t.ContactPointOnGround12 > 0.5, [13] = t.ContactPointOnGround13 > 0.5,
        [14] = t.ContactPointOnGround14 > 0.5, [15] = t.ContactPointOnGround15 > 0.5
    };

    private static IReadOnlyDictionary<int, double> ContactPointCompression(
        ContactPointTelemetryStruct t) => new Dictionary<int, double>
    {
        [0] = NormalizeUnitPosition(t.ContactPointCompression0),
        [1] = NormalizeUnitPosition(t.ContactPointCompression1),
        [2] = NormalizeUnitPosition(t.ContactPointCompression2),
        [3] = NormalizeUnitPosition(t.ContactPointCompression3),
        [4] = NormalizeUnitPosition(t.ContactPointCompression4),
        [5] = NormalizeUnitPosition(t.ContactPointCompression5),
        [6] = NormalizeUnitPosition(t.ContactPointCompression6),
        [7] = NormalizeUnitPosition(t.ContactPointCompression7),
        [8] = NormalizeUnitPosition(t.ContactPointCompression8),
        [9] = NormalizeUnitPosition(t.ContactPointCompression9),
        [10] = NormalizeUnitPosition(t.ContactPointCompression10),
        [11] = NormalizeUnitPosition(t.ContactPointCompression11),
        [12] = NormalizeUnitPosition(t.ContactPointCompression12),
        [13] = NormalizeUnitPosition(t.ContactPointCompression13),
        [14] = NormalizeUnitPosition(t.ContactPointCompression14),
        [15] = NormalizeUnitPosition(t.ContactPointCompression15)
    };

    private void EnsureDefinitions()
    {
        if (_sim is null || _defsRegistered) return;

        _sim.AddToDataDefinition(Definitions.Telemetry, "SIMULATION TIME", "seconds",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
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
        _sim.AddToDataDefinition(Definitions.Telemetry, "PLANE TOUCHDOWN NORMAL VELOCITY", "feet per second",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "G FORCE", "GForce",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "SIM ON GROUND", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        for (var gearIndex = 0; gearIndex <= 15; gearIndex++)
            _sim.AddToDataDefinition(Definitions.Telemetry, $"GEAR IS ON GROUND:{gearIndex}", "bool",
                SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "GEAR HANDLE POSITION", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "IS GEAR RETRACTABLE", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "IS GEAR WHEELS", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "IS GEAR FLOATS", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "IS TAIL DRAGGER", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "FLAPS HANDLE INDEX", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "FLAPS NUM HANDLE POSITIONS", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "SPOILER AVAILABLE", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AUTOPILOT AVAILABLE", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "THROTTLE LOWER LIMIT", "percent",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AMBIENT WIND DIRECTION", "degrees",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AMBIENT WIND VELOCITY", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "RADIO HEIGHT", "feet",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "DESIGN SPEED VS0", "knots",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "STALL WARNING", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "TOTAL WEIGHT", "pounds",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        // percent over 100 → stable 0–1. Unit "position" is 0–16383 on some airframes and
        // made stowed A330 levers look "out" when they were visually retracted.
        _sim.AddToDataDefinition(Definitions.Telemetry, "SPOILERS HANDLE POSITION", "percent over 100",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "SPOILERS LEFT POSITION", "percent over 100",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "SPOILERS RIGHT POSITION", "percent over 100",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "BRAKE PARKING POSITION", "percent over 100",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "BRAKE LEFT POSITION", "position",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "BRAKE RIGHT POSITION", "position",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AUTOBRAKES ACTIVE", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "SIMULATION RATE", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "CAMERA STATE", "Enum",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AUTOPILOT HEADING LOCK", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AUTOPILOT ALTITUDE LOCK", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AUTOPILOT MASTER", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AUTOPILOT THROTTLE ARM", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "AUTOPILOT MANAGED THROTTLE ACTIVE", "bool",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "L:INI_AP1_ON", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "L:INI_AP2_ON", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "L:INI_ATHR_LIGHT", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "L:INI_ATHR_MODE_ACTIVE", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "L:INI_AUTOTHROTTLE_ARMED", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "L:INI_BRAKE_PEDAL_LEFT", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "L:INI_BRAKE_PEDAL_RIGHT", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.Telemetry, "NUMBER OF ENGINES", "number",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        for (var engineIndex = 1; engineIndex <= 4; engineIndex++)
            _sim.AddToDataDefinition(Definitions.Telemetry, $"GENERAL ENG COMBUSTION:{engineIndex}", "bool",
                SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        for (var engineIndex = 1; engineIndex <= 4; engineIndex++)
            _sim.AddToDataDefinition(Definitions.Telemetry, $"GENERAL ENG REVERSE THRUST ENGAGED:{engineIndex}", "bool",
                SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        for (var engineIndex = 1; engineIndex <= 4; engineIndex++)
            _sim.AddToDataDefinition(Definitions.Telemetry, $"TURB ENG REVERSE NOZZLE PERCENT:{engineIndex}", "percent",
                SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        for (var engineIndex = 1; engineIndex <= 4; engineIndex++)
            _sim.AddToDataDefinition(Definitions.Telemetry, $"GENERAL ENG THROTTLE LEVER POSITION:{engineIndex}", "percent",
                SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        _sim.AddToDataDefinition(Definitions.ContactPoints, "SIMULATION TIME", "seconds",
            SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        for (var contactPointIndex = 0; contactPointIndex <= 15; contactPointIndex++)
            _sim.AddToDataDefinition(Definitions.ContactPoints,
                $"CONTACT POINT IS ON GROUND:{contactPointIndex}", "bool",
                SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);
        for (var contactPointIndex = 0; contactPointIndex <= 15; contactPointIndex++)
            _sim.AddToDataDefinition(Definitions.ContactPoints,
                $"CONTACT POINT COMPRESSION:{contactPointIndex}", "position",
                SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);

        _sim.RegisterDataDefineStruct<TelemetryStruct>(Definitions.Telemetry);
        _sim.RegisterDataDefineStruct<ContactPointTelemetryStruct>(Definitions.ContactPoints);

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

        // Free-mode navdata: request only the runway and start fields needed by scoring.
        // This API is read-only and never changes the aircraft or simulation state.
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "OPEN AIRPORT");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "COUNTRY");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "OPEN RUNWAY");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "LATITUDE");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "LONGITUDE");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "ALTITUDE");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "HEADING");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "LENGTH");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "WIDTH");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "SURFACE");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "PRIMARY_NUMBER");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "PRIMARY_DESIGNATOR");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "SECONDARY_NUMBER");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "SECONDARY_DESIGNATOR");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "PRIMARY_CLOSED");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "SECONDARY_CLOSED");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "PRIMARY_LANDING");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "SECONDARY_LANDING");
        // Offset-threshold pavement lengths are needed to derive landing distance available (LDA).
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "PRIMARY_THRESHOLD");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "SECONDARY_THRESHOLD");
        // VASI/PAPI angle structs (arrive as FACILITY_DATA_TYPE.VASI children of RUNWAY).
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "PRIMARY_LEFT_VASI");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "PRIMARY_RIGHT_VASI");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "SECONDARY_LEFT_VASI");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "SECONDARY_RIGHT_VASI");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "CLOSE RUNWAY");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "OPEN START");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "LATITUDE");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "LONGITUDE");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "ALTITUDE");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "HEADING");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "NUMBER");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "DESIGNATOR");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "TYPE");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "CLOSE START");
        _sim.AddToFacilityDefinition(Definitions.AirportFacility, "CLOSE AIRPORT");
        _sim.RegisterFacilityDataDefineStruct<AirportDetailFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.AIRPORT);
        _sim.RegisterFacilityDataDefineStruct<RunwayFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.RUNWAY);
        _sim.RegisterFacilityDataDefineStruct<PavementFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.PAVEMENT);
        _sim.RegisterFacilityDataDefineStruct<RunwayStartFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.START);
        _sim.RegisterFacilityDataDefineStruct<VasiFacilityStruct>(
            SIMCONNECT_FACILITY_DATA_TYPE.VASI);

        _defsRegistered = true;
    }

    private void EnsureEvents()
    {
        if (_sim is null || _eventsMapped) return;
        try
        {
            _sim.MapClientEventToSimEvent(Events.GearDown, "GEAR_DOWN");
            _sim.MapClientEventToSimEvent(Events.GearUp, "GEAR_UP");
            _sim.MapClientEventToSimEvent(Events.GearSet, "GEAR_SET");
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
            _sim.MapClientEventToSimEvent(Events.ZuluDaySet, "ZULU_DAY_SET");
            _sim.MapClientEventToSimEvent(Events.ZuluYearSet, "ZULU_YEAR_SET");
            _sim.MapClientEventToSimEvent(Events.SpoilersOff, "SPOILERS_OFF");
            _sim.MapClientEventToSimEvent(Events.SpoilersOn, "SPOILERS_ON");
            _sim.MapClientEventToSimEvent(Events.SpoilersSet, "SPOILERS_SET");
            _sim.MapClientEventToSimEvent(Events.SpoilersArmSet, "SPOILERS_ARM_SET");
            _sim.MapClientEventToSimEvent(Events.ParkingBrakeSet, "PARKING_BRAKE_SET");
            _sim.MapClientEventToSimEvent(Events.ActivePauseOn, "ACTIVE_PAUSE_ON");
            _sim.MapClientEventToSimEvent(Events.ActivePauseOff, "ACTIVE_PAUSE_OFF");
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearDown, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearUp, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.GearSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.FlapsSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.PauseOff, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.PauseOn, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.SpoilersOff, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.SpoilersOn, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.SpoilersSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.SpoilersArmSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.ParkingBrakeSet, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.ActivePauseOff, false);
            _sim.AddClientEventToNotificationGroup(Groups.Input, Events.ActivePauseOn, false);
            _sim.SetNotificationGroupPriority(Groups.Input, MsfsSc.SIMCONNECT_GROUP_PRIORITY_HIGHEST);
            _sim.SubscribeToSystemEvent(Events.PauseStateEx1, "Pause_EX1");
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

        // Keep live TITLE available on every telemetry sample for Free Flight VAPP lookup.
        // SECOND is enough — aircraft type does not change frame-to-frame.
        _sim.RequestDataOnSimObject(
            Requests.AircraftTitleStream,
            Definitions.AircraftTitle,
            MsfsSc.SIMCONNECT_OBJECT_ID_USER,
            SIMCONNECT_PERIOD.SECOND,
            SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
            0, 0, 0);
    }

    public void SetNoseGearImpactTelemetryEnabled(bool enabled)
    {
        lock (_contactPointLock)
        {
            _contactPointTelemetryEnabled = enabled;
            _latestContactPointSimulationTime = double.NaN;
            _latestContactPointAvailable = false;
            _latestContactPointData = default;
        }

        if (_sim is null)
            return;

        try
        {
            _sim.RequestDataOnSimObject(
                Requests.ContactPoints,
                Definitions.ContactPoints,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                enabled ? SIMCONNECT_PERIOD.VISUAL_FRAME : SIMCONNECT_PERIOD.NEVER,
                SIMCONNECT_DATA_REQUEST_FLAG.DEFAULT,
                0, 0, 0);
            Log($"Nose-impact contact-point telemetry {(enabled ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            Log($"Contact-point telemetry toggle: {ex.Message}");
        }
    }

    private void CleanupSim()
    {
        if (_sim is not null)
        {
            try
            {
                _sim.OnRecvOpen -= OnRecvOpen;
                _sim.OnRecvQuit -= OnRecvQuit;
                _sim.OnRecvException -= OnRecvException;
                _sim.OnRecvSimobjectData -= OnRecvSimobjectData;
                _sim.OnRecvEvent -= OnRecvEvent;
                _sim.OnRecvAirportList -= OnRecvAirportList;
                _sim.OnRecvFacilityData -= OnRecvFacilityData;
                _sim.OnRecvFacilityDataEnd -= OnRecvFacilityDataEnd;
                _sim.Dispose();
            }
            catch { /* ignore */ }
        }

        _sim = null;
        _defsRegistered = false;
        _eventsMapped = false;
        lock (_pauseStateLock)
        {
            _pauseStateFlags = 0;
            _pauseGeneration = 0;
            _pauseStateKnown = false;
            _pauseWasActive = false;
        }
        lock (_contactPointLock)
        {
            _latestContactPointSimulationTime = double.NaN;
            _latestContactPointAvailable = false;
            _latestContactPointData = default;
        }
        _cachedAircraftTitle = null;
        _titleTcs?.TrySetResult(null);
        _titleTcs = null;
        _airportCatalogTcs?.TrySetException(new InvalidOperationException("SimConnect disconnected."));
        _airportCatalogTcs = null;
        _airportCatalogPackets.Clear();
        _airportCatalogCache = null;
        foreach (var request in _facilityRequests.Values)
            request.Completion.TrySetException(new InvalidOperationException("SimConnect disconnected."));
        _facilityRequests.Clear();
        _airportDetailCache.Clear();
    }

    private static string FacilityCacheKey(AirportFacility airport)
        => $"{airport.Icao}|{airport.Region}";

    private void Log(string message) => LogMessage?.Invoke(this, message);
}
