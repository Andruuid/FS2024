using System.Globalization;
using System.Text;
using ChallengeLab.Core.Config;

namespace ChallengeLab.Core.Scenarios;

/// <summary>
/// Builds a minimal MSFS free-flight .FLT from challenge JSON (spawn, aircraft title,
/// gear/flaps, time). Used for review, publish artifacts, and optional freeflight prep —
/// not for mid-session FlightLoad aircraft swaps (unsafe in MSFS 2024).
/// </summary>
public static class FltScenarioBuilder
{
    /// <summary>UTF-8 with BOM — matches real MSFS flight files.</summary>
    public static Encoding FltFileEncoding { get; } = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// Official MSFS 2024 free-flight slot. Only write here via explicit prep APIs —
    /// never as part of an automatic mid-session aircraft swap.
    /// </summary>
    public static string? TryGetMsfsCustomFlightPath()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft Flight Simulator 2024",
            "MISSIONS",
            "Custom",
            "CustomFlight",
            "CustomFlight.FLT");
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
            return null;
        return path;
    }

    /// <summary>
    /// Write a generated .FLT for this challenge under LocalAppData\ChallengeLab\generated
    /// (or <paramref name="outputDirectory"/>). Returns the absolute path.
    /// </summary>
    public static string Write(ChallengeConfig challenge, string? outputDirectory = null)
    {
        var dir = outputDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChallengeLab",
            "generated");
        Directory.CreateDirectory(dir);

        var safeId = string.IsNullOrWhiteSpace(challenge.Id) ? "challenge" : challenge.Id;
        foreach (var c in Path.GetInvalidFileNameChars())
            safeId = safeId.Replace(c, '_');

        var path = Path.Combine(dir, $"{safeId}.FLT");
        WriteFltFile(path, BuildContent(challenge));
        return Path.GetFullPath(path);
    }

    /// <summary>Write FLT text using MSFS-friendly encoding.</summary>
    public static void WriteFltFile(string absolutePath, string content)
    {
        var dir = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(absolutePath, content, FltFileEncoding);
    }

    /// <summary>
    /// Resolve a debug/artifact .FLT path: optional hand override, else generate minimal
    /// content under LocalAppData. Does <b>not</b> overwrite MSFS CustomFlight.
    /// </summary>
    public static (string Path, bool Generated) ResolveFlightFile(
        ChallengeConfig challenge,
        Func<string, string>? resolveOverridePath = null,
        bool useMsfsCustomFlightSlot = false)
    {
        // useMsfsCustomFlightSlot intentionally ignored (always false behavior).
        // Mid-session CustomFlight overwrite + FlightLoad caused MSFS 2024 CTDs.
        _ = useMsfsCustomFlightSlot;

        if (!string.IsNullOrWhiteSpace(challenge.FlightFile))
        {
            var resolved = resolveOverridePath is not null
                ? resolveOverridePath(challenge.FlightFile)
                : challenge.FlightFile;
            if (File.Exists(resolved))
                return (Path.GetFullPath(resolved), Generated: false);
        }

        return (Write(challenge), Generated: true);
    }

    /// <summary>
    /// Optionally write a <b>minimal</b> scenario into CustomFlight with backup.
    /// For user-driven freeflight prep only — caller must not FlightLoad mid-session for swaps.
    /// </summary>
    public static string? PrepareCustomFlightWithBackup(ChallengeConfig challenge)
    {
        var customPath = TryGetMsfsCustomFlightPath();
        if (customPath is null)
            return null;

        if (File.Exists(customPath))
        {
            var bak = customPath + ".challengelab.bak";
            File.Copy(customPath, bak, overwrite: true);
        }

        WriteFltFile(customPath, BuildContent(challenge));
        return Path.GetFullPath(customPath);
    }

    /// <summary>Build minimal FLT text (for tests and artifacts).</summary>
    public static string BuildContent(ChallengeConfig challenge)
    {
        var spawn = challenge.Spawn ?? new SpawnConfig();
        var setup = challenge.AircraftSetup ?? new AircraftSetupConfig();
        var aircraft = challenge.AircraftTitles.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
                       ?? "A330-200 (RR)";
        var livery = challenge.AircraftLivery?.Trim() ?? "";

        var ias = Math.Max(0, spawn.AirspeedKts);
        var zVel = ias;
        var gear = setup.GearDown ? 1 : 0;
        var flapsPct = Math.Clamp(setup.FlapsHandleIndex, 0, 5) / 4.0 * 100.0;
        if (flapsPct > 100) flapsPct = 100;

        var title = AsciiSafe(string.IsNullOrWhiteSpace(challenge.Title)
            ? "Challenge Lab"
            : challenge.Title);
        var desc = AsciiSafe(string.IsNullOrWhiteSpace(challenge.Description)
            ? "Generated from challenge JSON"
            : challenge.Description.Replace('\r', ' ').Replace('\n', ' '));
        if (desc.Length > 200) desc = desc[..200];

        var location = challenge.Runway?.AirportIcao;
        if (string.IsNullOrWhiteSpace(location))
            location = "Challenge";

        var sb = new StringBuilder();
        sb.AppendLine("[Main]");
        sb.AppendLine($"Title=Challenge Lab - {title}");
        sb.AppendLine($"Description={desc}");
        sb.AppendLine("AppVersion=12.0.1");
        sb.AppendLine("FlightVersion=1");
        sb.AppendLine("MissionType=FreeFlight");
        sb.AppendLine($"MissionLocation={location}");
        sb.AppendLine("OriginalFlight=");
        sb.AppendLine("FlightType=NORMAL");
        sb.AppendLine("StartingCameraCategory=Cockpit");
        sb.AppendLine();
        sb.AppendLine("[Options]");
        sb.AppendLine("Sound=True");
        sb.AppendLine("Moonlight=False");
        sb.AppendLine("Save=False");
        sb.AppendLine("SaveOriginalFlightPlan=False");
        sb.AppendLine("TextDisplayPage=0");
        sb.AppendLine("SlewDisplayPage=0");
        sb.AppendLine("AxisIndicator=Off");
        sb.AppendLine("Titles=False");
        sb.AppendLine();
        sb.AppendLine("[Sim.0]");
        sb.AppendLine($"Sim={aircraft}");
        sb.AppendLine($"Livery={livery}");
        sb.AppendLine("Pilot=");
        sb.AppendLine("Copilot=");
        sb.AppendLine("TailNumber=CLAB1");
        sb.AppendLine("AirlineCallSign=CHALLENGE");
        sb.AppendLine("FlightNumber=1");
        sb.AppendLine("AppendHeavy=True");
        sb.AppendLine("DisableMassSections=False");
        sb.AppendLine();
        sb.AppendLine("[ObjectFile]");
        sb.AppendLine(@"File.0=Missions\Asobo\FreeFlights\FreeFlight\FreeFlight");
        sb.AppendLine();
        sb.AppendLine("[FreeFlight]");
        sb.AppendLine("FirstFlightState=FLIGHT_LANDING");
        sb.AppendLine();
        sb.AppendLine("[Weather]");
        sb.AppendLine("UseWeatherFile=False");
        sb.AppendLine($"UseLiveWeather={(challenge.Weather?.UseLiveWeather == true ? "True" : "False")}");
        sb.AppendLine("WeatherCanBeLive=False");
        sb.AppendLine("FixedClouds=False");
        sb.AppendLine();
        AppendDateTimeSeason(sb, challenge.TimeOfDay);
        sb.AppendLine("[Panels]");
        sb.AppendLine("Panel.On=True");
        sb.AppendLine("HUD.On=False");
        sb.AppendLine();
        sb.AppendLine("[SimVars.0]");
        sb.AppendLine($"Latitude={FormatLatitude(spawn.Latitude)}");
        sb.AppendLine($"Longitude={FormatLongitude(spawn.Longitude)}");
        sb.AppendLine($"Altitude={FormatAltitude(spawn.AltitudeFeet)}");
        sb.AppendLine($"Pitch={spawn.PitchDeg.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Bank={spawn.BankDeg.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Heading={spawn.HeadingDeg.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine("PVelBodyAxis=0");
        sb.AppendLine("BVelBodyAxis=0");
        sb.AppendLine("HVelBodyAxis=0");
        sb.AppendLine("XVelBodyAxis=0");
        sb.AppendLine("YVelBodyAxis=0");
        sb.AppendLine($"ZVelBodyAxis={zVel.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"ZVelBodyAxis_IAS={ias.ToString("0.###", CultureInfo.InvariantCulture)}");
        sb.AppendLine("SimOnGround=False");
        sb.AppendLine("OnPlatformHeight=-9999999999");
        sb.AppendLine();
        sb.AppendLine("[Avionics.0]");
        sb.AppendLine("Comm1Active=118.100");
        sb.AppendLine("Comm1Standby=121.800");
        sb.AppendLine("Comm2Active=121.800");
        sb.AppendLine("Comm2Standby=121.800");
        sb.AppendLine("Nav1Active=110.30");
        sb.AppendLine("Nav1Standby=113.90");
        sb.AppendLine($"OBS1={Math.Round(spawn.HeadingDeg):0}");
        sb.AppendLine("Nav2Active=110.30");
        sb.AppendLine("Nav2Standby=113.90");
        sb.AppendLine($"OBS2={Math.Round(spawn.HeadingDeg):0}");
        sb.AppendLine("Transponder=2000");
        sb.AppendLine("TransponderState=4");
        sb.AppendLine("AvionicsSwitch=True");
        sb.AppendLine();
        sb.AppendLine("[Slew.0]");
        sb.AppendLine("Active=False");
        sb.AppendLine();
        sb.AppendLine("[Freeze.0]");
        sb.AppendLine("Location=False");
        sb.AppendLine("Altitude=False");
        sb.AppendLine("Attitude=False");
        sb.AppendLine();
        sb.AppendLine("[Systems.0]");
        sb.AppendLine("Battery=True");
        sb.AppendLine("StructuralDeice=False");
        sb.AppendLine("PropDeice=False");
        sb.AppendLine("Autobrakes=3");
        sb.AppendLine("StandbyVacuum=False");
        sb.AppendLine("PropSync=False");
        sb.AppendLine("AutoFeather=False");
        sb.AppendLine("FlightDirector=True");
        sb.AppendLine("PanelLights=False");
        sb.AppendLine("LaunchBarSwitch=False");
        sb.AppendLine("LaunchBarState=0");
        sb.AppendLine("TailhookHandle=False");
        sb.AppendLine("TailhookState=0");
        sb.AppendLine("FoldingWingsHandle=False");
        sb.AppendLine("FoldingWingsState=0, 0");
        sb.AppendLine("ExternalPowerSwitch=False");
        sb.AppendLine("AuxPowerUnitSwitch=False");
        sb.AppendLine("AileronTrimPct=0");
        sb.AppendLine("RudderTrimPct=0");
        sb.AppendLine("GForceSettle=True");
        sb.AppendLine();
        sb.AppendLine("[Controls.0]");
        sb.AppendLine("SpoilersHandle=000.00");
        sb.AppendLine($"FlapsHandle={flapsPct.ToString("000.00", CultureInfo.InvariantCulture)}");
        sb.AppendLine($"GearHandle={gear}");
        sb.AppendLine("BrakeLeft=0");
        sb.AppendLine("BrakeRight=0");
        sb.AppendLine("ParkingBrake=0");
        sb.AppendLine("YokeX=0");
        sb.AppendLine("YokeY=0");
        sb.AppendLine("Rudder=0");
        sb.AppendLine("Throttle=45");
        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Emit [DateTimeSeason] so freeflight-from-menu loads can set a fixed clock.
    /// </summary>
    public static void AppendDateTimeSeason(StringBuilder sb, TimeOfDayConfig? timeOfDay)
    {
        var tod = timeOfDay ?? new TimeOfDayConfig();
        var hour = Math.Clamp(tod.Hour, 0, 23);
        var minute = Math.Clamp(tod.Minute, 0, 59);
        var year = tod.Year is > 1900 and < 2200 ? tod.Year.Value : DateTime.Now.Year;
        var day = tod.DayOfYear is >= 1 and <= 366 ? tod.DayOfYear.Value : 180;
        var useZulu = tod.UseZuluTime;

        sb.AppendLine("[DateTimeSeason]");
        sb.AppendLine("Season=Summer");
        sb.AppendLine($"Year={year}");
        sb.AppendLine($"Day={day}");
        sb.AppendLine($"Hours={hour}");
        sb.AppendLine($"Minutes={minute}");
        sb.AppendLine("Seconds=0");
        sb.AppendLine($"UseZuluTime={(useZulu ? "True" : "False")}");
        sb.AppendLine();
    }

    public static string FormatLatitude(double lat)
    {
        var hemi = lat >= 0 ? "N" : "S";
        return FormatDms(Math.Abs(lat), hemi);
    }

    public static string FormatLongitude(double lon)
    {
        var hemi = lon >= 0 ? "E" : "W";
        return FormatDms(Math.Abs(lon), hemi);
    }

    public static string FormatAltitude(double feet)
    {
        var sign = feet >= 0 ? "+" : "-";
        return sign + Math.Abs(feet).ToString("000000.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Strip characters that break FLT metadata lines.</summary>
    public static string AsciiSafe(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case '\u2014':
                case '\u2013':
                case '\u2212':
                    sb.Append('-');
                    break;
                case '\u2018':
                case '\u2019':
                case '\u201C':
                case '\u201D':
                    sb.Append('\'');
                    break;
                case '\u00B0':
                    sb.Append(ch);
                    break;
                default:
                    if (ch < 32 && ch is not '\t')
                        sb.Append(' ');
                    else if (ch <= 255)
                        sb.Append(ch);
                    else
                        sb.Append('?');
                    break;
            }
        }
        return sb.ToString();
    }

    private static string FormatDms(double absDeg, string hemi)
    {
        var deg = (int)Math.Floor(absDeg);
        var minFloat = (absDeg - deg) * 60.0;
        var min = (int)Math.Floor(minFloat);
        var sec = (minFloat - min) * 60.0;
        return string.Create(CultureInfo.InvariantCulture,
            $"{hemi}{deg}\u00B0 {min}' {sec:0.00}\"");
    }
}
