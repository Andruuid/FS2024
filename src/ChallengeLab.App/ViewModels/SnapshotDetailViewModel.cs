using System.Globalization;
using ChallengeLab.Core.Snapshots;

namespace ChallengeLab.App.ViewModels;

/// <summary>
/// Read-only STORE tab detail for a selected <see cref="FlightStateSnapshot"/>.
/// Built once on selection so the XAML only binds simple strings.
/// </summary>
public sealed class SnapshotDetailViewModel
{
    public SnapshotDetailViewModel(FlightStateSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        Title = string.IsNullOrWhiteSpace(snapshot.Name) ? "Unnamed snapshot" : snapshot.Name.Trim();
        Subtitle = snapshot.CreatedUtc.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        Aircraft = string.IsNullOrWhiteSpace(snapshot.AircraftTitle) ? "—" : snapshot.AircraftTitle.Trim();
        Airport = snapshot.Airport?.Label is { Length: > 0 } label ? label : "—";
        Situation = snapshot.OnGround ? "On ground" : "Airborne";

        Ias = FormatKts(snapshot.IasKts);
        Tas = FormatKts(snapshot.TasKts);
        GroundSpeed = FormatKts(snapshot.GroundSpeedKts);
        VerticalSpeed = FormatFpm(snapshot.VerticalSpeedFpm);
        AltitudeMsl = FormatFeet(snapshot.AltitudeFeet) + " MSL";
        AltitudeAgl = FormatFeet(snapshot.AglFeet) + " AGL";
        Heading = FormatDeg(snapshot.HeadingTrueDeg) + " true";
        Pitch = FormatDeg(snapshot.PitchDeg);
        Bank = FormatDeg(snapshot.BankDeg);

        Gear = snapshot.IsGearRetractable
            ? (snapshot.GearHandleDown
                ? $"Down ({snapshot.GearTotalPctExtended:P0} extended)"
                : $"Up ({snapshot.GearTotalPctExtended:P0} extended)")
            : "Fixed gear";
        Flaps = snapshot.FlapsHandleCount > 0
            ? $"Index {snapshot.FlapsHandleIndex} / {Math.Max(0, snapshot.FlapsHandleCount - 1)} " +
              $"(of {snapshot.FlapsHandleCount} positions)"
            : $"Index {snapshot.FlapsHandleIndex}";
        Spoilers = snapshot.SpoilersHandle01 < 0.02
            ? "Stowed"
            : $"Handle {snapshot.SpoilersHandle01:P0} · L {snapshot.SpoilersLeft01:P0} / R {snapshot.SpoilersRight01:P0}";
        ParkingBrake = snapshot.ParkingBrakeOn ? "On" : "Off";

        var ap = snapshot.Autopilot;
        if (ap is null)
        {
            AutopilotMaster = "—";
            FlightDirector = "—";
            Autothrust = "—";
            AutothrustTarget = "—";
            NavMode = "—";
            HeadingMode = "—";
            HeadingBug = "—";
            AltitudeMode = "—";
            AltitudeTarget = "—";
            VerticalMode = "—";
            VerticalTarget = "—";
            SpeedMode = "—";
            SpeedTarget = "—";
            YawDamper = "—";
        }
        else
        {
            AutopilotMaster = OnOff(ap.Master);
            FlightDirector = OnOff(ap.FlightDirector);
            Autothrust = ap.ManagedThrottleActive
                ? "Active (managed)"
                : ap.AutothrottleArmed
                    ? "Armed"
                    : "Off";
            AutothrustTarget = ap.AirspeedHold
                ? $"{ap.AirspeedTargetKts:0.#} kt"
                : ap.MachHold
                    ? $"M {ap.MachTarget:0.000}"
                    : "—";
            NavMode = ap.ApproachHold
                ? (ap.GlideslopeHold ? "Approach + GS" : "Approach")
                : ap.Nav1Lock
                    ? "NAV"
                    : ap.HeadingLock
                        ? "HDG"
                        : "Off";
            HeadingMode = ap.HeadingLock ? "On" : "Off";
            HeadingBug = FormatDeg(ap.HeadingBugDeg) + " mag";
            AltitudeMode = ap.AltitudeLock ? "On" : "Off";
            AltitudeTarget = FormatFeet(ap.AltitudeTargetFeet);
            VerticalMode = ap.FlightLevelChange
                ? "FLC"
                : ap.VerticalSpeedHold
                    ? "VS"
                    : "Off";
            VerticalTarget = ap.VerticalSpeedHold || ap.FlightLevelChange
                ? FormatFpm(ap.VerticalSpeedTargetFpm)
                : "—";
            SpeedMode = ap.MachHold ? "Mach" : ap.AirspeedHold ? "IAS" : "Off";
            SpeedTarget = ap.MachHold
                ? $"M {ap.MachTarget:0.000}"
                : ap.AirspeedHold
                    ? $"{ap.AirspeedTargetKts:0.#} kt"
                    : "—";
            YawDamper = OnOff(ap.YawDamper);
        }

        if (snapshot.Engines is { } eng && eng.Count > 0)
        {
            var throttles = eng.ThrottleLeverPct
                .Take(Math.Clamp(eng.Count, 1, eng.ThrottleLeverPct.Length))
                .Select((t, i) => $"E{i + 1} {t:0.#}%");
            Engines = string.Join(" · ", throttles);
        }
        else
        {
            Engines = "—";
        }

        Fuel = snapshot.Fuel is { } fuel
            ? $"{fuel.TotalGallons:0.#} / {fuel.TotalCapacityGallons:0.#} gal"
            : "—";

        if (snapshot.Time is { } time)
            TimeZulu = $"{time.ZuluHour:00}:{time.ZuluMinute:00}Z  ·  day {time.ZuluDayOfYear} / {time.ZuluYear}";
        else
            TimeZulu = "—";

        Weather = snapshot.Weather is { } wx
            ? $"Wind {wx.WindDirDeg:000}° / {wx.WindKts:0.#} kt · {wx.AmbientTempC:0.#} °C · {wx.SeaLevelPressureMb:0.#} hPa"
            : "—";
    }

