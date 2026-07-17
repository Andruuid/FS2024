using ChallengeLab.Core.Facilities;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class SteepApproachGlideslopeCatalogTests
{
    [Theory]
    [InlineData("LSZA", "01", 6.65)]
    [InlineData("lsza", "Rwy 01", 6.65)]
    [InlineData("EGLC", "09", 5.50)]
    [InlineData("EGLC", "27", 5.50)]
    [InlineData("KASE", "15", 6.59)]
    [InlineData("LOWI", "08", 4.00)]
    [InlineData("LPMA", "05", 4.00)]
    [InlineData("LFML", "31R", 4.00)]
    [InlineData("KPVU", "13", 3.75)]
    public void Catalog_ResolvesListedRunwayEnds(string icao, string runway, double expectedDeg)
    {
        Assert.True(SteepApproachGlideslopeCatalog.TryResolve(icao, runway, out var r));
        Assert.Equal(expectedDeg, r.Degrees);
        Assert.Equal(SteepApproachGlideslopeCatalog.SourceCatalog, r.Source);
    }

    [Theory]
    [InlineData("LXGB", "09", 4.00)]
    [InlineData("LXGB", "27", 5.00)]
    [InlineData("LXGB", "01", 3.50)]
    [InlineData("LEXJ", "29", 4.75)]
    [InlineData("EDDF", "07R", 3.51)]
    [InlineData("EDDF", "25L", 3.51)]
    [InlineData("NZCH", "02", 4.70)]
    [InlineData("PANC", "07R", 4.00)]
    [InlineData("CYTZ", "08", 4.80)]
    [InlineData("CYTZ", "26", 4.80)]
    public void Catalog_RunwaySpecificAngles(string icao, string runway, double expectedDeg)
    {
        Assert.True(SteepApproachGlideslopeCatalog.TryResolve(icao, runway, out var r));
        Assert.Equal(expectedDeg, r.Degrees);
        Assert.Equal(SteepApproachGlideslopeCatalog.SourceCatalog, r.Source);
    }

    [Fact]
    public void Catalog_UnlistedRunwayAtListedAirport_DoesNotMatch()
    {
        // LSZA reverse end is not in the catalog → VASI/default path.
        Assert.False(SteepApproachGlideslopeCatalog.TryResolve("LSZA", "19", out _));
        Assert.False(SteepApproachGlideslopeCatalog.TryResolve("LXGB", "09L", out _));
        Assert.False(SteepApproachGlideslopeCatalog.TryResolve("LOWI", "26", out _));
    }

    [Fact]
    public void Catalog_MissingRunwayId_DoesNotMatch()
    {
        Assert.False(SteepApproachGlideslopeCatalog.TryResolve("LSZA", null, out _));
        Assert.False(SteepApproachGlideslopeCatalog.TryResolve("LSZA", "", out _));
    }

    [Fact]
    public void Catalog_UnknownAirport_ReturnsFalse()
    {
        Assert.False(SteepApproachGlideslopeCatalog.TryResolve("KSEA", "16L", out _));
    }

    [Theory]
    [InlineData("29", "29")]
    [InlineData("rwy 29", "29")]
    [InlineData("RWY29", "29")]
    [InlineData("9", "09")]
    [InlineData("07R", "07R")]
    [InlineData(" 25l ", "25L")]
    [InlineData("Ywy 13", "13")]
    public void NormalizeRunwayId_HandlesCommonForms(string input, string expected)
        => Assert.Equal(expected, SteepApproachGlideslopeCatalog.NormalizeRunwayId(input));

    [Fact]
    public void ResolveFreeFlight_CatalogBeatsVasi_OnListedEndOnly()
    {
        var listed = GlideslopeAngleResolver.ResolveFreeFlight("LSZA", "01", 3.0, 3.0);
        Assert.Equal(6.65, listed.Degrees);
        Assert.Equal(GlideslopeAngleResolver.SourceCatalog, listed.Source);

        // Opposite end: no catalog hit → VASI wins.
        var other = GlideslopeAngleResolver.ResolveFreeFlight("LSZA", "19", 3.0, null);
        Assert.Equal(3.0, other.Degrees);
        Assert.Equal(GlideslopeAngleResolver.SourceVasi, other.Source);
    }

    [Fact]
    public void ResolveFreeFlight_UnknownAirport_UsesVasiThenDefault()
    {
        var vasi = GlideslopeAngleResolver.ResolveFreeFlight("KSEA", "16L", 5.5, null);
        Assert.Equal(5.5, vasi.Degrees);
        Assert.Equal(GlideslopeAngleResolver.SourceVasi, vasi.Source);

        var def = GlideslopeAngleResolver.ResolveFreeFlight("KSEA", "16L", null, null);
        Assert.Equal(3.0, def.Degrees);
        Assert.Equal(GlideslopeAngleResolver.SourceDefault, def.Source);
    }

    [Fact]
    public void BuildEnds_AppliesCatalogOnlyToListedLuganoEnd()
    {
        var airport = new AirportFacility("LSZA", "LS", 46.00, 8.91, 273);
        var runway = new RunwayFacility(
            CenterLatitude: 46.004,
            CenterLongitude: 8.910,
            AltitudeMeters: 273,
            HeadingTrueDeg: 19,
            LengthMeters: 1415,
            WidthMeters: 30,
            Surface: 1,
            PrimaryNumber: 1,
            PrimaryDesignator: 0,
            SecondaryNumber: 19,
            SecondaryDesignator: 0,
            PrimaryClosed: false,
            SecondaryClosed: false,
            PrimaryLandingAllowed: true,
            SecondaryLandingAllowed: true,
            VasiAnglesDeg: [3.0, 3.0, 3.0, 3.0]);

        var ends = RunwayFacilityGeometry.BuildEnds(
            new AirportRunwayFacility(airport, [runway], []));

        var rwy01 = Assert.Single(ends, e => e.RunwayId is "01");
        Assert.Equal(6.65, rwy01.GlideslopeDeg);
        Assert.Equal(SteepApproachGlideslopeCatalog.SourceCatalog, rwy01.GlideslopeSource);

        var rwy19 = Assert.Single(ends, e => e.RunwayId is "19");
        Assert.Equal(3.0, rwy19.GlideslopeDeg);
        Assert.Equal(GlideslopeAngleResolver.SourceVasi, rwy19.GlideslopeSource);
    }

    [Fact]
    public void BuildEnds_UnknownAirport_KeepsVasi()
    {
        var airport = new AirportFacility("KSEA", "K1", 47.45, -122.31, 130);
        var runway = new RunwayFacility(
            CenterLatitude: 47.45,
            CenterLongitude: -122.31,
            AltitudeMeters: 130,
            HeadingTrueDeg: 160,
            LengthMeters: 3000,
            WidthMeters: 45,
            Surface: 1,
            PrimaryNumber: 16,
            PrimaryDesignator: 1,
            SecondaryNumber: 34,
            SecondaryDesignator: 2,
            PrimaryClosed: false,
            SecondaryClosed: false,
            PrimaryLandingAllowed: true,
            SecondaryLandingAllowed: true,
            VasiAnglesDeg: [3.0, null, 3.0, null]);

        var ends = RunwayFacilityGeometry.BuildEnds(
            new AirportRunwayFacility(airport, [runway], []));

        var primary = Assert.Single(ends, e => e.RunwayId == "16L");
        Assert.Equal(3.0, primary.GlideslopeDeg);
        Assert.Equal(GlideslopeAngleResolver.SourceVasi, primary.GlideslopeSource);
    }
}
