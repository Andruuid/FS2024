using System.Runtime.InteropServices;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Snapshots;
using Microsoft.FlightSimulator.SimConnect;
using MsfsSc = Microsoft.FlightSimulator.SimConnect.SimConnect;

namespace ChallengeLab.SimConnect;

/// <summary>
/// STORE tab: full flight-state capture + restore. Restore reuses the safe-apply pipeline
/// (SET PAUSE + FREEZE → teleport → exact body velocity → config settle → verify → careful
/// release). Never FlightLoad — mid-session .FLT loads crash MSFS 2024.
/// </summary>
public sealed partial class SimConnectClient
{
    private TaskCompletionSource<SnapshotCaptureStruct>? _snapshotCaptureTcs;
    private bool _snapshotDefsRegistered;
    private bool _snapshotEventsMapped;

    /// <summary>Extended settle when a ground restore has to move the gear down first.</summary>
    private const int MaxGearSettleMs = 25000;

    // Tank order is fixed — capture struct, write struct and dictionary keys must agree.
    private static readonly string[] FuelTankNames =
    {
        "CENTER", "CENTER2", "CENTER3",
        "LEFT MAIN", "LEFT AUX", "LEFT TIP",
        "RIGHT MAIN", "RIGHT AUX", "RIGHT TIP",
        "EXTERNAL1", "EXTERNAL2"
    };

    /// <summary>
    /// One-shot full-state read. Field order MUST match RegisterSnapshotDefinitions exactly.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct SnapshotCaptureStruct
    {
        public double Latitude;
        public double Longitude;
        public double AltitudeFeet;
        public double AglFeet;
        public double PitchDeg;
        public double BankDeg;
        public double HeadingTrueDeg;
        public double SimOnGround;
        public double IasKts;
        public double TasKts;
        public double GroundSpeedKts;
        public double VerticalSpeedFpm;
        public double BodyVelX;
        public double BodyVelY;
        public double BodyVelZ;
        public double RotVelX;
        public double RotVelY;
        public double RotVelZ;
        public double GearHandle;
        public double IsGearRetractable;
        public double GearTotalPctExtended;
        public double FlapsIndex;
        public double FlapsCount;
        public double SpoilersHandle;
        public double SpoilersLeft;
        public double SpoilersRight;
        public double ParkingBrake;
        public double ElevatorTrimRad;
        public double AileronTrimPct;
        public double RudderTrimPct;
        public double EngineCount;
        public double EngineType;
        public double Combustion1;
        public double Combustion2;
        public double Combustion3;
        public double Combustion4;
        public double Throttle1;
        public double Throttle2;
        public double Throttle3;
        public double Throttle4;
        public double Mixture1;
        public double Mixture2;
        public double Mixture3;
        public double Mixture4;
        public double PropLever1;
        public double PropLever2;
        public double PropLever3;
        public double PropLever4;
        public double N1_1;
        public double N1_2;
        public double N1_3;
        public double N1_4;
        public double N2_1;
        public double N2_2;
        public double N2_3;
        public double N2_4;
        public double FuelTotalGal;
        public double FuelCapacityGal;
        public double FuelTank1;
        public double FuelTank2;
        public double FuelTank3;
        public double FuelTank4;
        public double FuelTank5;
        public double FuelTank6;
        public double FuelTank7;
        public double FuelTank8;
        public double FuelTank9;
        public double FuelTank10;
        public double FuelTank11;
        public double LightBeacon;
        public double LightLanding;
        public double LightTaxi;
        public double LightNav;
        public double LightStrobe;
        public double LightPanel;
        public double LightRecognition;
        public double LightWing;
        public double LightLogo;
        public double LightCabin;
        public double ZuluTimeSeconds;
        public double ZuluDayOfYear;
        public double ZuluYear;
        public double LocalTimeSeconds;
        public double TimeZoneOffsetSeconds;
        public double SeaLevelPressureMb;
        public double AmbientTempC;
        public double AmbientVisibilityM;
        public double AmbientPrecipState;
        public double AmbientWindDirection;
        public double AmbientWindVelocity;
        public double SimulationRate;
        public double CameraState;
        public double TotalWeightLbs;
        public double ApMaster;
        public double ApFlightDirector;
        public double ApThrottleArm;
        public double ApManagedThrottle;
        public double ApYawDamper;
        public double ApHeadingLock;
        public double ApHeadingBugDeg;
        public double ApNav1Lock;
        public double ApApproachHold;
        public double ApGlideslopeHold;
        public double ApAltitudeLock;
        public double ApAltitudeTargetFeet;
        public double ApVerticalHold;
        public double ApVerticalTargetFpm;
        public double ApFlightLevelChange;
        public double ApAirspeedHold;
        public double ApAirspeedTargetKts;
        public double ApMachHold;
        public double ApMachTarget;
    }

