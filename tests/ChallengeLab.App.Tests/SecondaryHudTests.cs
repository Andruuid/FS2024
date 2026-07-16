using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChallengeLab.App.Controls;
using ChallengeLab.App.ViewModels;
using ChallengeLab.App.Views;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Tests;

[CollectionDefinition("Wpf", DisableParallelization = true)]
public sealed class WpfCollection;

[Collection("Wpf")]
public sealed class SecondaryHudTests
{
    [Fact]
    public void MonitorState_StartsInWindow_ThrottlesGraph_EstimatesAndResets()
    {
        RunSta(() =>
        {
            var monitor = new SecondaryHudViewModel();
            var challenge = Challenge();
            var settings = Settings();
            var start = DateTimeOffset.Parse("2026-07-15T10:00:00Z");

            monitor.Update(Sample(5, start), challenge, settings, 135, 100, LandingPhase.Approach, true);
            Assert.False(monitor.IsCollecting);
            Assert.Empty(monitor.GraphPoints);

            monitor.Update(Sample(4.5, start.AddSeconds(1)), challenge, settings, 135, 98, LandingPhase.Approach, true);
            Assert.True(monitor.IsCollecting);
            Assert.Single(monitor.GraphPoints);
            Assert.Equal("03:07", monitor.EtaDisplay);
            Assert.InRange(monitor.GraphHorizonSeconds, 196.9, 197.2);

            monitor.Update(Sample(4.4, start.AddSeconds(1.1)), challenge, settings, 135, 96, LandingPhase.Approach, true);
            Assert.Single(monitor.GraphPoints);
            monitor.Update(Sample(4.3, start.AddSeconds(1.25)), challenge, settings, 135, 94, LandingPhase.Approach, true);
            Assert.Equal(2, monitor.GraphPoints.Count);

            monitor.Update(Sample(4.2, start.AddSeconds(2), heading: 270), challenge, settings, 135, 90, LandingPhase.Approach, true);
            Assert.Equal("--:--", monitor.EtaDisplay);
            Assert.InRange(monitor.GraphHorizonSeconds, 196.9, 197.2);

            monitor.CompleteAttempt(87, start.AddSeconds(10));
            Assert.Equal("00:00", monitor.EtaDisplay);
            Assert.Equal(87, monitor.GraphPoints[^1].ScorePercent);

            monitor.ResetAttempt();
            Assert.False(monitor.IsCollecting);
            Assert.Empty(monitor.GraphPoints);
            Assert.Equal(30, monitor.GraphHorizonSeconds);
        });
    }

    [Theory]
    [InlineData(0, 0, 10, 0, "From 0°", "Headwind 10.0 kt", "Crosswind 0.0 kt")]
    [InlineData(0, 180, 10, 180, "From 180°", "Tailwind 10.0 kt", "Crosswind 0.0 kt")]
    [InlineData(0, 90, 10, 90, "From 90°", "Headwind 0.0 kt", "Crosswind R 10.0 kt")]
    [InlineData(0, 270, 10, -90, "From 270°", "Headwind 0.0 kt", "Crosswind L 10.0 kt")]
    [InlineData(350, 10, 10, 20, "From 10°", "Headwind 9.4 kt", "Crosswind R 3.4 kt")]
    public void WindIndicator_UsesAircraftRelativeFromAngleAndComponents(
        double heading,
        double windDirection,
        double windSpeed,
        double expectedAngle,
        string expectedFrom,
        string expectedLongitudinal,
        string expectedCrosswind)
    {
        RunSta(() =>
        {
            var monitor = new SecondaryHudViewModel();
            monitor.Update(
                Sample(5, DateTimeOffset.UtcNow, heading: heading, windDirection: windDirection, windSpeed: windSpeed),
                null,
                null,
                null,
                null,
                LandingPhase.Idle,
                true);

            Assert.True(monitor.HasWind);
            Assert.Equal(expectedAngle, monitor.WindRelativeFromAngleDeg, 6);
            Assert.Equal(windSpeed, monitor.WindSpeedKts, 6);
            Assert.Equal(expectedFrom, monitor.WindFromDisplay);
            Assert.Equal(expectedLongitudinal, monitor.WindLongitudinalDisplay);
            Assert.Equal(expectedCrosswind, monitor.WindCrosswindDisplay);
            Assert.Equal($"Wind {windSpeed:0.0} kt", monitor.WindTotalDisplay);
        });
    }

    [Fact]
    public void WindIndicator_CalmAndDisconnectedStatesStopAnimationData()
    {
        RunSta(() =>
        {
            var monitor = new SecondaryHudViewModel();
            monitor.Update(
                Sample(5, DateTimeOffset.UtcNow, heading: 20, windDirection: 80, windSpeed: 0.4),
                null,
                null,
                null,
                null,
                LandingPhase.Idle,
                true);

            Assert.False(monitor.HasWind);
            Assert.Equal("Calm", monitor.WindFromDisplay);
            Assert.Equal("Wind 0.4 kt", monitor.WindTotalDisplay);

            monitor.SetDisconnected();

            Assert.False(monitor.HasWind);
            Assert.Equal(0, monitor.WindSpeedKts);
            Assert.Equal("From —", monitor.WindFromDisplay);
            Assert.Equal("Wind —", monitor.WindTotalDisplay);
        });
    }

