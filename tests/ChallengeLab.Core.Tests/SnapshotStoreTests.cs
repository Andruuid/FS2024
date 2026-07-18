using ChallengeLab.Core.Snapshots;

namespace ChallengeLab.Core.Tests;

public sealed class SnapshotStoreTests
{
    [Fact]
    public void SaveLoad_RoundTripsAllGroups()
    {
        using var dir = new TempDir();
        var store = new SnapshotStore(dir.Path);
        var snapshot = BuildSnapshot("Final LSZH 14");

        var path = store.Save(snapshot);
        Assert.True(File.Exists(path));

        var loaded = store.Load(path);
        Assert.Equal(FlightStateSnapshot.FormatId, loaded.Format);
        Assert.Equal(snapshot.Id, loaded.Id);
        Assert.Equal("Final LSZH 14", loaded.Name);
        Assert.Equal("A330-200 (RR)", loaded.AircraftTitle);
        Assert.Equal(SnapshotPauseContext.Flying, loaded.PauseContext);
        Assert.Equal(snapshot.Latitude, loaded.Latitude, 8);
        Assert.Equal(snapshot.AltitudeFeet, loaded.AltitudeFeet, 3);
        Assert.False(loaded.OnGround);
        Assert.Equal(-71.42, loaded.BodyVelZMs, 3);
        Assert.Equal(0.013, loaded.RotVelYRadS, 6);
        Assert.False(loaded.GearHandleDown);
        Assert.Equal(2, loaded.FlapsHandleIndex);
        Assert.Equal(4, loaded.FlapsHandleCount);
        Assert.True(loaded.ParkingBrakeOn);
        Assert.NotNull(loaded.Trim);
        Assert.Equal(-0.031, loaded.Trim!.ElevatorTrimRad, 6);
        Assert.NotNull(loaded.Fuel);
        Assert.Equal(2, loaded.Fuel!.Tanks.Count);
        Assert.Equal(1_450.5, loaded.Fuel.Tanks["LEFT MAIN"], 3);
        Assert.NotNull(loaded.Engines);
        Assert.True(loaded.Engines!.Combustion[0]);
        Assert.True(loaded.Engines.Combustion[1]);
        Assert.Equal(62.5, loaded.Engines.ThrottleLeverPct[0], 3);
        Assert.NotNull(loaded.Lights);
        Assert.True(loaded.Lights!.Landing);
        Assert.False(loaded.Lights.Logo);
        Assert.NotNull(loaded.Time);
        Assert.Equal(13 * 3600 + 45 * 60, loaded.Time!.ZuluTimeSeconds, 3);
        Assert.Equal(13, loaded.Time.ZuluHour);
        Assert.Equal(45, loaded.Time.ZuluMinute);
        Assert.Equal(199, loaded.Time.ZuluDayOfYear);
        Assert.Equal(2026, loaded.Time.ZuluYear);
        Assert.NotNull(loaded.Weather);
        Assert.Contains("GLOB", loaded.Weather!.ReconstructedMetar);
        Assert.NotNull(loaded.Airport);
        Assert.Equal("LSZH", loaded.Airport!.Icao);
        Assert.Equal("CH", loaded.Airport.CountryCode);
        Assert.Contains("LSZH", loaded.Airport.Label);
        Assert.NotNull(loaded.Autopilot);
        Assert.True(loaded.Autopilot!.Master);
        Assert.True(loaded.Autopilot.AutothrottleArmed);
        Assert.True(loaded.Autopilot.Nav1Lock);
        Assert.False(loaded.Autopilot.HeadingLock);
        Assert.Equal(137, loaded.Autopilot.HeadingBugDeg, 3);
        Assert.Equal(4000, loaded.Autopilot.AltitudeTargetFeet, 3);
        Assert.Equal(139, loaded.Autopilot.AirspeedTargetKts, 3);
        Assert.Equal(-800, loaded.Autopilot.VerticalSpeedTargetFpm, 3);
    }

