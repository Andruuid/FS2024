using System.Text;
using ChallengeLab.Core.FlightLoading;

namespace ChallengeLab.Core.Tests;

public sealed class FlightLoadDomainTests
{
    [Fact]
    public void RealDeveloperExport_ParsesExpectedAircraftPoseAndWeatherReference()
    {
        var metadata = FltFileParser.Parse(Path.Combine(
            FindRepositoryRoot(), "data", "FltFiles", "andi1.flt"));

        Assert.Equal("andi1.flt", metadata.Title);
        Assert.Equal("A320neo V2", metadata.AircraftTitle);
        Assert.Equal("AIRBUS HOUSE", metadata.Livery);
        Assert.Equal(47.43757494, metadata.Latitude!.Value, 7);
        Assert.Equal(8.78471639, metadata.Longitude!.Value, 7);
        Assert.Equal(4464.21, metadata.AltitudeFeet!.Value, 2);
        Assert.Equal(276.269451, metadata.HeadingDegrees!.Value, 5);
        Assert.Equal(236, metadata.AirspeedKts);
        Assert.False(metadata.OnGround);
        Assert.False(metadata.UseWeatherFile);
        Assert.False(metadata.UseLiveWeather);
        Assert.Equal("andi1.wpr", metadata.WeatherPresetFile);
        Assert.False(metadata.WeatherPresetExists);
        Assert.Equal(FlightLoadWeatherStatus.NotRequested, metadata.WeatherStatus);
    }

    [Fact]
    public void Parser_PreservesLegacySingleByteDegreeMarkersAndResolvesWeatherBesideFlt()
    {
        using var dir = new TempDir();
        var flt = Path.Combine(dir.Path, "legacy.flt");
        var wpr = Path.Combine(dir.Path, "legacy.wpr");
        File.WriteAllText(wpr, "preset");
        var contents = """
                       [Main]
                       Title=legacy
                       [Sim.0]
                       Sim=A320neo V2
                       [Weather]
                       UseWeatherFile=True
                       UseLiveWeather=False
                       WeatherPresetFile=.\legacy.wpr
                       [SimVars.0]
                       Latitude=S12Â° 30' 0.0000"
                       Longitude=W45Â° 15' 30.0000"
                       Altitude=+005000.50
                       Heading=-10
                       ZVelBodyAxis_IAS=140
                       SimOnGround=0
                       """;
        File.WriteAllBytes(flt, Encoding.Latin1.GetBytes(contents));

        var metadata = FltFileParser.Parse(flt);

        Assert.Equal(-12.5, metadata.Latitude);
        Assert.Equal(-45.25833333, metadata.Longitude!.Value, 7);
        Assert.Equal(350, metadata.HeadingDegrees);
        Assert.Equal(Path.GetFullPath(wpr), metadata.WeatherPresetAbsolutePath);
        Assert.True(metadata.WeatherPresetExists);
        Assert.Equal(FlightLoadWeatherStatus.DependencyAvailable, metadata.WeatherStatus);
    }