    [Fact]
    public void MonitorGraph_RetainsAtMostTenMinutesAndTwentyFourHundredPoints()
    {
        RunSta(() =>
        {
            var monitor = new SecondaryHudViewModel();
            var challenge = Challenge();
            var settings = Settings();
            var start = DateTimeOffset.Parse("2026-07-15T10:00:00Z");

            for (var i = 0; i <= 2500; i++)
            {
                monitor.Update(
                    Sample(4, start.AddSeconds(i * .25)),
                    challenge,
                    settings,
                    135,
                    90 + i % 10,
                    LandingPhase.Approach,
                    true);
            }

            Assert.InRange(monitor.GraphPoints.Count, 2399, 2400);
            Assert.True(
                monitor.GraphPoints[^1].ElapsedSeconds - monitor.GraphPoints[0].ElapsedSeconds <= 600);
        });
    }

    [Fact]
    public void PositionClamp_KeepsWindowInsideVirtualDesktop()
    {
        RunSta(() =>
        {
            var clamped = SecondaryHudWindow.ClampToVisibleDesktop(
                new SecondaryHudPosition(double.MaxValue, double.MinValue),
                420,
                560);
            Assert.InRange(
                clamped.Left,
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 420);
            Assert.InRange(
                clamped.Top,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 560);
        });
    }

    [Fact]
    public void ScoreGraph_RendersPointSnapshotWithoutAChartDependency()
    {
        RunSta(() =>
        {
            var graph = new LandingScoreGraph
            {
                Width = 360,
                Height = 160,
                HorizonSeconds = 40,
                Points =
                [
                    new ScoreHistoryPoint(0, 100),
                    new ScoreHistoryPoint(10, 92),
                    new ScoreHistoryPoint(20, 86)
                ]
            };
            graph.Measure(new Size(360, 160));
            graph.Arrange(new Rect(0, 0, 360, 160));
            var bitmap = new RenderTargetBitmap(360, 160, 96, 96, PixelFormats.Pbgra32);

            bitmap.Render(graph);

            Assert.Equal(360, bitmap.PixelWidth);
            Assert.Equal(160, bitmap.PixelHeight);
        });
    }

    [Fact]
    public void WindFlowIndicator_RendersAircraftAndWindWithoutAnImageAsset()
    {
        RunSta(() =>
        {
            var indicator = new WindFlowIndicator
            {
                Width = 72,
                Height = 72,
                RelativeFromAngle = 90,
                WindSpeedKts = 12,
                IsActive = true
            };
            indicator.Measure(new Size(72, 72));
            indicator.Arrange(new Rect(0, 0, 72, 72));
            var bitmap = new RenderTargetBitmap(72, 72, 96, 96, PixelFormats.Pbgra32);

            bitmap.Render(indicator);

            var pixels = new byte[72 * 72 * 4];
            bitmap.CopyPixels(pixels, 72 * 4, 0);
            Assert.Contains(pixels.Where((_, index) => index % 4 == 3), alpha => alpha > 0);
        });
    }

    private static ChallengeConfig Challenge() => new()
    {
        Id = "monitor-test",
        Title = "Monitor test",
        Runway = new RunwayConfig
        {
            AirportIcao = "TEST",
            RunwayId = "09",
            ThresholdLatitude = 0,
            ThresholdLongitude = 0,
            HeadingTrueDeg = 90,
            ElevationFeet = 100
        }
    };

    private static LandingSessionSettings Settings() => new(
        35, 1, 2, 50, 4, true, 80, 8, .2, 4.5, 143, 5, 1.3);

    private static TelemetrySample Sample(
        double distanceNm,
        DateTimeOffset timestamp,
        double heading = 120,
        double windDirection = 0,
        double windSpeed = 0)
    {
        var distanceMeters = distanceNm * RunwayPathGeometry.MetersPerNauticalMile;
        var longitude = -distanceMeters / RunwayPathGeometry.EarthRadiusMeters * 180 / Math.PI;
        var heightFeet = Math.Tan(3 * Math.PI / 180)
                         * distanceMeters * LandingMonitorCalculator.FeetPerMeter;
        return new TelemetrySample
        {
            Timestamp = timestamp,
            Latitude = 0,
            Longitude = longitude,
            AltitudeFeet = 100 + heightFeet,
            AglFeet = heightFeet,
            RadioHeightFeet = heightFeet,
            AirspeedKts = 135,
            GroundSpeedKts = 100,
            HeadingTrueDeg = heading,
            VerticalSpeedFpm = -700,
            WindDirectionDeg = windDirection,
            WindVelocityKts = windSpeed,
            SimOnGround = false
        };
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

}