    [Fact]
    public void List_OrdersNewestFirst_AndSkipsCorruptFiles()
    {
        using var dir = new TempDir();
        var store = new SnapshotStore(dir.Path);

        var older = BuildSnapshot("older");
        var newer = BuildSnapshot("newer");
        var olderPath = store.Save(older);
        var newerPath = store.Save(newer);
        File.SetLastWriteTimeUtc(olderPath, DateTime.UtcNow.AddHours(-2));
        File.SetLastWriteTimeUtc(newerPath, DateTime.UtcNow);

        File.WriteAllText(Path.Combine(dir.Path, "broken.json"), "{ not valid json");

        var items = store.List();
        Assert.Equal(2, items.Count);
        Assert.Equal("newer", items[0].Name);
        Assert.Equal("older", items[1].Name);
        Assert.Contains("A330-200 (RR)", items[0].DisplayName);
        Assert.Contains("AIR", items[0].DisplayName);
    }

    [Fact]
    public void Rename_UpdatesNameAndFile_KeepsStampAndId()
    {
        using var dir = new TempDir();
        var store = new SnapshotStore(dir.Path);
        var snapshot = BuildSnapshot("original");
        var originalPath = store.Save(snapshot);
        var stamp = snapshot.CreatedUtc.ToString("yyyyMMdd_HHmmss");

        var newPath = store.Rename(originalPath, "Before the storm: try/2");

        Assert.False(File.Exists(originalPath));
        Assert.True(File.Exists(newPath));
        var fileName = Path.GetFileName(newPath);
        Assert.StartsWith(stamp, fileName);
        Assert.Contains(snapshot.Id.ToString("N"), fileName);
        Assert.DoesNotContain(":", fileName);
        Assert.DoesNotContain("/", fileName);

        var loaded = store.Load(newPath);
        Assert.Equal("Before the storm: try/2", loaded.Name);
        Assert.Equal(snapshot.Id, loaded.Id);
        Assert.Equal(snapshot.CreatedUtc, loaded.CreatedUtc);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        using var dir = new TempDir();
        var store = new SnapshotStore(dir.Path);
        var path = store.Save(BuildSnapshot("to delete"));

        store.Delete(path);

        Assert.False(File.Exists(path));
        Assert.Empty(store.List());
    }

    [Fact]
    public void Load_ToleratesUnknownFieldsAndMissingGroups()
    {
        using var dir = new TempDir();
        var store = new SnapshotStore(dir.Path);
        var id = Guid.NewGuid();
        var json =
            $$"""
            {
              "format": "challengelab.flightsnapshot/v1",
              "id": "{{id}}",
              "createdUtc": "2026-07-18T12:00:00+00:00",
              "name": "minimal",
              "aircraftTitle": "Cessna 172",
              "latitude": 47.0,
              "longitude": 8.0,
              "altitudeFeet": 3500,
              "onGround": false,
              "someFutureField": { "nested": true },
              "anotherUnknown": 42
            }
            """;
        var path = Path.Combine(dir.Path, "minimal.json");
        File.WriteAllText(path, json);

        var loaded = store.Load(path);
        Assert.Equal(id, loaded.Id);
        Assert.Equal("minimal", loaded.Name);
        Assert.Null(loaded.Trim);
        Assert.Null(loaded.Fuel);
        Assert.Null(loaded.Engines);
        Assert.Null(loaded.Lights);
        Assert.Null(loaded.Time);
        Assert.Null(loaded.Weather);
        Assert.Null(loaded.Airport);
        Assert.True(loaded.IsAirborne);
    }

    [Fact]
    public void NameBuilder_ComposesCustomAirportAndCoordinates()
    {
        var airport = new SnapshotAirportInfo
        {
            Icao = "LSZH",
            Municipality = "Zurich",
            CountryCode = "CH"
        };

        Assert.Equal("LSZH Zurich (CH)", SnapshotNameBuilder.BuildDefaultName(null, airport, 47, 8));
        Assert.Equal(
            "Storm approach LSZH Zurich (CH)",
            SnapshotNameBuilder.BuildDefaultName("  Storm approach ", airport, 47, 8));
        Assert.Equal(
            "47.46N 8.55E",
            SnapshotNameBuilder.BuildDefaultName("", null, 47.4647, 8.5492));
        Assert.Equal(
            "12.30S 45.60W",
            SnapshotNameBuilder.BuildDefaultName(null, null, -12.3, -45.6));

        var noMunicipality = new SnapshotAirportInfo { Icao = "EDDM", CountryCode = "DE" };
        Assert.Equal("EDDM (DE)", SnapshotNameBuilder.BuildDefaultName(null, noMunicipality, 48, 11));
    }

