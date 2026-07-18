using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class AircraftVappCatalogTests
{
    private static readonly AirportFacility Airport = new("TEST", "ZZ", 0, 0, 10);

    [Fact]
    public void DefaultCatalog_LoadsBundledAircraftDb()
    {
        var catalog = AircraftVappCatalog.Default;
        Assert.True(catalog.IsAvailable, catalog.LoadError);
        Assert.True(catalog.EntryCount >= 20);
        Assert.Null(catalog.LoadError);
    }

    [Theory]
    [InlineData("Airbus A320 neo Asobo", "a320", 136)]
    [InlineData("FBW A32NX", "a320", 136)]
    [InlineData("Boeing 737-800", "b737", 138)]
    [InlineData("Boeing 777-300ER PMDG", "b777", 145)]
    [InlineData("Airbus A380-800", "a380", 145)]
    [InlineData("Cessna 172 Skyhawk Asobo", "c172", 65)]
    [InlineData("H125 / AS350 B3e", "h125", 65)]
    public void TryMatch_ResolvesCommonTitles(string title, string expectedId, double expectedVapp)
    {
        var catalog = LoadBundled();
        var match = catalog.TryMatch(title);
        Assert.NotNull(match);
        Assert.Equal(expectedId, match!.Entry.Id);
        Assert.Equal(expectedVapp, match.Entry.VappKts);
    }

    [Fact]
    public void TryMatch_PrefersMoreSpecificBoeing737MaxBeforeGeneric737()
    {
        var catalog = LoadBundled();
        var match = catalog.TryMatch("Boeing 737 MAX 8");
        Assert.NotNull(match);
        Assert.Equal("b737-max", match!.Entry.Id);
        Assert.Equal(140, match.Entry.VappKts);
    }

    [Fact]
    public void TryMatch_ReturnsNullForUnknownTitle()
    {
        var catalog = LoadBundled();
        Assert.Null(catalog.TryMatch("Completely Fictional Space Shuttle MkII"));
        Assert.Null(catalog.TryMatch("Airbus"));
        Assert.Null(catalog.TryMatch("Boeing"));
        Assert.Null(catalog.TryMatch(null));
        Assert.Null(catalog.TryMatch("   "));
    }

    [Fact]
    public void FreeFlightFactory_FreezesMatchedVappAndTitle()
    {
        var end = new RunwayEndFacility(Airport, "09L", 0, 0, 10, 90, 2000, 45, 4, false);
        var sample = new TelemetrySample
        {
            AircraftTitle = "IniBuilds A330-900neo",
            IsGearRetractable = true,
            IsGearWheels = true
        };

        var challenge = FreeFlightChallengeFactory.Create(new FreeFlightTarget(end, 2, 0, 0), sample);

        Assert.Equal("IniBuilds A330-900neo", challenge.AircraftTitles.Single());
        Assert.Equal(143, challenge.AircraftSetup.VappKts);
        Assert.Contains("A330", string.Join(' ', challenge.HudTips));
    }

    private static AircraftVappCatalog LoadBundled()
    {
        var path = Path.Combine(FindRepoConfigRoot(), "scoring", "aircraft-vapp-db.json");
        return AircraftVappCatalog.Load(path);
    }

    private static string FindRepoConfigRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "config");
            if (File.Exists(Path.Combine(candidate, "catalog.json")))
                return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("config/ not found from test base directory.");
    }
}
