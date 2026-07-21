using ChallengeLab.Core.Facilities;

namespace ChallengeLab.Core.Tests;

public sealed class IlsFrequencyCatalogTests
{
    [Fact]
    public void Load_SearchesNamesAndIcao_OrdersRunwaysAndRemovesExactDuplicates()
    {
        using var fixture = new CatalogFixture(
            """
            "icao","elevation_ft","heading_deg","frequency_mhz","range_nm","ident","runway"
            "LSZH","1416","332.03622","110.75","27","IZS","ILS RW34"
            "LSZH","1416","273.01172","109.75","27","IZW","ILS RW28"
            "LSZH","1416","134.32645","111.75","27","IKL","ILS RW14"
            "LSZH","1416","152.03622","110.5","27","IZH","ILS RW16"
            "LSZH","1416","273.01172","109.75","27","IZW","ILS RW28"
            "LEZG","118.1","300","109.5","27","IZZA","ILS DME RW30R"
            """,
            """
            id,ident,type,name,latitude_deg,longitude_deg,iso_country,municipality,icao_code,gps_code,iata_code
            1,LSZH,large_airport,"Zürich Airport",47.4647,8.5492,CH,Zürich,LSZH,LSZH,ZRH
            """);

        var catalog = IlsFrequencyCatalog.Load(fixture.IlsPath, fixture.AirportsPath);

        Assert.True(catalog.IsAvailable);
        Assert.Equal(2, catalog.AirportCount);
        Assert.Equal(5, catalog.TransmitterCount);

        var byName = Assert.Single(catalog.Search("zurich"));
        Assert.Equal("LSZH", byName.Icao);
        Assert.Contains("Zürich Airport", byName.DisplayName, StringComparison.Ordinal);
        Assert.Equal(["14", "16", "28", "34"], byName.Runways.Select(runway => runway.Runway));

        var byCode = Assert.Single(catalog.Search("lszh"));
        var runway28 = Assert.Single(byCode.Runways, runway => runway.Runway == "28");
        Assert.Equal("IZW", runway28.Ident);
        Assert.Equal(109.75m, runway28.FrequencyMhz);
        Assert.Equal(273, runway28.CourseDegrees);

        var codeOnly = catalog.FindExact("LEZG");
        Assert.NotNull(codeOnly);
        Assert.Equal("LEZG", codeOnly!.DisplayName);
        Assert.Equal("30R", Assert.Single(codeOnly.Runways).Runway);
    }

    [Fact]
    public void SharedFrequencyDifferentCourses_IsPreservedAndClearlyFlagged()
    {
        using var fixture = new CatalogFixture(
            """
            icao,elevation_ft,heading_deg,frequency_mhz,range_nm,ident,runway
            KSEA,433,164.2,111.70,27,IUC,ILS RW16C
            KSEA,433,344.2,111.70,27,IUC,ILS RW34C
            TEST,0,90,110.10,27,ITST,ILS RW09
            TEST,0,91,110.30,27,IALT,ILS RW09
            """,
            """
            id,ident,type,name,latitude_deg,longitude_deg,iso_country,municipality,icao_code,gps_code,iata_code
            1,KSEA,large_airport,Seattle-Tacoma,47.45,-122.31,US,Seattle,KSEA,KSEA,SEA
            2,TEST,small_airport,Test Airport,0,0,ZZ,Test,TEST,TEST,TST
            """);

        var catalog = IlsFrequencyCatalog.Load(fixture.IlsPath, fixture.AirportsPath);
        var seattle = Assert.Single(catalog.Search("KSEA"));
        Assert.Equal(2, seattle.Runways.Count);
        Assert.All(seattle.Runways, runway => Assert.True(runway.HasAmbiguity));
        Assert.Contains("RW16C 164°", seattle.Runways[0].AmbiguityWarning, StringComparison.Ordinal);
        Assert.Contains("RW34C 344°", seattle.Runways[0].AmbiguityWarning, StringComparison.Ordinal);

        var sameRunwayTransmitters = Assert.Single(catalog.Search("TEST"));
        Assert.Equal(2, sameRunwayTransmitters.Runways.Count);
        Assert.All(sameRunwayTransmitters.Runways, runway => Assert.Equal("09", runway.Runway));
    }

    [Theory]
    [InlineData(0, 360)]
    [InlineData(359.6, 360)]
    [InlineData(0.5, 1)]
    [InlineData(273.01172, 273)]
    public void RoundCourse_ProducesMcduCourse(double heading, int expected)
    {
        Assert.Equal(expected, IlsFrequencyCatalog.RoundCourse(heading));
    }

    [Fact]
    public void BundledCatalogue_ContainsZurichRunway28()
    {
        var catalog = IlsFrequencyCatalog.Default;
        Assert.True(catalog.IsAvailable, catalog.LoadError);
        var zurich = catalog.FindExact("LSZH");
        Assert.NotNull(zurich);
        Assert.Contains(zurich!.Runways, runway =>
            runway.Runway == "28"
            && runway.Ident == "IZW"
            && runway.FrequencyMhz == 109.75m
            && runway.CourseDegrees == 273);
    }

    private sealed class CatalogFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(), "ChallengeLabIlsCatalog", Guid.NewGuid().ToString("N"));

        public CatalogFixture(string ilsCsv, string airportsCsv)
        {
            Directory.CreateDirectory(_directory);
            IlsPath = Path.Combine(_directory, "ils_frequencies.csv");
            AirportsPath = Path.Combine(_directory, "airports.csv");
            File.WriteAllText(IlsPath, ilsCsv);
            File.WriteAllText(AirportsPath, airportsCsv);
        }

        public string IlsPath { get; }
        public string AirportsPath { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch { /* best effort cleanup */ }
        }
    }
}
