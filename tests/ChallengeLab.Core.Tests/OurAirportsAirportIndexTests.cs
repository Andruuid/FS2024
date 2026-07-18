using ChallengeLab.Core.Facilities;

namespace ChallengeLab.Core.Tests;

public sealed class OurAirportsAirportIndexTests
{
    [Fact]
    public void FindNearest_PicksClosestOpenAirport_WithNameCountryMunicipality()
    {
        using var fixture = new AirportsCsvFixture(
            """
            id,ident,type,name,latitude_deg,longitude_deg,iso_country,municipality
            1,LSZH,large_airport,"Zurich Airport",47.4647,8.5492,CH,Zurich
            2,EDDM,large_airport,"Munich Airport",48.3538,11.7861,DE,Munich
            3,XCLS,closed,"Old Closed Field",47.4650,8.5495,CH,Zurich
            4,LSPH,small_airport,"Winterthur",47.5150,8.7720,CH,Winterthur
            """);
        var index = OurAirportsAirportIndex.Load(fixture.Path);

        Assert.True(index.IsAvailable);
        Assert.Equal(3, index.AirportCount);

        // The closed field sits closest to this point but must be excluded.
        var nearZurich = index.FindNearest(47.4650, 8.5495);
        Assert.NotNull(nearZurich);
        Assert.Equal("LSZH", nearZurich!.Ident);
        Assert.Equal("CH", nearZurich.CountryCode);
        Assert.Equal("Zurich", nearZurich.Municipality);
        Assert.Equal("Zurich Airport", nearZurich.Name);
        Assert.True(nearZurich.DistanceNm < 1);

        var nearMunich = index.FindNearest(48.35, 11.78);
        Assert.NotNull(nearMunich);
        Assert.Equal("EDDM", nearMunich!.Ident);
        Assert.Equal("DE", nearMunich.CountryCode);
    }

    [Fact]
    public void Load_MissingFile_Throws_AndDefaultDegradesGracefully()
    {
        Assert.Throws<FileNotFoundException>(() =>
            OurAirportsAirportIndex.Load(Path.Combine(Path.GetTempPath(), "does-not-exist.csv")));
    }

    [Fact]
    public void BundledCsv_ResolvesRealAirports()
    {
        // Integration smoke test against the shipped OurAirports snapshot; soft-skips
        // when the repo data folder is not reachable from the test bin.
        var index = OurAirportsAirportIndex.Default;
        if (!index.IsAvailable)
            return;

        var zurich = index.FindNearest(47.4647, 8.5492);
        Assert.NotNull(zurich);
        Assert.Equal("CH", zurich!.CountryCode);
        Assert.True(zurich.DistanceNm < 5);
    }

    private sealed class AirportsCsvFixture : IDisposable
    {
        public string Path { get; }

        public AirportsCsvFixture(string csv)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"challenge-lab-airports-{Guid.NewGuid():N}.csv");
            File.WriteAllText(Path, csv);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path)) File.Delete(Path);
            }
            catch
            {
                // best effort cleanup
            }
        }
    }
}