    // Small single-purpose write structs: one unwritable simvar fails the whole
    // SetDataOnSimObject, so never mix independent groups into one definition.

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct FuelSetStruct
    {
        public double Tank1;
        public double Tank2;
        public double Tank3;
        public double Tank4;
        public double Tank5;
        public double Tank6;
        public double Tank7;
        public double Tank8;
        public double Tank9;
        public double Tank10;
        public double Tank11;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct TrimSetStruct
    {
        public double ElevatorTrimRad;
        public double AileronTrimPct;
        public double RudderTrimPct;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct ThrottleSetStruct
    {
        public double Throttle1;
        public double Throttle2;
        public double Throttle3;
        public double Throttle4;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct LightsSetStruct
    {
        public double Beacon;
        public double Landing;
        public double Taxi;
        public double Nav;
        public double Strobe;
        public double Panel;
        public double Recognition;
        public double Wing;
        public double Logo;
        public double Cabin;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct CombustionSetStruct
    {
        public double Combustion1;
        public double Combustion2;
        public double Combustion3;
        public double Combustion4;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    private struct PistonControlsSetStruct
    {
        public double Mixture1;
        public double Mixture2;
        public double Mixture3;
        public double Mixture4;
        public double PropLever1;
        public double PropLever2;
        public double PropLever3;
        public double PropLever4;
    }

    /// <summary>
    /// Register snapshot capture + write definitions. Failure is contained: the flag stays
    /// false and STORE capture/restore report unavailable, the rest of the app is unaffected.
    /// </summary>
    private void RegisterSnapshotDefinitions()
    {
        if (_sim is null || _snapshotDefsRegistered) return;

        try
        {
            void Add(Definitions def, string simvar, string? unit) =>
                _sim!.AddToDataDefinition(def, simvar, unit,
                    SIMCONNECT_DATATYPE.FLOAT64, 0, MsfsSc.SIMCONNECT_UNUSED);

            // --- Capture (order must match SnapshotCaptureStruct) ---
            Add(Definitions.SnapshotCapture, "PLANE LATITUDE", "degrees");
            Add(Definitions.SnapshotCapture, "PLANE LONGITUDE", "degrees");
            Add(Definitions.SnapshotCapture, "PLANE ALTITUDE", "feet");
            Add(Definitions.SnapshotCapture, "PLANE ALT ABOVE GROUND", "feet");
            Add(Definitions.SnapshotCapture, "PLANE PITCH DEGREES", "degrees");
            Add(Definitions.SnapshotCapture, "PLANE BANK DEGREES", "degrees");
            Add(Definitions.SnapshotCapture, "PLANE HEADING DEGREES TRUE", "degrees");
            Add(Definitions.SnapshotCapture, "SIM ON GROUND", "bool");
            Add(Definitions.SnapshotCapture, "AIRSPEED INDICATED", "knots");
            Add(Definitions.SnapshotCapture, "AIRSPEED TRUE", "knots");
            Add(Definitions.SnapshotCapture, "GROUND VELOCITY", "knots");
            Add(Definitions.SnapshotCapture, "VERTICAL SPEED", "feet per minute");
            Add(Definitions.SnapshotCapture, "VELOCITY BODY X", "meters per second");
            Add(Definitions.SnapshotCapture, "VELOCITY BODY Y", "meters per second");
            Add(Definitions.SnapshotCapture, "VELOCITY BODY Z", "meters per second");
            Add(Definitions.SnapshotCapture, "ROTATION VELOCITY BODY X", "radians per second");
            Add(Definitions.SnapshotCapture, "ROTATION VELOCITY BODY Y", "radians per second");
            Add(Definitions.SnapshotCapture, "ROTATION VELOCITY BODY Z", "radians per second");
            Add(Definitions.SnapshotCapture, "GEAR HANDLE POSITION", "bool");
            Add(Definitions.SnapshotCapture, "IS GEAR RETRACTABLE", "bool");
            Add(Definitions.SnapshotCapture, "GEAR TOTAL PCT EXTENDED", "percent over 100");
            Add(Definitions.SnapshotCapture, "FLAPS HANDLE INDEX", "number");
            Add(Definitions.SnapshotCapture, "FLAPS NUM HANDLE POSITIONS", "number");
            Add(Definitions.SnapshotCapture, "SPOILERS HANDLE POSITION", "percent over 100");
            Add(Definitions.SnapshotCapture, "SPOILERS LEFT POSITION", "percent over 100");
            Add(Definitions.SnapshotCapture, "SPOILERS RIGHT POSITION", "percent over 100");
            Add(Definitions.SnapshotCapture, "BRAKE PARKING POSITION", "percent over 100");
            Add(Definitions.SnapshotCapture, "ELEVATOR TRIM POSITION", "radians");
            Add(Definitions.SnapshotCapture, "AILERON TRIM PCT", "percent over 100");
            Add(Definitions.SnapshotCapture, "RUDDER TRIM PCT", "percent over 100");
            Add(Definitions.SnapshotCapture, "NUMBER OF ENGINES", "number");
            Add(Definitions.SnapshotCapture, "ENGINE TYPE", "Enum");
            for (var i = 1; i <= 4; i++)
                Add(Definitions.SnapshotCapture, $"GENERAL ENG COMBUSTION:{i}", "bool");
            for (var i = 1; i <= 4; i++)
                Add(Definitions.SnapshotCapture, $"GENERAL ENG THROTTLE LEVER POSITION:{i}", "percent");
            for (var i = 1; i <= 4; i++)
                Add(Definitions.SnapshotCapture, $"GENERAL ENG MIXTURE LEVER POSITION:{i}", "percent");
            for (var i = 1; i <= 4; i++)
                Add(Definitions.SnapshotCapture, $"GENERAL ENG PROPELLER LEVER POSITION:{i}", "percent");
            for (var i = 1; i <= 4; i++)
                Add(Definitions.SnapshotCapture, $"TURB ENG N1:{i}", "percent");
            for (var i = 1; i <= 4; i++)
                Add(Definitions.SnapshotCapture, $"TURB ENG N2:{i}", "percent");
            Add(Definitions.SnapshotCapture, "FUEL TOTAL QUANTITY", "gallons");
            Add(Definitions.SnapshotCapture, "FUEL TOTAL CAPACITY", "gallons");
            foreach (var tank in FuelTankNames)
                Add(Definitions.SnapshotCapture, $"FUEL TANK {tank} QUANTITY", "gallons");
            Add(Definitions.SnapshotCapture, "LIGHT BEACON", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT LANDING", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT TAXI", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT NAV", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT STROBE", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT PANEL", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT RECOGNITION", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT WING", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT LOGO", "bool");
            Add(Definitions.SnapshotCapture, "LIGHT CABIN", "bool");
            Add(Definitions.SnapshotCapture, "ZULU TIME", "seconds");
            Add(Definitions.SnapshotCapture, "ZULU DAY OF YEAR", "number");
            Add(Definitions.SnapshotCapture, "ZULU YEAR", "number");
            Add(Definitions.SnapshotCapture, "LOCAL TIME", "seconds");
            Add(Definitions.SnapshotCapture, "TIME ZONE OFFSET", "seconds");
            Add(Definitions.SnapshotCapture, "SEA LEVEL PRESSURE", "millibars");
            Add(Definitions.SnapshotCapture, "AMBIENT TEMPERATURE", "celsius");
            Add(Definitions.SnapshotCapture, "AMBIENT VISIBILITY", "meters");
            Add(Definitions.SnapshotCapture, "AMBIENT PRECIP STATE", "mask");
            Add(Definitions.SnapshotCapture, "AMBIENT WIND DIRECTION", "degrees");
            Add(Definitions.SnapshotCapture, "AMBIENT WIND VELOCITY", "knots");
            Add(Definitions.SnapshotCapture, "SIMULATION RATE", "number");
            Add(Definitions.SnapshotCapture, "CAMERA STATE", "Enum");
            Add(Definitions.SnapshotCapture, "TOTAL WEIGHT", "pounds");
            Add(Definitions.SnapshotCapture, "AUTOPILOT MASTER", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT FLIGHT DIRECTOR ACTIVE", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT THROTTLE ARM", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT MANAGED THROTTLE ACTIVE", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT YAW DAMPER", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT HEADING LOCK", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT HEADING LOCK DIR", "degrees");
            Add(Definitions.SnapshotCapture, "AUTOPILOT NAV1 LOCK", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT APPROACH HOLD", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT GLIDESLOPE HOLD", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT ALTITUDE LOCK", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT ALTITUDE LOCK VAR", "feet");
            Add(Definitions.SnapshotCapture, "AUTOPILOT VERTICAL HOLD", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT VERTICAL HOLD VAR", "feet per minute");
            Add(Definitions.SnapshotCapture, "AUTOPILOT FLIGHT LEVEL CHANGE", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT AIRSPEED HOLD", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT AIRSPEED HOLD VAR", "knots");
            Add(Definitions.SnapshotCapture, "AUTOPILOT MACH HOLD", "bool");
            Add(Definitions.SnapshotCapture, "AUTOPILOT MACH HOLD VAR", "number");
            _sim.RegisterDataDefineStruct<SnapshotCaptureStruct>(Definitions.SnapshotCapture);

            // --- Writes ---
            foreach (var tank in FuelTankNames)
                Add(Definitions.FuelSet, $"FUEL TANK {tank} QUANTITY", "gallons");
            _sim.RegisterDataDefineStruct<FuelSetStruct>(Definitions.FuelSet);

            Add(Definitions.TrimSet, "ELEVATOR TRIM POSITION", "radians");
            Add(Definitions.TrimSet, "AILERON TRIM PCT", "percent over 100");
            Add(Definitions.TrimSet, "RUDDER TRIM PCT", "percent over 100");
            _sim.RegisterDataDefineStruct<TrimSetStruct>(Definitions.TrimSet);

            for (var i = 1; i <= 4; i++)
                Add(Definitions.ThrottleSet, $"GENERAL ENG THROTTLE LEVER POSITION:{i}", "percent");
            _sim.RegisterDataDefineStruct<ThrottleSetStruct>(Definitions.ThrottleSet);

            Add(Definitions.LightsSet, "LIGHT BEACON", "bool");
            Add(Definitions.LightsSet, "LIGHT LANDING", "bool");
            Add(Definitions.LightsSet, "LIGHT TAXI", "bool");
            Add(Definitions.LightsSet, "LIGHT NAV", "bool");
            Add(Definitions.LightsSet, "LIGHT STROBE", "bool");
            Add(Definitions.LightsSet, "LIGHT PANEL", "bool");
            Add(Definitions.LightsSet, "LIGHT RECOGNITION", "bool");
            Add(Definitions.LightsSet, "LIGHT WING", "bool");
            Add(Definitions.LightsSet, "LIGHT LOGO", "bool");
            Add(Definitions.LightsSet, "LIGHT CABIN", "bool");
            _sim.RegisterDataDefineStruct<LightsSetStruct>(Definitions.LightsSet);

            for (var i = 1; i <= 4; i++)
                Add(Definitions.CombustionSet, $"GENERAL ENG COMBUSTION:{i}", "bool");
            _sim.RegisterDataDefineStruct<CombustionSetStruct>(Definitions.CombustionSet);

            for (var i = 1; i <= 4; i++)
                Add(Definitions.PistonControlsSet, $"GENERAL ENG MIXTURE LEVER POSITION:{i}", "percent");
            for (var i = 1; i <= 4; i++)
                Add(Definitions.PistonControlsSet, $"GENERAL ENG PROPELLER LEVER POSITION:{i}", "percent");
            _sim.RegisterDataDefineStruct<PistonControlsSetStruct>(Definitions.PistonControlsSet);

            _snapshotDefsRegistered = true;
        }
        catch (Exception ex)
        {
            // Leave the flag false: a half-registered definition must never be requested.
            Log($"Snapshot definitions failed to register — STORE capture/restore disabled: {ex.Message}");
        }
    }

    /// <summary>
    /// Autopilot key-event mappings, each in its own try/catch so one unknown event name
    /// cannot break the others (unlike the all-or-nothing EnsureEvents block).
    /// </summary>
    private void EnsureSnapshotEvents()
    {
        if (_sim is null || _snapshotEventsMapped) return;

        var mappings = new (Events Id, string Name)[]
        {
            (Events.AutopilotOn, "AUTOPILOT_ON"),
            (Events.AutopilotOff, "AUTOPILOT_OFF"),
            (Events.AutoThrottleArm, "AUTO_THROTTLE_ARM"),
            (Events.ToggleFlightDirector, "TOGGLE_FLIGHT_DIRECTOR"),
            (Events.HeadingBugSet, "HEADING_BUG_SET"),
            (Events.ApAltVarSet, "AP_ALT_VAR_SET_ENGLISH"),
            (Events.ApSpdVarSet, "AP_SPD_VAR_SET"),
            (Events.ApMachVarSet, "AP_MACH_VAR_SET"),
            (Events.ApVsVarSet, "AP_VS_VAR_SET_ENGLISH"),
            (Events.ApHdgHoldOn, "AP_HDG_HOLD_ON"),
            (Events.ApHdgHoldOff, "AP_HDG_HOLD_OFF"),
            (Events.ApAltHoldOn, "AP_ALT_HOLD_ON"),
            (Events.ApAltHoldOff, "AP_ALT_HOLD_OFF"),
            (Events.ApNav1HoldOn, "AP_NAV1_HOLD_ON"),
            (Events.ApNav1HoldOff, "AP_NAV1_HOLD_OFF"),
            (Events.ApAprHoldOn, "AP_APR_HOLD_ON"),
            (Events.ApAprHoldOff, "AP_APR_HOLD_OFF"),
            (Events.ApAirspeedOn, "AP_AIRSPEED_ON"),
            (Events.ApAirspeedOff, "AP_AIRSPEED_OFF"),
            (Events.ApMachOn, "AP_MACH_ON"),
            (Events.ApMachOff, "AP_MACH_OFF"),
            (Events.ApPanelVsOn, "AP_PANEL_VS_ON"),
            (Events.ApPanelVsOff, "AP_PANEL_VS_OFF"),
            (Events.FlightLevelChangeOn, "FLIGHT_LEVEL_CHANGE_ON"),
            (Events.FlightLevelChangeOff, "FLIGHT_LEVEL_CHANGE_OFF"),
            (Events.YawDamperOn, "YAW_DAMPER_ON"),
            (Events.YawDamperOff, "YAW_DAMPER_OFF")
        };

        foreach (var (id, name) in mappings)
        {
            try
            {
                _sim.MapClientEventToSimEvent(id, name);
            }
            catch (Exception ex)
            {
                Log($"AP event map '{name}': {ex.Message}");
            }
        }

        _snapshotEventsMapped = true;
    }

    private void TransmitSnapshotEvent(Events id, uint value = 0)
    {
        if (_sim is null) return;
        try
        {
            _sim.TransmitClientEvent(
                MsfsSc.SIMCONNECT_OBJECT_ID_USER, id, value,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
        catch (Exception ex)
        {
            Log($"TransmitSnapshotEvent {id}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ capture

    public async Task<FlightStateSnapshot?> CaptureSnapshotAsync(CancellationToken ct = default)
    {
        if (!IsConnected || _sim is null)
        {
            Log("CaptureSnapshot: not connected.");
            return null;
        }

        EnsureDefinitions();
        if (!_snapshotDefsRegistered)
        {
            Log("CaptureSnapshot: snapshot definitions unavailable.");
            return null;
        }

        var raw = await RequestSnapshotStateOnceAsync(ct);
        if (raw is null)
        {
            Log("CaptureSnapshot: no state data received (menu or paused load screen?).");
            return null;
        }

        var title = _cachedAircraftTitle;
        if (string.IsNullOrWhiteSpace(title))
            title = await RequestAircraftTitleAsync(ct);

        return MapSnapshot(raw.Value, title);
    }

    /// <summary>One-shot read of the full snapshot struct with a manual pump loop.</summary>
    private async Task<SnapshotCaptureStruct?> RequestSnapshotStateOnceAsync(CancellationToken ct)
    {
        if (_sim is null || !_snapshotDefsRegistered) return null;

        try
        {
            var tcs = new TaskCompletionSource<SnapshotCaptureStruct>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _snapshotCaptureTcs = tcs;

            _sim.RequestDataOnSimObject(
                Requests.SnapshotCapture,
                Definitions.SnapshotCapture,
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
                Log("Snapshot state request timed out.");
                _snapshotCaptureTcs = null;
                return null;
            }

            return await tcs.Task;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"RequestSnapshotState: {ex.Message}");
            _snapshotCaptureTcs = null;
            return null;
        }
    }

    private FlightStateSnapshot MapSnapshot(in SnapshotCaptureStruct raw, string? title)
    {
        SnapshotPauseContext pauseContext;
        lock (_pauseStateLock)
        {
            if (!_pauseStateKnown) pauseContext = SnapshotPauseContext.Unknown;
            else if ((_pauseStateFlags & 4u) != 0) pauseContext = SnapshotPauseContext.ActivePause;
            else if ((_pauseStateFlags & (1u | 2u | 8u)) != 0) pauseContext = SnapshotPauseContext.NormalPause;
            else pauseContext = SnapshotPauseContext.Flying;
        }

        var tanks = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        Span<double> tankValues = stackalloc double[]
        {
            raw.FuelTank1, raw.FuelTank2, raw.FuelTank3, raw.FuelTank4, raw.FuelTank5,
            raw.FuelTank6, raw.FuelTank7, raw.FuelTank8, raw.FuelTank9, raw.FuelTank10,
            raw.FuelTank11
        };
        for (var i = 0; i < FuelTankNames.Length; i++)
        {
            if (double.IsFinite(tankValues[i]) && tankValues[i] > 0.001)
                tanks[FuelTankNames[i]] = tankValues[i];
        }

        return new FlightStateSnapshot
        {
            CreatedUtc = DateTimeOffset.UtcNow,
            AircraftTitle = title ?? "",
            PauseContext = pauseContext,

            Latitude = raw.Latitude,
            Longitude = raw.Longitude,
            AltitudeFeet = raw.AltitudeFeet,
            PitchDeg = raw.PitchDeg,
            BankDeg = raw.BankDeg,
            HeadingTrueDeg = raw.HeadingTrueDeg,
            OnGround = raw.SimOnGround > 0.5,
            IasKts = Math.Max(0, raw.IasKts),

            BodyVelXMs = raw.BodyVelX,
            BodyVelYMs = raw.BodyVelY,
            BodyVelZMs = raw.BodyVelZ,
            RotVelXRadS = raw.RotVelX,
            RotVelYRadS = raw.RotVelY,
            RotVelZRadS = raw.RotVelZ,
            TasKts = raw.TasKts,
            GroundSpeedKts = Math.Max(0, raw.GroundSpeedKts),
            VerticalSpeedFpm = raw.VerticalSpeedFpm,
            AglFeet = raw.AglFeet,

            GearHandleDown = raw.GearHandle > 0.5,
            IsGearRetractable = raw.IsGearRetractable > 0.5,
            GearTotalPctExtended = raw.GearTotalPctExtended,
            FlapsHandleIndex = (int)Math.Round(raw.FlapsIndex),
            FlapsHandleCount = (int)Math.Round(raw.FlapsCount),
            SpoilersHandle01 = raw.SpoilersHandle,
            SpoilersLeft01 = raw.SpoilersLeft,
            SpoilersRight01 = raw.SpoilersRight,
            ParkingBrakeOn = raw.ParkingBrake > 0.5,
            SimulationRate = raw.SimulationRate,

            Trim = new SnapshotTrim
            {
                ElevatorTrimRad = raw.ElevatorTrimRad,
                AileronTrimPct01 = raw.AileronTrimPct,
                RudderTrimPct01 = raw.RudderTrimPct
            },
            Fuel = new SnapshotFuel
            {
                TotalGallons = raw.FuelTotalGal,
                TotalCapacityGallons = raw.FuelCapacityGal,
                Tanks = tanks
            },
            Engines = new SnapshotEngines
            {
                Count = (int)Math.Round(raw.EngineCount),
                EngineType = (int)Math.Round(raw.EngineType),
                Combustion = new[]
                {
                    raw.Combustion1 > 0.5, raw.Combustion2 > 0.5,
                    raw.Combustion3 > 0.5, raw.Combustion4 > 0.5
                },
                ThrottleLeverPct = new[] { raw.Throttle1, raw.Throttle2, raw.Throttle3, raw.Throttle4 },
                MixtureLeverPct = new[] { raw.Mixture1, raw.Mixture2, raw.Mixture3, raw.Mixture4 },
                PropellerLeverPct = new[] { raw.PropLever1, raw.PropLever2, raw.PropLever3, raw.PropLever4 }
            },
            Lights = new SnapshotLights
            {
                Beacon = raw.LightBeacon > 0.5,
                Landing = raw.LightLanding > 0.5,
                Taxi = raw.LightTaxi > 0.5,
                Nav = raw.LightNav > 0.5,
                Strobe = raw.LightStrobe > 0.5,
                Panel = raw.LightPanel > 0.5,
                Recognition = raw.LightRecognition > 0.5,
                Wing = raw.LightWing > 0.5,
                Logo = raw.LightLogo > 0.5,
                Cabin = raw.LightCabin > 0.5
            },
            Time = new SnapshotTime
            {
                ZuluTimeSeconds = raw.ZuluTimeSeconds,
                ZuluDayOfYear = (int)Math.Round(raw.ZuluDayOfYear),
                ZuluYear = (int)Math.Round(raw.ZuluYear),
                LocalTimeSeconds = raw.LocalTimeSeconds,
                TimeZoneOffsetSeconds = raw.TimeZoneOffsetSeconds
            },
            Weather = new SnapshotWeather
            {
                WindDirDeg = raw.AmbientWindDirection,
                WindKts = raw.AmbientWindVelocity,
                SeaLevelPressureMb = raw.SeaLevelPressureMb,
                AmbientTempC = raw.AmbientTempC,
                VisibilityM = raw.AmbientVisibilityM,
                PrecipState = raw.AmbientPrecipState,
                ReconstructedMetar = BuildReconstructedMetar(raw)
            },
            Autopilot = new SnapshotAutopilot
            {
                Master = raw.ApMaster > 0.5,
                FlightDirector = raw.ApFlightDirector > 0.5,
                AutothrottleArmed = raw.ApThrottleArm > 0.5,
                ManagedThrottleActive = raw.ApManagedThrottle > 0.5,
                YawDamper = raw.ApYawDamper > 0.5,
                HeadingLock = raw.ApHeadingLock > 0.5,
                HeadingBugDeg = raw.ApHeadingBugDeg,
                Nav1Lock = raw.ApNav1Lock > 0.5,
                ApproachHold = raw.ApApproachHold > 0.5,
                GlideslopeHold = raw.ApGlideslopeHold > 0.5,
                AltitudeLock = raw.ApAltitudeLock > 0.5,
                AltitudeTargetFeet = raw.ApAltitudeTargetFeet,
                VerticalSpeedHold = raw.ApVerticalHold > 0.5,
                VerticalSpeedTargetFpm = raw.ApVerticalTargetFpm,
                FlightLevelChange = raw.ApFlightLevelChange > 0.5,
                AirspeedHold = raw.ApAirspeedHold > 0.5,
                AirspeedTargetKts = raw.ApAirspeedTargetKts,
                MachHold = raw.ApMachHold > 0.5,
                MachTarget = raw.ApMachTarget
            },
            Info = new SnapshotInfo
            {
                N1Pct = new[] { raw.N1_1, raw.N1_2, raw.N1_3, raw.N1_4 },
                N2Pct = new[] { raw.N2_1, raw.N2_2, raw.N2_3, raw.N2_4 },
                CameraState = raw.CameraState,
                TotalWeightLbs = raw.TotalWeightLbs
            }
        };
    }

    /// <summary>
    /// Ambient conditions → fixed GLOB observation (same shape ApplyWeather builds).
    /// Cloud layers are not readable via simvars, so clouds are approximated.
    /// </summary>
    private static string BuildReconstructedMetar(in SnapshotCaptureStruct raw)
    {
        var dir = (int)Math.Round(raw.AmbientWindDirection / 10.0) * 10 % 360;
        if (dir < 0) dir += 360;
        var speed = Math.Clamp((int)Math.Round(raw.AmbientWindVelocity), 0, 99);

        string visibility;
        if (!double.IsFinite(raw.AmbientVisibilityM) || raw.AmbientVisibilityM >= 9999)
            visibility = "9999";
        else
            visibility = Math.Clamp((int)Math.Round(raw.AmbientVisibilityM / 100.0) * 100, 0, 9900)
                .ToString("0000");

        var mask = double.IsFinite(raw.AmbientPrecipState) ? (int)raw.AmbientPrecipState : 0;
        var rain = (mask & 4) != 0;
        var snow = (mask & 8) != 0;
        var wx = snow ? "SN " : rain ? "RA " : "";
        var clouds = wx.Length > 0 ? "OVC015" : "FEW035";

        var temp = double.IsFinite(raw.AmbientTempC)
            ? Math.Clamp((int)Math.Round(raw.AmbientTempC), -60, 60)
            : 15;
        var dew = temp - 2;
        static string FormatTemp(int value) => value < 0 ? $"M{Math.Abs(value):00}" : $"{value:00}";

        var qnh = double.IsFinite(raw.SeaLevelPressureMb)
            ? (int)Math.Round(raw.SeaLevelPressureMb)
            : 1013;
        if (qnh is < 800 or > 1100) qnh = 1013;

        return $"GLOB 010000Z {dir:000}{speed:00}KT {visibility} {wx}{clouds} " +
               $"{FormatTemp(temp)}/{FormatTemp(dew)} Q{qnh}";
    }

    // ------------------------------------------------------------------ restore

    public async Task<SpawnApplyResult> RestoreSnapshotAsync(
        FlightStateSnapshot snapshot,
        SnapshotRestoreOptions options,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        options ??= new SnapshotRestoreOptions();

        if (!IsConnected || _sim is null)
            throw new InvalidOperationException("Not connected to the simulator.");

        EnsureDefinitions();
        if (!_snapshotDefsRegistered)
            return SpawnApplyResult.Fail("Snapshot definitions unavailable — reconnect and try again.");

        progress?.Report("Checking aircraft…");
        var actualTitle = await RequestAircraftTitleAsync(ct);
        Log($"Snapshot restore (safe apply, no FlightLoad). Current TITLE='{actualTitle ?? "(unknown)"}'");

        var expectedTitles = string.IsNullOrWhiteSpace(snapshot.AircraftTitle)
            ? Array.Empty<string>()
            : new[] { snapshot.AircraftTitle };
        if (expectedTitles.Length > 0 && actualTitle is not null
            && !AircraftTitleMatches(actualTitle, expectedTitles))
        {
            Log($"Snapshot aircraft mismatch — aborting. sim='{actualTitle}' snapshot='{snapshot.AircraftTitle}'");
            throw new AircraftMismatchException(actualTitle, expectedTitles);
        }

        var spawn = SpawnFromSnapshot(snapshot);
        var result = SpawnApplyResult.Fail("Snapshot restore did not complete.");

        try
        {
            // Identical entry whether flying, on ground, ESC-paused or active-paused.
            await NormalizeLoadEntryAsync(ct);

            if (options.RestoreWeather && snapshot.Weather is not null)
            {
                progress?.Report("Applying weather (approx.)…");
                ApplyWeather(new WeatherConfig
                {
                    UseLiveWeather = false,
                    Metar = snapshot.Weather.ReconstructedMetar,
                    WindDirectionDeg = snapshot.Weather.WindDirDeg,
                    WindVelocityKts = snapshot.Weather.WindKts,
                    GustKts = 0,
                    VisibilitySm = (int)Math.Clamp(snapshot.Weather.VisibilityM / 1609.0, 1, 10)
                });
                await Task.Delay(250, ct);
            }

            progress?.Report(snapshot.OnGround ? "Placing aircraft on ground…" : "Positioning aircraft…");
            result = await ApplySnapshotSpawnAsync(snapshot, spawn, ct);

            if (result.Success)
            {
                if (options.RestoreTime && snapshot.Time is not null)
                {
                    // Clock AFTER teleport; zulu events are timezone-independent.
                    progress?.Report("Setting time…");
                    ApplyTimeOfDay(TimeOfDayFromSnapshot(snapshot));
                    await Task.Delay(200, ct);
                }

                if (options.RestoreFuel && snapshot.Fuel is not null && snapshot.Fuel.Tanks.Count > 0)
                {
                    progress?.Report("Restoring fuel…");
                    ApplyFuelSnapshot(snapshot.Fuel);
                }

                await RestoreConfigureAndSettleAsync(snapshot, spawn, progress, ct);

                if (options.RestoreEngines && snapshot.Engines is not null)
                {
                    progress?.Report("Restoring engine state…");
                    await ApplyEngineSnapshotAsync(snapshot.Engines, ct);
                }

                if (snapshot.Trim is not null)
                    ApplyTrimSnapshot(snapshot.Trim);

                if (options.RestoreLights && snapshot.Lights is not null)
                    ApplyLightsSnapshot(snapshot.Lights);

                if (options.RestoreAutopilot && snapshot.Autopilot is not null)
                {
                    progress?.Report("Restoring autopilot…");
                    await ApplyAutopilotSnapshotAsync(snapshot.Autopilot, ct);
                }

                // Re-pin after config so residual sink cannot pull us off the target.
                progress?.Report("Stabilizing…");
                await RePinSnapshotAsync(snapshot, spawn, ct);

                if (options.RestoreTime && snapshot.Time is not null)
                    ApplyTimeOfDay(TimeOfDayFromSnapshot(snapshot));

                await NormalizeSimRateAsync(ct);

                result = await VerifySnapshotAsync(snapshot, ct);
                if (!result.Success)
                    Log($"Snapshot post-config re-verify: {result.Message}");
            }
            else
            {
                progress?.Report("Positioning failed…");
                Log($"Snapshot spawn apply failed: {result.Message}");
            }
        }
        finally
        {
            // Release ordering from LoadScenarioAsync: hold SET PAUSE, final pose write,
            // only then release FREEZE. Unfreeze-before-pause = freefall / snap-to-ground.
            if (options.AutoResume)
            {
                EnsureActivePauseOff();
                try
                {
                    TeleportSnapshot(snapshot);
                    SetPoseDirect(spawn);
                    SetVelocitySnapshot(snapshot);
                }
                catch { /* best effort */ }

                FreezePose(false);
                PauseSim(false);
                PauseSim(false);
            }
            else
            {
                ForceSetPauseOn();
                try
                {
                    TeleportSnapshot(snapshot);
                    SetPoseDirect(spawn);
                    SetVelocitySnapshot(snapshot);
                }
                catch { /* best effort */ }

                ForceSetPauseOn();
                FreezePose(false);
                ForceSetPauseOn();
            }
        }

        if (result.Success)
        {
            progress?.Report(options.AutoResume
                ? "Snapshot restored — flying."
                : "Restored — PAUSED. Resume when ready.");
            Log(
                $"Snapshot restore complete. '{snapshot.Name}' · {(snapshot.OnGround ? "ground" : "air")} · " +
                $"alt {snapshot.AltitudeFeet:0} ft · ias {snapshot.IasKts:0} kt · gear " +
                $"{(snapshot.GearHandleDown ? "down" : "up")} · flaps {snapshot.FlapsHandleIndex} · " +
                $"verify horiz={result.HorizontalErrorM:0} m altErr={result.AltErrorFeet:0} ft.");
        }

        return result;
    }

    private static SpawnConfig SpawnFromSnapshot(FlightStateSnapshot snapshot) => new()
    {
        Latitude = snapshot.Latitude,
        Longitude = snapshot.Longitude,
        AltitudeFeet = snapshot.AltitudeFeet,
        HeadingDeg = snapshot.HeadingTrueDeg,
        AirspeedKts = snapshot.OnGround ? snapshot.GroundSpeedKts : snapshot.IasKts,
        PitchDeg = snapshot.PitchDeg,
        BankDeg = snapshot.BankDeg
    };

    private static TimeOfDayConfig TimeOfDayFromSnapshot(FlightStateSnapshot snapshot) => new()
    {
        UseZuluTime = true,
        Hour = snapshot.Time?.ZuluHour ?? 12,
        Minute = snapshot.Time?.ZuluMinute ?? 0,
        Year = snapshot.Time?.ZuluYear is > 1900 and < 2200 ? snapshot.Time.ZuluYear : null,
        DayOfYear = snapshot.Time?.ZuluDayOfYear is >= 1 and <= 366 ? snapshot.Time.ZuluDayOfYear : null
    };

    /// <summary>INITPOSITION honoring the snapshot's on-ground flag (Teleport hardcodes air).</summary>
    private void TeleportSnapshot(FlightStateSnapshot snapshot)
    {
        if (_sim is null) return;

        try
        {
            EnsureDefinitions();
            var airspeed = snapshot.OnGround ? snapshot.GroundSpeedKts : snapshot.IasKts;
            var init = new SIMCONNECT_DATA_INITPOSITION
            {
                Latitude = snapshot.Latitude,
                Longitude = snapshot.Longitude,
                Altitude = snapshot.AltitudeFeet,
                Pitch = snapshot.PitchDeg,
                Bank = snapshot.BankDeg,
                Heading = snapshot.HeadingTrueDeg,
                OnGround = snapshot.OnGround ? 1u : 0u,
                Airspeed = (uint)Math.Max(0, Math.Round(airspeed))
            };

            _sim.SetDataOnSimObject(
                Definitions.InitPosition,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                init);
        }
        catch (Exception ex)
        {
            Log($"TeleportSnapshot: {ex.Message}");
        }
    }

    /// <summary>Exact captured body velocity + rotation. Parked = all zeros.</summary>
    private void SetVelocitySnapshot(FlightStateSnapshot snapshot)
    {
        if (_sim is null) return;

        try
        {
            EnsureDefinitions();
            var stationary = snapshot.OnGround && snapshot.GroundSpeedKts < 2;
            var vel = stationary
                ? new VelocitySetStruct()
                : new VelocitySetStruct
                {
                    BodyX = snapshot.BodyVelXMs,
                    BodyY = snapshot.BodyVelYMs,
                    BodyZ = snapshot.BodyVelZMs,
                    RotX = snapshot.RotVelXRadS,
                    RotY = snapshot.RotVelYRadS,
                    RotZ = snapshot.RotVelZRadS
                };

            _sim.SetDataOnSimObject(
                Definitions.VelocitySet,
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT,
                vel);
        }
        catch (Exception ex)
        {
            Log($"SetVelocitySnapshot: {ex.Message}");
        }
    }

    private async Task PulseSnapshotAsync(
        FlightStateSnapshot snapshot,
        SpawnConfig spawn,
        int pulses,
        CancellationToken ct)
    {
        for (var i = 1; i <= pulses; i++)
        {
            TeleportSnapshot(snapshot);
            SetPoseDirect(spawn);
            SetVelocitySnapshot(snapshot);
            ReceiveMessage();
            await Task.Delay(280, ct);
        }
    }

    private async Task<SpawnApplyResult> ApplySnapshotSpawnAsync(
        FlightStateSnapshot snapshot,
        SpawnConfig spawn,
        CancellationToken ct)
    {
        EnsureEvents();
        EnsureDefinitions();

        Log(
            $"ApplySnapshotSpawn target lat={snapshot.Latitude:F5} lon={snapshot.Longitude:F5} " +
            $"alt={snapshot.AltitudeFeet:F0} ft hdg={snapshot.HeadingTrueDeg:F1}° " +
            $"onGround={snapshot.OnGround} ias={snapshot.IasKts:F0} gs={snapshot.GroundSpeedKts:F0}");

        ForceSetPauseOn();
        FreezePose(true);
        await Task.Delay(120, ct);

        await PulseSnapshotAsync(snapshot, spawn, SpawnPulseCount, ct);
        await Task.Delay(350, ct);

        var result = await VerifySnapshotAsync(snapshot, ct);
        if (result.Success)
            return result;

        Log($"Snapshot spawn verify failed (attempt 1): {result.Message} — retrying pulses…");
        await PulseSnapshotAsync(snapshot, spawn, SpawnPulseCount, ct);
        await Task.Delay(400, ct);
        result = await VerifySnapshotAsync(snapshot, ct);
        if (!result.Success)
            Log($"Snapshot spawn verify failed (attempt 2): {result.Message}");
        return result;
    }

    private async Task<SpawnApplyResult> VerifySnapshotAsync(
        FlightStateSnapshot snapshot,
        CancellationToken ct)
    {
        var sample = await RequestSnapshotStateOnceAsync(ct);
        if (sample is null)
            return SpawnApplyResult.Fail("Could not read back aircraft state after teleport.");

        var s = sample.Value;
        var horizM = HaversineMeters(snapshot.Latitude, snapshot.Longitude, s.Latitude, s.Longitude);
        var altErr = Math.Abs(s.AltitudeFeet - snapshot.AltitudeFeet);
        var onGround = s.SimOnGround > 0.5;
        var ias = s.IasKts;

        Log(
            $"Snapshot verify: lat={s.Latitude:F5} lon={s.Longitude:F5} alt={s.AltitudeFeet:F0} " +
            $"ias={ias:F0} onGround={onGround} horizErr={horizM:F0} m altErr={altErr:F0} ft");

        if (snapshot.OnGround)
        {
            // Ground: position + seated on wheels; altitude/IAS follow the terrain mesh.
            if (horizM > 150)
            {
                return SpawnApplyResult.Fail(
                    $"Aircraft still {horizM:0} m from stored position (ground limit 150 m).",
                    altErr, horizM, ias, onGround, s.Latitude, s.Longitude, s.AltitudeFeet);
            }

            if (!onGround)
            {
                return SpawnApplyResult.Fail(
                    "Aircraft not seated on the ground after ground restore. Try Load again.",
                    altErr, horizM, ias, onGround: false, s.Latitude, s.Longitude, s.AltitudeFeet);
            }

            return SpawnApplyResult.Ok(
                "Ground restore verified.", altErr, horizM, ias,
                s.Latitude, s.Longitude, s.AltitudeFeet, onGround: true);
        }

        if (horizM > MaxHorizontalErrorM)
        {
            return SpawnApplyResult.Fail(
                $"Aircraft still {horizM:0} m from stored position (limit {MaxHorizontalErrorM:0} m). Try Load again.",
                altErr, horizM, ias, onGround, s.Latitude, s.Longitude, s.AltitudeFeet);
        }

        if (altErr > MaxAltitudeErrorFeet)
        {
            return SpawnApplyResult.Fail(
                $"Altitude error {altErr:0} ft (limit {MaxAltitudeErrorFeet:0} ft).",
                altErr, horizM, ias, onGround, s.Latitude, s.Longitude, s.AltitudeFeet);
        }

        if (onGround && snapshot.AglFeet > 200)
        {
            return SpawnApplyResult.Fail(
                "Aircraft still reports on ground after airborne restore. Try Load once more.",
                altErr, horizM, ias, onGround: true, s.Latitude, s.Longitude, s.AltitudeFeet);
        }

        if (snapshot.IasKts >= 80 && ias < snapshot.IasKts * MinAirspeedFraction)
        {
            return SpawnApplyResult.Fail(
                $"Airspeed too low after restore ({ias:0} kt, expected ≥ {snapshot.IasKts * MinAirspeedFraction:0} kt).",
                altErr, horizM, ias, onGround, s.Latitude, s.Longitude, s.AltitudeFeet);
        }

        return SpawnApplyResult.Ok(
            "Snapshot position verified.", altErr, horizM, ias,
            s.Latitude, s.Longitude, s.AltitudeFeet, onGround);
    }

    /// <summary>
    /// Gear/flaps/spoilers/parking brake from the snapshot while SET-paused + frozen.
    /// Gear only moves after the aircraft is verified at the target — the sim is never
    /// asked to retract while it still thinks it is on a runway.
    /// </summary>
    private async Task RestoreConfigureAndSettleAsync(
        FlightStateSnapshot snapshot,
        SpawnConfig spawn,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        progress?.Report("Restoring gear, flaps, spoilers…");
        var started = DateTimeOffset.UtcNow;

        EnsureActivePauseOff();
        ForceSetPauseOn();
        FreezePose(true);
        await Task.Delay(120, ct);

        // Ground restores that need the wheels down get extra settle time — the classic
        // "sim must wait until the gear is out" case.
        var preSample = await RequestSnapshotStateOnceAsync(ct);
        var gearNeedsChange = snapshot.IsGearRetractable
                              && preSample is not null
                              && (preSample.Value.GearHandle > 0.5) != snapshot.GearHandleDown;
        var maxSettleMs = snapshot.OnGround && snapshot.GearHandleDown && gearNeedsChange
            ? MaxGearSettleMs
            : MaxConfigSettleMs;

        if (snapshot.SpoilersHandle01 <= 0.05)
        {
            // Clear lever↔surface desync before commanding the stowed position.
            TeleportSnapshot(snapshot);
            SetPoseDirect(spawn);
            SetVelocitySnapshot(snapshot);
            ForceSetPauseOn();
            FreezePose(true);
            await ResyncSpoilersCycleAsync(ct);
            ForceSetPauseOn();
            FreezePose(true);
        }

        for (var i = 1; i <= ConfigPulseCount; i++)
        {
            TeleportSnapshot(snapshot);
            SetPoseDirect(spawn);
            SetVelocitySnapshot(snapshot);
            ApplySnapshotConfigCommands(snapshot);
            ForceSetPauseOn();
            FreezePose(true);
            Log($"Snapshot config pulse {i}/{ConfigPulseCount} " +
                $"(gear={(snapshot.GearHandleDown ? "down" : "up")} flaps={snapshot.FlapsHandleIndex} " +
                $"spoilers={snapshot.SpoilersHandle01:0%} parkBrake={snapshot.ParkingBrakeOn})");
            await Task.Delay(ConfigPulseDelayMs, ct);
        }

        progress?.Report("Waiting for aircraft to settle…");

        var matched = false;
        string? lastDetail = null;
        var poll = 0;
        while ((DateTimeOffset.UtcNow - started).TotalMilliseconds < maxSettleMs)
        {
            poll++;
            ForceSetPauseOn();
            FreezePose(true);
            if (poll % 2 == 0)
            {
                SetPoseDirect(spawn);
                SetVelocitySnapshot(snapshot);
            }

            var elapsed = (DateTimeOffset.UtcNow - started).TotalMilliseconds;
            var sample = await RequestSnapshotStateOnceAsync(ct);
            if (sample is not null)
            {
                matched = SnapshotConfigMatches(snapshot, sample.Value, out lastDetail);

                var driftedToGround = !snapshot.OnGround && sample.Value.SimOnGround > 0.5;
                var altErr = Math.Abs(sample.Value.AltitudeFeet - snapshot.AltitudeFeet);
                if (driftedToGround || (!snapshot.OnGround && altErr > MaxAltitudeErrorFeet))
                {
                    Log($"Snapshot config drift: onGround={sample.Value.SimOnGround > 0.5} altErr={altErr:0} — re-pinning");
                    TeleportSnapshot(snapshot);
                    SetPoseDirect(spawn);
                    SetVelocitySnapshot(snapshot);
                    FreezePose(true);
                }

                if (matched && elapsed >= MinConfigSettleMs)
                {
                    Log($"Snapshot config settled OK after {elapsed:0} ms — {lastDetail}");
                    break;
                }
            }

            await Task.Delay(ConfigPollMs, ct);
        }

        ApplySnapshotConfigCommands(snapshot);
        ForceSetPauseOn();
        FreezePose(true);

        if (matched)
        {
            Log($"Snapshot config verify OK: {lastDetail}");
        }
        else
        {
            // Soft-fail (existing philosophy): surfaces may finish moving after resume.
            progress?.Report("Config timeout — check gear/surfaces after resume.");
            Log($"Snapshot config soft-fail after settle: {lastDetail ?? "no telemetry"}");
        }
    }

    private void ApplySnapshotConfigCommands(FlightStateSnapshot snapshot)
    {
        if (_sim is null) return;

        try
        {
            EnsureEvents();

            if (snapshot.IsGearRetractable)
                CommandGear(snapshot.GearHandleDown);

            // Scale by the captured handle count instead of assuming 4 detents.
            var detents = Math.Max(1, snapshot.FlapsHandleCount);
            var index = Math.Clamp(snapshot.FlapsHandleIndex, 0, detents);
            var flapsValue = (uint)Math.Clamp((int)Math.Round(16383.0 * index / detents), 0, 16383);
            _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.FlapsSet, flapsValue,
                Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);

            if (snapshot.SpoilersHandle01 <= 0.05)
            {
                CommandSpoilersFullyRetracted();
            }
            else
            {
                var spoilerValue = (uint)Math.Clamp(
                    (int)Math.Round(snapshot.SpoilersHandle01 * 16383.0), 0, 16383);
                _sim.TransmitClientEvent(MsfsSc.SIMCONNECT_OBJECT_ID_USER, Events.SpoilersSet, spoilerValue,
                    Groups.Input, SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
            }

            _sim.TransmitClientEvent(
                MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                Events.ParkingBrakeSet,
                snapshot.ParkingBrakeOn ? 1u : 0u,
                Groups.Input,
                SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
        }
        catch (Exception ex)
        {
            Log($"ApplySnapshotConfigCommands: {ex.Message}");
        }
    }

    private static bool SnapshotConfigMatches(
        FlightStateSnapshot snapshot,
        in SnapshotCaptureStruct s,
        out string detail)
    {
        var gearDown = s.GearHandle > 0.5;
        var flaps = (int)Math.Round(s.FlapsIndex);
        var spoilersOut = Math.Max(s.SpoilersLeft, s.SpoilersRight) > 0.15;
        var wantSpoilersOut = snapshot.SpoilersHandle01 > 0.15;
        var parkOn = s.ParkingBrake > 0.5;

        var gearOk = !snapshot.IsGearRetractable || gearDown == snapshot.GearHandleDown;
        var flapsOk = flaps == Math.Clamp(snapshot.FlapsHandleIndex, 0, Math.Max(1, snapshot.FlapsHandleCount));
        var spoilersOk = wantSpoilersOut || !spoilersOut;
        var parkOk = parkOn == snapshot.ParkingBrakeOn;

        detail =
            $"gear={(gearDown ? "down" : "up")}({(gearOk ? "ok" : "want " + (snapshot.GearHandleDown ? "down" : "up"))}) " +
            $"gearPct={s.GearTotalPctExtended:0%} " +
            $"flaps={flaps}({(flapsOk ? "ok" : "want " + snapshot.FlapsHandleIndex)}) " +
            $"spoilers={Math.Max(s.SpoilersLeft, s.SpoilersRight):0%}({(spoilersOk ? "ok" : "want in")}) " +
            $"park={(parkOn ? "on" : "off")}({(parkOk ? "ok" : "want " + (snapshot.ParkingBrakeOn ? "on" : "off"))})";

        return gearOk && flapsOk && spoilersOk && parkOk;
    }

    private async Task RePinSnapshotAsync(
        FlightStateSnapshot snapshot,
        SpawnConfig spawn,
        CancellationToken ct)
    {
        ForceSetPauseOn();
        FreezePose(true);
        await PulseSnapshotAsync(snapshot, spawn, pulses: 3, ct);
        await Task.Delay(200, ct);
        ForceSetPauseOn();
        FreezePose(true);
    }

    // ------------------------------------------------------------------ extras

    private void ApplyFuelSnapshot(SnapshotFuel fuel)
    {
        if (_sim is null || !_snapshotDefsRegistered) return;

        try
        {
            var values = new double[FuelTankNames.Length];
            for (var i = 0; i < FuelTankNames.Length; i++)
                values[i] = fuel.Tanks.TryGetValue(FuelTankNames[i], out var gallons)
                    ? Math.Max(0, gallons)
                    : 0;

            var data = new FuelSetStruct
            {
                Tank1 = values[0], Tank2 = values[1], Tank3 = values[2],
                Tank4 = values[3], Tank5 = values[4], Tank6 = values[5],
                Tank7 = values[6], Tank8 = values[7], Tank9 = values[8],
                Tank10 = values[9], Tank11 = values[10]
            };
            _sim.SetDataOnSimObject(
                Definitions.FuelSet, MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
            Log($"Fuel restored: {fuel.TotalGallons:0} gal across {fuel.Tanks.Count} tank(s).");
        }
        catch (Exception ex)
        {
            Log($"ApplyFuelSnapshot: {ex.Message}");
        }
    }

    private void ApplyTrimSnapshot(SnapshotTrim trim)
    {
        if (_sim is null || !_snapshotDefsRegistered) return;

        try
        {
            var data = new TrimSetStruct
            {
                ElevatorTrimRad = trim.ElevatorTrimRad,
                AileronTrimPct = trim.AileronTrimPct01,
                RudderTrimPct = trim.RudderTrimPct01
            };
            _sim.SetDataOnSimObject(
                Definitions.TrimSet, MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
        }
        catch (Exception ex)
        {
            Log($"ApplyTrimSnapshot: {ex.Message}");
        }
    }

    private void ApplyLightsSnapshot(SnapshotLights lights)
    {
        if (_sim is null || !_snapshotDefsRegistered) return;

        try
        {
            var data = new LightsSetStruct
            {
                Beacon = lights.Beacon ? 1 : 0,
                Landing = lights.Landing ? 1 : 0,
                Taxi = lights.Taxi ? 1 : 0,
                Nav = lights.Nav ? 1 : 0,
                Strobe = lights.Strobe ? 1 : 0,
                Panel = lights.Panel ? 1 : 0,
                Recognition = lights.Recognition ? 1 : 0,
                Wing = lights.Wing ? 1 : 0,
                Logo = lights.Logo ? 1 : 0,
                Cabin = lights.Cabin ? 1 : 0
            };
            _sim.SetDataOnSimObject(
                Definitions.LightsSet, MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, data);
        }
        catch (Exception ex)
        {
            Log($"ApplyLightsSnapshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Throttle always; mixture/prop for pistons/turboprops; combustion only on mismatch
    /// (stock aircraft honor the settable simvar; complex addons may ignore it).
    /// </summary>
    private async Task ApplyEngineSnapshotAsync(SnapshotEngines engines, CancellationToken ct)
    {
        if (_sim is null || !_snapshotDefsRegistered) return;

        try
        {
            var current = await RequestSnapshotStateOnceAsync(ct);
            if (current is not null)
            {
                var live = new[]
                {
                    current.Value.Combustion1 > 0.5, current.Value.Combustion2 > 0.5,
                    current.Value.Combustion3 > 0.5, current.Value.Combustion4 > 0.5
                };
                var mismatch = false;
                for (var i = 0; i < Math.Min(4, Math.Max(1, engines.Count)); i++)
                    mismatch |= live[i] != engines.Combustion[i];

                if (mismatch)
                {
                    Log("Engine running-state mismatch — setting combustion (best effort).");
                    var combustion = new CombustionSetStruct
                    {
                        Combustion1 = engines.Combustion[0] ? 1 : 0,
                        Combustion2 = engines.Combustion[1] ? 1 : 0,
                        Combustion3 = engines.Combustion[2] ? 1 : 0,
                        Combustion4 = engines.Combustion[3] ? 1 : 0
                    };
                    _sim.SetDataOnSimObject(
                        Definitions.CombustionSet, MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                        SIMCONNECT_DATA_SET_FLAG.DEFAULT, combustion);
                    await Task.Delay(300, ct);
                }
            }

            var throttle = new ThrottleSetStruct
            {
                Throttle1 = engines.ThrottleLeverPct[0],
                Throttle2 = engines.ThrottleLeverPct[1],
                Throttle3 = engines.ThrottleLeverPct[2],
                Throttle4 = engines.ThrottleLeverPct[3]
            };
            _sim.SetDataOnSimObject(
                Definitions.ThrottleSet, MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                SIMCONNECT_DATA_SET_FLAG.DEFAULT, throttle);

            // ENGINE TYPE: 0 piston, 5 turboprop — the ones with mixture/prop levers.
            if (engines.EngineType is 0 or 5)
            {
                var pistons = new PistonControlsSetStruct
                {
                    Mixture1 = engines.MixtureLeverPct[0],
                    Mixture2 = engines.MixtureLeverPct[1],
                    Mixture3 = engines.MixtureLeverPct[2],
                    Mixture4 = engines.MixtureLeverPct[3],
                    PropLever1 = engines.PropellerLeverPct[0],
                    PropLever2 = engines.PropellerLeverPct[1],
                    PropLever3 = engines.PropellerLeverPct[2],
                    PropLever4 = engines.PropellerLeverPct[3]
                };
                _sim.SetDataOnSimObject(
                    Definitions.PistonControlsSet, MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                    SIMCONNECT_DATA_SET_FLAG.DEFAULT, pistons);
            }
        }
        catch (Exception ex)
        {
            Log($"ApplyEngineSnapshot: {ex.Message}");
        }
    }

    /// <summary>
    /// Restore autopilot: reference values FIRST (bug/alt/speed/VS), then engage modes so
    /// engagement cannot sync targets to the current state, then re-assert the values.
    /// Toggle-only controls (FD, A/THR arm) fire only on a read-back mismatch.
    /// Standard-interface only — custom FMGC/FBW addons honor this best-effort.
    /// </summary>
    private async Task ApplyAutopilotSnapshotAsync(SnapshotAutopilot ap, CancellationToken ct)
    {
        if (_sim is null) return;

        try
        {
            EnsureSnapshotEvents();

            void SetTargets()
            {
                var headingBug = (uint)(((int)Math.Round(ap.HeadingBugDeg) % 360 + 360) % 360);
                TransmitSnapshotEvent(Events.HeadingBugSet, headingBug);
                TransmitSnapshotEvent(Events.ApAltVarSet,
                    (uint)Math.Clamp(Math.Round(ap.AltitudeTargetFeet), 0, 99_000));
                TransmitSnapshotEvent(Events.ApSpdVarSet,
                    (uint)Math.Clamp(Math.Round(ap.AirspeedTargetKts), 0, 999));
                TransmitSnapshotEvent(Events.ApMachVarSet,
                    (uint)Math.Clamp(Math.Round(ap.MachTarget * 100.0), 0, 99));
                // Signed fpm travels as a raw DWORD.
                TransmitSnapshotEvent(Events.ApVsVarSet,
                    unchecked((uint)(int)Math.Clamp(Math.Round(ap.VerticalSpeedTargetFpm), -9_000, 9_000)));
            }

            SetTargets();
            await Task.Delay(150, ct);

            // Toggle-only controls need the live state to decide.
            var current = await RequestSnapshotStateOnceAsync(ct);
            if (current is not null)
            {
                if ((current.Value.ApFlightDirector > 0.5) != ap.FlightDirector)
                    TransmitSnapshotEvent(Events.ToggleFlightDirector);
                if ((current.Value.ApThrottleArm > 0.5) != ap.AutothrottleArmed)
                    TransmitSnapshotEvent(Events.AutoThrottleArm);
            }

            TransmitSnapshotEvent(ap.Master ? Events.AutopilotOn : Events.AutopilotOff);
            TransmitSnapshotEvent(ap.YawDamper ? Events.YawDamperOn : Events.YawDamperOff);

            // Lateral: NAV wins over heading select; explicit OFF is set-style and safe.
            TransmitSnapshotEvent(ap.Nav1Lock ? Events.ApNav1HoldOn : Events.ApNav1HoldOff);
            TransmitSnapshotEvent(ap.HeadingLock ? Events.ApHdgHoldOn : Events.ApHdgHoldOff);

            // Vertical.
            TransmitSnapshotEvent(ap.AltitudeLock ? Events.ApAltHoldOn : Events.ApAltHoldOff);
            if (ap.VerticalSpeedHold)
                TransmitSnapshotEvent(Events.ApPanelVsOn);
            if (ap.FlightLevelChange)
                TransmitSnapshotEvent(Events.FlightLevelChangeOn);
            TransmitSnapshotEvent(ap.ApproachHold ? Events.ApAprHoldOn : Events.ApAprHoldOff);

            // Speed.
            if (ap.MachHold)
                TransmitSnapshotEvent(Events.ApMachOn);
            else if (ap.AirspeedHold)
                TransmitSnapshotEvent(Events.ApAirspeedOn);

            await Task.Delay(250, ct);
            SetTargets();

            Log(
                $"Autopilot restored: master={ap.Master} FD={ap.FlightDirector} A/THR={ap.AutothrottleArmed} " +
                $"lat={(ap.Nav1Lock ? "NAV" : ap.HeadingLock ? $"HDG {ap.HeadingBugDeg:0}°" : "—")} " +
                $"vert={(ap.AltitudeLock ? $"ALT {ap.AltitudeTargetFeet:0} ft" : ap.VerticalSpeedHold ? $"VS {ap.VerticalSpeedTargetFpm:0} fpm" : ap.FlightLevelChange ? "FLC" : "—")} " +
                $"spd={(ap.MachHold ? $"M{ap.MachTarget:0.00}" : ap.AirspeedHold ? $"{ap.AirspeedTargetKts:0} kt" : "—")} " +
                $"apr={ap.ApproachHold} (standard interface; addon FMGC modes best-effort).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log($"ApplyAutopilotSnapshot: {ex.Message}");
        }
    }

    /// <summary>Bring SIMULATION RATE back to 1x (restore never keeps 2x/4x).</summary>
    private async Task NormalizeSimRateAsync(CancellationToken ct)
    {
        if (_sim is null) return;

        try
        {
            for (var attempt = 0; attempt < 6; attempt++)
            {
                var sample = await RequestSnapshotStateOnceAsync(ct);
                if (sample is null) return;

                var rate = sample.Value.SimulationRate;
                if (!double.IsFinite(rate) || Math.Abs(rate - 1.0) < 0.01) return;

                _sim.TransmitClientEvent(
                    MsfsSc.SIMCONNECT_OBJECT_ID_USER,
                    rate > 1.0 ? Events.SimRateDecr : Events.SimRateIncr,
                    0,
                    Groups.Input,
                    SIMCONNECT_EVENT_FLAG.GROUPID_IS_PRIORITY);
                Log($"Sim rate {rate:0.##}x → nudging toward 1x.");
                await Task.Delay(250, ct);
            }
        }
        catch (Exception ex)
        {
            Log($"NormalizeSimRate: {ex.Message}");
        }
    }
}
