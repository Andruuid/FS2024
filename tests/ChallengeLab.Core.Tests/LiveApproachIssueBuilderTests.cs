using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class LiveApproachIssueBuilderTests
{
    private static ChallengeConfig Sllp() => new()
    {
        Id = "test-sllp",
        Title = "Test",
        RequireGearDown = true,
        Runway = new RunwayConfig
        {
            AirportIcao = "SLLP",
            RunwayId = "28",
            ThresholdLatitude = -16.513847,
            ThresholdLongitude = -68.176663,
            HeadingTrueDeg = 271.86,
            ElevationFeet = 13300,
            LengthM = 4000,
            WidthM = 46
        },
        AircraftSetup = new AircraftSetupConfig { VappKts = 155 }
    };

    /// <summary>Point ~2 NM on extended centerline on the nominal 3° path.</summary>
    private static TelemetrySample OnPath(
        double altitudeErrorFt = 0,
        double ias = 155,
        double vsFpm = -700,
        double bank = 0,
        double gear = 1,
        int flaps = 3)
    {
        // 2 NM before threshold along runway heading 271.86° → slightly east of threshold.
        const double distNm = 2.0;
        var headingRad = 271.86 * Math.PI / 180.0;
        // Along approach is opposite runway heading (from threshold outward).
        var northM = Math.Cos(headingRad + Math.PI) * distNm * 1852.0;
        var eastM = Math.Sin(headingRad + Math.PI) * distNm * 1852.0;
        var lat = -16.513847 + (northM / 6_371_000.0) * (180.0 / Math.PI);
        var lon = -68.176663
                  + (eastM / (6_371_000.0 * Math.Cos(-16.513847 * Math.PI / 180.0))) * (180.0 / Math.PI);
        var expectedAlt = 13300 + distNm * 318.0;

        return new TelemetrySample
        {
            Latitude = lat,
            Longitude = lon,
            AltitudeFeet = expectedAlt + altitudeErrorFt,
            AirspeedKts = ias,
            VerticalSpeedFpm = vsFpm,
            BankDeg = bank,
            GearHandlePosition = gear,
            FlapsHandleIndex = flaps,
            SimOnGround = false
        };
    }

    [Fact]
    public void TooHigh_ReportsHighWithDetail()
    {
        var issues = LiveApproachIssueBuilder.Build(
            Sllp(),
            new LandingSnapshot(),
            preview: null,
            OnPath(altitudeErrorFt: 320),
            vappKts: 155,
            targetTouchdownIasKts: 150);

        Assert.Contains(issues, i => i.Label.Contains("high", StringComparison.OrdinalIgnoreCase));
        var line = LiveApproachIssueBuilder.FormatLine(issues);
        Assert.Contains("high", line, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ft", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TooLow_ReportsLow()
    {
        var issues = LiveApproachIssueBuilder.Build(
            Sllp(),
            new LandingSnapshot(),
            preview: null,
            OnPath(altitudeErrorFt: -250),
            vappKts: 155,
            targetTouchdownIasKts: 150);

        Assert.Contains(issues, i => i.Label.Contains("low", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Fast_ReportsTooFastVsVapp()
    {
        var issues = LiveApproachIssueBuilder.Build(
            Sllp(),
            new LandingSnapshot(),
            preview: null,
            OnPath(ias: 185),
            vappKts: 155,
            targetTouchdownIasKts: 150);

        Assert.Contains(issues, i => i.Label.Contains("fast", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CleanPath_NoIssues()
    {
        var issues = LiveApproachIssueBuilder.Build(
            Sllp(),
            new LandingSnapshot(),
            preview: null,
            OnPath(),
            vappKts: 155,
            targetTouchdownIasKts: 150);

        Assert.Empty(issues);
    }

    [Fact]
    public void LowScoredGlideslope_AddsPathOffTag()
    {
        var preview = new ScoreResult
        {
            ChallengeId = "x",
            ChallengeTitle = "x",
            Criteria =
            [
                new CriterionScore
                {
                    Id = "approach_glideslope",
                    DisplayName = "Average glideslope match",
                    Score01 = 0.4,
                    Status = MetricStatus.Scored,
                    MaxOverallPoints = 8
                }
            ]
        };

        var snap = new LandingSnapshot { ApproachGlideslopeMeanAbsFt = 210 };
        var issues = LiveApproachIssueBuilder.Build(
            Sllp(),
            snap,
            preview,
            sample: null);

        Assert.Contains(issues, i => i.Label.Contains("path off", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void GearUpInside3Nm_ReportsGearUp()
    {
        var issues = LiveApproachIssueBuilder.Build(
            Sllp(),
            new LandingSnapshot(),
            preview: null,
            OnPath(gear: 0, flaps: 3),
            vappKts: 155,
            targetTouchdownIasKts: 150);

        Assert.Contains(issues, i => i.Label.Contains("gear", StringComparison.OrdinalIgnoreCase));
    }
}