    [Theory]
    [InlineData(FlightLoadSimulatorMode.MainMenu, null, true)]
    [InlineData(FlightLoadSimulatorMode.ActiveFlight, "A320neo V2", true)]
    [InlineData(FlightLoadSimulatorMode.ActiveFlight, "Microsoft A320neo V2", true)]
    [InlineData(FlightLoadSimulatorMode.ActiveFlight, "H125", false)]
    [InlineData(FlightLoadSimulatorMode.ActiveFlight, null, false)]
    [InlineData(FlightLoadSimulatorMode.Unknown, "A320neo V2", false)]
    public void SafetyPolicy_AllowsMenuOrSameAircraftAndBlocksUnsafeStates(
        FlightLoadSimulatorMode mode,
        string? currentTitle,
        bool expected)
    {
        var decision = FlightLoadSafetyPolicy.Evaluate(Target(), mode, currentTitle);

        Assert.Equal(expected, decision.Allowed);
        if (!expected && currentTitle == "H125")
            Assert.Contains("crash", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Readiness_IgnoresStaleSamplesAndRequiresThreeConsecutiveMatches()
    {
        var loadedUtc = DateTimeOffset.Parse("2026-07-21T12:00:10Z");
        var evaluator = new FlightLoadReadinessEvaluator(Target(), 3);
        evaluator.MarkFlightLoaded(loadedUtc);

        Assert.False(evaluator.Observe(Matching(loadedUtc.AddMilliseconds(-1))));
        Assert.Equal(0, evaluator.ConsecutiveValidSamples);
        Assert.False(evaluator.Observe(Matching(loadedUtc.AddMilliseconds(1))));
        Assert.False(evaluator.Observe(Matching(loadedUtc.AddMilliseconds(2))));
        Assert.True(evaluator.Observe(Matching(loadedUtc.AddMilliseconds(3))));
        Assert.True(evaluator.IsReady);
    }

    [Fact]
    public void Readiness_InvalidSampleResetsSequenceAndExplainsMismatch()
    {
        var loadedUtc = DateTimeOffset.Parse("2026-07-21T12:00:10Z");
        var evaluator = new FlightLoadReadinessEvaluator(Target(), 2);
        evaluator.MarkFlightLoaded(loadedUtc);
        evaluator.Observe(Matching(loadedUtc.AddSeconds(1)));

        var invalid = Matching(loadedUtc.AddSeconds(2)) with
        {
            AircraftTitle = "H125",
            Latitude = 40,
            AltitudeFeet = 15_000,
            AirspeedKts = 20,
            OnGround = true
        };
        Assert.False(evaluator.Observe(invalid));

        Assert.Equal(0, evaluator.ConsecutiveValidSamples);
        Assert.NotNull(evaluator.LastValidation);
        Assert.False(evaluator.LastValidation!.IsMatch);
        Assert.Contains(evaluator.LastValidation.Issues, issue => issue.Contains("TITLE", StringComparison.Ordinal));
        Assert.Contains(evaluator.LastValidation.Issues, issue => issue.Contains("Position", StringComparison.Ordinal));
        Assert.Contains(evaluator.LastValidation.Issues, issue => issue.Contains("Altitude", StringComparison.Ordinal));
        Assert.Contains(evaluator.LastValidation.Issues, issue => issue.Contains("On-ground", StringComparison.Ordinal));
        Assert.Contains(evaluator.LastValidation.Issues, issue => issue.Contains("IAS", StringComparison.Ordinal));
    }

    [Fact]
    public void ReportStore_AtomicallyRoundTripsStructuredAttempt()
    {
        using var dir = new TempDir();
        var store = new FlightLoadReportStore(dir.Path);
        var attempt = Guid.NewGuid();
        var result = new FlightLoadResult
        {
            AttemptId = attempt,
            RequestedUtc = DateTimeOffset.Parse("2026-07-21T12:00:00Z"),
            CompletedUtc = DateTimeOffset.Parse("2026-07-21T12:00:18Z"),
            Outcome = FlightLoadOutcome.PartialSuccess,
            FlightFilePath = "C:\\tests\\andi1.flt",
            FlightLoadedEventReceived = true,
            LoadedFilename = "andi1.flt",
            Message = "Loaded; weather unverified.",
            ValidationIssues = ["Weather unavailable"],
            Timeline = [new FlightLoadTimelineEntry { Stage = "FlightLoaded", Message = "event" }],
            Weather = new FlightLoadWeatherAssessment
            {
                Status = FlightLoadWeatherStatus.NotRequested,
                PresetFile = "andi1.wpr"
            }
        };

        var path = store.Save(result);
        var loaded = store.Load(path);

        Assert.True(File.Exists(path));
        Assert.Equal(attempt, loaded.AttemptId);
        Assert.Equal(FlightLoadOutcome.PartialSuccess, loaded.Outcome);
        Assert.Single(loaded.Timeline);
        Assert.Equal(path, loaded.ReportPath);
        Assert.Empty(Directory.EnumerateFiles(dir.Path, "*.tmp"));
    }

    private static FltFileMetadata Target() => new()
    {
        FilePath = "andi1.flt",
        AircraftTitle = "A320neo V2",
        Latitude = 47.43757494,
        Longitude = 8.78471639,
        AltitudeFeet = 4464.21,
        HeadingDegrees = 276.27,
        AirspeedKts = 236,
        OnGround = false,
        WeatherPresetFile = "andi1.wpr"
    };

    private static FlightLoadObservation Matching(DateTimeOffset timestamp) => new()
    {
        TimestampUtc = timestamp,
        AircraftTitle = "Microsoft A320neo V2",
        Latitude = 47.4376,
        Longitude = 8.7847,
        AltitudeFeet = 4500,
        HeadingTrueDeg = 276,
        AirspeedKts = 230,
        OnGround = false,
        WindDirectionDeg = 270,
        WindVelocityKts = 18
    };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChallengeLab.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "challenge-lab-flight-load-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch { /* best effort */ }
        }
    }
}