    public string Title { get; }
    public string Subtitle { get; }
    public string Aircraft { get; }
    public string Airport { get; }
    public string Situation { get; }

    public string Ias { get; }
    public string Tas { get; }
    public string GroundSpeed { get; }
    public string VerticalSpeed { get; }
    public string AltitudeMsl { get; }
    public string AltitudeAgl { get; }
    public string Heading { get; }
    public string Pitch { get; }
    public string Bank { get; }

    public string Gear { get; }
    public string Flaps { get; }
    public string Spoilers { get; }
    public string ParkingBrake { get; }

    public string AutopilotMaster { get; }
    public string FlightDirector { get; }
    public string Autothrust { get; }
    public string AutothrustTarget { get; }
    public string NavMode { get; }
    public string HeadingMode { get; }
    public string HeadingBug { get; }
    public string AltitudeMode { get; }
    public string AltitudeTarget { get; }
    public string VerticalMode { get; }
    public string VerticalTarget { get; }
    public string SpeedMode { get; }
    public string SpeedTarget { get; }
    public string YawDamper { get; }

    public string Engines { get; }
    public string Fuel { get; }
    public string TimeZulu { get; }
    public string Weather { get; }

    private static string OnOff(bool value) => value ? "On" : "Off";

    private static string FormatKts(double value) =>
        double.IsFinite(value) ? $"{value:0.#} kt" : "—";

    private static string FormatFpm(double value) =>
        double.IsFinite(value) ? $"{value:+0;-0;0} fpm" : "—";

    private static string FormatFeet(double value) =>
        double.IsFinite(value) ? $"{value:#,##0} ft" : "—";

    private static string FormatDeg(double value) =>
        double.IsFinite(value) ? $"{value:0.#}°" : "—";
}
