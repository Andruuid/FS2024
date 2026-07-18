using System.Globalization;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class OurAirportsRunwayCatalogTests
{
    [Theory]
    [InlineData("US", 4_199.999, 500)]
    [InlineData("us", 4_200, 1_000)]
    [InlineData("US", 8_000, 1_000)]
    [InlineData("FR", 3_937.007874015748 - 0.001, 500)]
    [InlineData("FR", 3_937.007874015748, 984)]
    [InlineData("GR", 7_874.015748031496 - 0.001, 984)]
    [InlineData("GR", 7_874.015748031496, 1_312)]
    public void AimingPointCalculator_UsesFaaAndIcaoLdaBoundaries(
        string countryCode,
        double ldaFeet,
        double expectedFeet)
    {
        Assert.Equal(
            expectedFeet,
            AimingPointCalculator.CalculateExpectedDistanceFromThresholdFeet(countryCode, ldaFeet),
            6);
    }

    [Fact]
    public void AimingPointCalculator_ReportsPavementDiagnosticWithoutChangingThresholdValue()
    {
        var thresholdRelative = AimingPointCalculator.CalculateExpectedDistanceFromThresholdFeet("US", 5_000);
        var pavementRelative = AimingPointCalculator.CalculateDistanceFromPavementEndFeet("US", 5_000, 350);

        Assert.Equal(1_000, thresholdRelative);
        Assert.Equal(1_350, pavementRelative);
    }

    [Fact]
    public void Catalog_IndexesBothEndsAndHandlesCsvEdgeCases()
    {
        using var fixture = CsvFixture.Create();
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-CH");
            var catalog = OurAirportsRunwayCatalog.Load(fixture.AirportsPath, fixture.RunwaysPath);

            Assert.True(catalog.TryGetRunwayEnd("kaaa", "RWY 9", out var low));
            Assert.Equal("US", low.CountryCode);
            Assert.Equal("09", low.RunwayId);
            Assert.Equal(90, low.HeadingTrueDeg, 6);
            Assert.Equal(100, low.DisplacedThresholdFeet);
            Assert.Equal(4_900, low.LandingDistanceAvailableFeet);
            Assert.True(low.UsableThresholdLongitude > low.PhysicalEndLongitude);

            Assert.True(catalog.TryGetRunwayEnd("TST1", "runway-27", out var high));
            Assert.Equal(270, high.HeadingTrueDeg, 6);
            Assert.Equal(200, high.DisplacedThresholdFeet);
            Assert.Equal(4_800, high.LandingDistanceAvailableFeet);
            Assert.True(high.UsableThresholdLongitude < high.PhysicalEndLongitude);

            Assert.True(catalog.TryGetRunwayEnd("TEST2", "01", out var derivedLow));
            Assert.InRange(derivedLow.HeadingTrueDeg, 18, 21);
            Assert.Equal(0, derivedLow.DisplacedThresholdFeet);
            Assert.Equal(4_000, derivedLow.LandingDistanceAvailableFeet);
            Assert.True(catalog.TryGetRunwayEnd("TEST2", "19", out var derivedHigh));
            Assert.InRange(derivedHigh.HeadingTrueDeg, 198, 201);

            Assert.False(catalog.TryGetRunwayEnd("TST1", "10", out _));
            Assert.False(catalog.TryGetRunwayEnd("BAD", "01", out _));
            Assert.StartsWith("ourairports-sha256-", catalog.SnapshotId, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void Resolver_UsesUsableThresholdLdaAndDoesNotDoubleCountDisplacement()
    {
        using var fixture = CsvFixture.Create();
        var catalog = OurAirportsRunwayCatalog.Load(fixture.AirportsPath, fixture.RunwaysPath);
        var runway = new RunwayConfig
        {
            AirportIcao = "KAAA",
            RunwayId = "09",
            ThresholdLatitude = -1,
            ThresholdLongitude = -1,
            HeadingTrueDeg = 0,
            LengthM = 1,
            WidthM = 1
        };

        var resolved = new RunwayReferenceResolver(catalog).TryApplyCsv(runway);

        Assert.True(resolved);
        Assert.Equal("US", runway.CountryCode);
        Assert.Equal("OurAirports CSV", runway.RunwayDataSource);
        Assert.Equal(catalog.SnapshotId, runway.RunwayDataSnapshotId);
        Assert.Equal(100 * RunwayPathGeometry.MetersPerFoot, runway.DisplacedThresholdM!.Value, 6);
        Assert.Equal(4_900 * RunwayPathGeometry.MetersPerFoot, runway.LandingDistanceAvailableM!.Value, 6);
        Assert.Equal(1_000 * RunwayPathGeometry.MetersPerFoot, runway.AimingMarkerStartM!.Value, 6);
        Assert.Equal(1_100 * RunwayPathGeometry.MetersPerFoot, runway.AimingMarkerFromPavementEndM!.Value, 6);
        Assert.Equal(1_400 * RunwayPathGeometry.MetersPerFoot, runway.IdealTouchdownDistanceM!.Value, 6);
        Assert.True(runway.ThresholdLongitude > 7);
    }

    [Fact]
    public void Resolver_RequiresAnExactIdentifierAndLeavesStoredGeometryUntouchedOnMiss()
    {
        using var fixture = CsvFixture.Create();
        var resolver = new RunwayReferenceResolver(
            OurAirportsRunwayCatalog.Load(fixture.AirportsPath, fixture.RunwaysPath));
        var runway = new RunwayConfig
        {
            AirportIcao = "KAAA",
            RunwayId = "10",
            ThresholdLatitude = 44,
            ThresholdLongitude = 6,
            HeadingTrueDeg = 100,
            LengthM = 1_000,
            WidthM = 30,
            RunwayDataSource = "Stored flight-tape geometry"
        };

        Assert.False(resolver.TryApplyCsv(runway));
        Assert.Equal(44, runway.ThresholdLatitude);
        Assert.Equal(6, runway.ThresholdLongitude);
        Assert.Equal("Stored flight-tape geometry", runway.RunwayDataSource);
    }

    [Fact]
    public void BundledSnapshot_ResolvesKnownSuppliedRunway()
    {
        var catalog = OurAirportsRunwayCatalog.Default;

        Assert.True(catalog.IsAvailable, catalog.LoadError);
        Assert.True(catalog.TryGetRunwayEnd("LGSK", "01", out var runway));
        Assert.Equal("GR", runway.CountryCode);
        Assert.Equal(190, runway.DisplacedThresholdFeet);
        Assert.Equal(5_151, runway.LandingDistanceAvailableFeet);
        Assert.Equal(19, runway.HeadingTrueDeg, 6);
    }

    private sealed class CsvFixture : IDisposable
    {
        private CsvFixture(string directory)
        {
            Directory = directory;
            AirportsPath = Path.Combine(directory, "airports.csv");
            RunwaysPath = Path.Combine(directory, "runways.csv");
        }

        public string Directory { get; }
        public string AirportsPath { get; }
        public string RunwaysPath { get; }

        public static CsvFixture Create()
        {
            var fixture = new CsvFixture(Path.Combine(
                Path.GetTempPath(),
                "ChallengeLabTests",
                Guid.NewGuid().ToString("N")));
            System.IO.Directory.CreateDirectory(fixture.Directory);
            File.WriteAllText(fixture.AirportsPath,
                "\"id\",\"ident\",\"type\",\"name\",\"latitude_deg\",\"longitude_deg\",\"elevation_ft\",\"iso_country\",\"icao_code\",\"gps_code\"\r\n" +
                "1,\"TST1\",\"small_airport\",\"Quoted, Airport\",45,7,100,\"US\",\"KAAA\",\"KAAA\"\r\n" +
                "2,\"TEST2\",\"small_airport\",\"Line one\nline two\",46,8,250,\"FR\",\"\",\"\"\r\n");
            File.WriteAllText(fixture.RunwaysPath,
                "\"id\",\"airport_ref\",\"airport_ident\",\"length_ft\",\"width_ft\",\"surface\",\"lighted\",\"closed\",\"le_ident\",\"le_latitude_deg\",\"le_longitude_deg\",\"le_elevation_ft\",\"le_heading_degT\",\"le_displaced_threshold_ft\",\"he_ident\",\"he_latitude_deg\",\"he_longitude_deg\",\"he_elevation_ft\",\"he_heading_degT\",\"he_displaced_threshold_ft\"\r\n" +
                "10,1,\"TST1\",5000.0,100.5,\"ASP\",1,0,\"9\",45,7,100,90,100,\"27\",45,7.02,101,270,200\r\n" +
                "11,2,\"TEST2\",4000,80,\"ASP\",0,0,\"01\",46,8,250,,,\"19\",46.01,8.005,251,,\r\n" +
                "12,999,\"BAD\",not-a-number,80,\"ASP\",0,0,\"01\",95,8,,,,\"19\",46,8.1,,,\r\n" +
                "13,1,\"TST1\",4500,80,\"ASP\",0,1,\"10\",45,7,,,,\"28\",45,7.1,,,\r\n");
            return fixture;
        }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