    [Fact]
    public void Sanitize_HandlesInvalidCharsAndLength()
    {
        Assert.Equal("snapshot", SnapshotStore.SanitizeFileNamePart(null));
        Assert.Equal("snapshot", SnapshotStore.SanitizeFileNamePart("   "));
        Assert.Equal("a_b_c", SnapshotStore.SanitizeFileNamePart("a/b:c"));
        Assert.Equal("two_words", SnapshotStore.SanitizeFileNamePart("two words"));
        Assert.True(SnapshotStore.SanitizeFileNamePart(new string('x', 200)).Length <= 80);
    }

    private static FlightStateSnapshot BuildSnapshot(string name) => new()
    {
        CreatedUtc = new DateTimeOffset(2026, 7, 18, 13, 45, 0, TimeSpan.Zero),
        Name = name,
        AircraftTitle = "A330-200 (RR)",
        PauseContext = SnapshotPauseContext.Flying,
        Airport = new SnapshotAirportInfo
        {
            Icao = "LSZH",
            Name = "Zurich Airport",
            Municipality = "Zurich",
            CountryCode = "CH",
            DistanceNm = 3.4
        },
        Latitude = 47.4647,
        Longitude = 8.5492,
        AltitudeFeet = 4200,
        PitchDeg = -2.5,
        BankDeg = 0.8,
        HeadingTrueDeg = 137.2,
        OnGround = false,
        IasKts = 138,
        BodyVelXMs = 0.4,
        BodyVelYMs = -1.9,
        BodyVelZMs = -71.42,
        RotVelXRadS = 0.001,
        RotVelYRadS = 0.013,
        RotVelZRadS = -0.002,
        TasKts = 146,
        GroundSpeedKts = 141,
        VerticalSpeedFpm = -750,
        AglFeet = 2800,
        GearHandleDown = false,
        IsGearRetractable = true,
        GearTotalPctExtended = 0,
        FlapsHandleIndex = 2,
        FlapsHandleCount = 4,
        SpoilersHandle01 = 0,
        ParkingBrakeOn = true,
        SimulationRate = 1,
        Trim = new SnapshotTrim { ElevatorTrimRad = -0.031, AileronTrimPct01 = 0.01, RudderTrimPct01 = 0 },
        Fuel = new SnapshotFuel
        {
            TotalGallons = 2901,
            TotalCapacityGallons = 36_700,
            Tanks = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            {
                ["LEFT MAIN"] = 1_450.5,
                ["RIGHT MAIN"] = 1_450.5
            }
        },
        Engines = new SnapshotEngines
        {
            Count = 2,
            EngineType = 1,
            Combustion = new[] { true, true, false, false },
            ThrottleLeverPct = new[] { 62.5, 62.5, 0, 0 },
            MixtureLeverPct = new double[4],
            PropellerLeverPct = new double[4]
        },
        Lights = new SnapshotLights { Beacon = true, Landing = true, Nav = true, Strobe = true },
        Autopilot = new SnapshotAutopilot
        {
            Master = true,
            FlightDirector = true,
            AutothrottleArmed = true,
            YawDamper = true,
            Nav1Lock = true,
            HeadingLock = false,
            HeadingBugDeg = 137,
            AltitudeLock = true,
            AltitudeTargetFeet = 4000,
            AirspeedHold = true,
            AirspeedTargetKts = 139,
            VerticalSpeedTargetFpm = -800,
            MachTarget = 0.42
        },
        Time = new SnapshotTime
        {
            ZuluTimeSeconds = 13 * 3600 + 45 * 60,
            ZuluDayOfYear = 199,
            ZuluYear = 2026,
            LocalTimeSeconds = 15 * 3600 + 45 * 60,
            TimeZoneOffsetSeconds = 7200
        },
        Weather = new SnapshotWeather
        {
            WindDirDeg = 340,
            WindKts = 12,
            SeaLevelPressureMb = 1018,
            AmbientTempC = 21,
            VisibilityM = 9999,
            ReconstructedMetar = "GLOB 010000Z 34012KT 9999 FEW035 21/19 Q1018"
        },
        Info = new SnapshotInfo { CameraState = 2, TotalWeightLbs = 380_000 }
    };

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "challenge-lab-snapshot-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
