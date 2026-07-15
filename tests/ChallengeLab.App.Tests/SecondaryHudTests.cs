using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChallengeLab.App.Controls;
using ChallengeLab.App.ViewModels;
using ChallengeLab.App.Views;
using ChallengeLab.Core.Config;
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
            Assert.Equal("STANDBY", monitor.ProgressDisplay);

            monitor.Update(Sample(4.5, start.AddSeconds(1)), challenge, settings, 135, 98, LandingPhase.Approach, true);
            Assert.True(monitor.IsCollecting);
            Assert.Single(monitor.GraphPoints);
            Assert.Equal("02:42", monitor.EtaDisplay);
            Assert.Equal(0, monitor.ProgressPercent, 1);
            Assert.InRange(monitor.GraphHorizonSeconds, 171.9, 172.1);

            monitor.Update(Sample(4.4, start.AddSeconds(1.1)), challenge, settings, 135, 96, LandingPhase.Approach, true);
            Assert.Single(monitor.GraphPoints);
            monitor.Update(Sample(4.3, start.AddSeconds(1.25)), challenge, settings, 135, 94, LandingPhase.Approach, true);
            Assert.Equal(2, monitor.GraphPoints.Count);

            monitor.Update(Sample(4.2, start.AddSeconds(2), track: 270), challenge, settings, 135, 90, LandingPhase.Approach, true);
            Assert.Equal("--:--", monitor.EtaDisplay);
            Assert.InRange(monitor.GraphHorizonSeconds, 171.9, 172.1);

            monitor.CompleteAttempt(87, start.AddSeconds(10));
            Assert.Equal(100, monitor.ProgressPercent);
            Assert.Equal("00:00", monitor.EtaDisplay);
            Assert.Equal(87, monitor.GraphPoints[^1].ScorePercent);

            monitor.ResetAttempt();
            Assert.False(monitor.IsCollecting);
            Assert.Empty(monitor.GraphPoints);
            Assert.Equal("STANDBY", monitor.ProgressDisplay);
            Assert.Equal(30, monitor.GraphHorizonSeconds);
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
                    new LandingMonitorGraphPoint(0, 100),
                    new LandingMonitorGraphPoint(10, 92),
                    new LandingMonitorGraphPoint(20, 86)
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
        35, 1, 3, 3, 2, 50, 4, true, 80, 8, .2, 4.5, 143, 5, 1.3);

    private static TelemetrySample Sample(
        double distanceNm,
        DateTimeOffset timestamp,
        double track = 90)
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
            GroundTrackTrueDeg = track,
            HeadingTrueDeg = 120,
            VerticalSpeedFpm = -700,
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
