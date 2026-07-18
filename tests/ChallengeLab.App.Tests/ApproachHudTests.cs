using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChallengeLab.App.Controls.Hud;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Tests;

[Collection("Wpf")]
public sealed class ApproachHudTests
{
    [Fact]
    public void PresentationFrame_UsesTheLandingMonitorAndSharedWindResultsExactly()
    {
        var sample = Sample();
        var runway = Runway();
        var expectedGuidance = LandingMonitorCalculator.Calculate(sample, runway, 135, .2, 4.5);
        var expectedWind = RelativeWindCalculator.Calculate(sample);

        var frame = HudPresentationFrame.FromSample(sample, true, 17, runway, 135, .2, 4.5);

        Assert.Equal(expectedGuidance, frame.Guidance);
        Assert.Equal(expectedWind, frame.Wind);
        Assert.Equal(17, frame.Sequence);
        Assert.Equal(3, frame.TargetGlideslopeDeg);
    }

    [Fact]
    public void ViewGate_ShowsAlignedCockpitAndHidesFlapsExteriorAndInstruments()
    {
        var runway = Runway(latitude: .01, longitude: 0);

        Assert.True(new HudViewGate().ShouldShow(Frame(Sample(), runway)));
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraPitchRadians: Degrees(-60)), runway)));
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraYawRadians: Degrees(90)), runway)));
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraState: 3), runway)));
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraViewType: 2), runway)));
    }

    [Fact]
    public void ViewGate_FallsBackVisibleWhenTargetOrDirectionTelemetryIsUnavailable()
    {
        Assert.True(new HudViewGate().ShouldShow(Frame(Sample(), runway: null)));
        Assert.True(new HudViewGate().ShouldShow(Frame(
            Sample(cameraPitchRadians: null, cameraYawRadians: null),
            Runway(latitude: .01, longitude: 0))));
    }

    [Fact]
    public void ViewGate_UsesHysteresisAtTheDirectionBoundary()
    {
        var gate = new HudViewGate();
        var runway = Runway(latitude: .01, longitude: 0);

        Assert.True(gate.ShouldShow(Frame(Sample(cameraYawRadians: Degrees(34)), runway)));
        Assert.True(gate.ShouldShow(Frame(Sample(cameraYawRadians: Degrees(42)), runway)));
        Assert.False(gate.ShouldShow(Frame(Sample(cameraYawRadians: Degrees(46)), runway)));
        Assert.False(gate.ShouldShow(Frame(Sample(cameraYawRadians: Degrees(40)), runway)));
        Assert.True(gate.ShouldShow(Frame(Sample(cameraYawRadians: Degrees(34)), runway)));
    }

    [Fact]
    public void ViewGate_StaysVisibleThroughThresholdCrossingInAForwardCockpitView()
    {
        var gate = new HudViewGate();
        var thresholdJustBehindAircraft = Runway(latitude: -0.0001, longitude: 0);
        var aircraftAtThreshold = Runway(latitude: 0, longitude: 0);

        Assert.True(gate.ShouldShow(Frame(Sample(), thresholdJustBehindAircraft)));
        Assert.True(gate.ShouldShow(Frame(Sample(), aircraftAtThreshold)));
        Assert.False(gate.ShouldShow(Frame(
            Sample(cameraYawRadians: Degrees(90)), thresholdJustBehindAircraft)));
        Assert.False(gate.ShouldShow(Frame(
            Sample(cameraPitchRadians: Degrees(-60)), aircraftAtThreshold)));
    }

    [Fact]
    public void ViewGate_HidesDisconnectedOrInactiveFlightFrames()
    {
        var gate = new HudViewGate();

        Assert.False(gate.ShouldShow(HudPresentationFrame.Disconnected(1)));
        Assert.False(gate.ShouldShow(HudPresentationFrame.FromSample(
            new TelemetrySample
            {
                SimOnGround = true,
                CameraState = 2,
                CameraGameplayPitchRadians = 0,
                CameraGameplayYawRadians = 0,
            },
            true,
            2,
            null,
            null,
            .2,
            4.5)));
    }

    [Fact]
    public void Renderer_PaintsFiveEdgeRegionsAndLeavesCenterTransparent()
    {
        RunSta(() =>
        {
            var visual = new HudVisual();
            visual.UpdatePresentation(Frame(Sample(), Runway()));
            visual.UpdateScale(1);
            visual.Measure(new Size(HudVisual.DesignWidth, HudVisual.DesignHeight));
            visual.Arrange(new Rect(0, 0, HudVisual.DesignWidth, HudVisual.DesignHeight));
            visual.UpdateLayout();

            var bitmap = new RenderTargetBitmap(
                (int)HudVisual.DesignWidth,
                (int)HudVisual.DesignHeight,
                96,
                96,
                PixelFormats.Pbgra32);
            bitmap.Render(visual);

            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(670, 80, 320, 90)));
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(445, 300, 220, 90)));
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(445, 420, 220, 90)));
            Assert.False(HasVisiblePixel(bitmap, new Int32Rect(995, 360, 190, 30)));
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(995, 395, 190, 50)));
            Assert.False(HasVisiblePixel(bitmap, new Int32Rect(995, 447, 190, 25)));
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(710, 640, 180, 80)));
            Assert.False(HasVisiblePixel(bitmap, new Int32Rect(680, 220, 280, 360)));
        });
    }

    [Fact]
    public void RendererScale_ClampsToSliderRange()
    {
        RunSta(() =>
        {
            var visual = new HudVisual();

            visual.UpdateScale(0.1);
            Assert.Equal(0.55, visual.HudScale);
            visual.UpdateScale(2);
            Assert.Equal(1.25, visual.HudScale);
            visual.UpdateOpacity(0.05);
            Assert.Equal(0.2, visual.HudOpacity);
            visual.UpdateOpacity(2);
            Assert.Equal(1.0, visual.HudOpacity);
        });
    }

    [Fact]
    public void Renderer_HidesRunwayGuidanceUntilAnAirportIsDetected()
    {
        RunSta(() =>
        {
            var bitmap = Render(Frame(Sample(), runway: null));

            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(670, 80, 320, 90)));
            Assert.False(HasVisiblePixel(bitmap, new Int32Rect(445, 300, 220, 210)));
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(995, 390, 190, 60)));
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(710, 640, 180, 80)));
        });
    }

    [Fact]
    public void Renderer_ShowsDirectionArrowAndNumericSpeedForCalmWind()
    {
        RunSta(() =>
        {
            var sample = Sample(windSpeedKts: 0.1, windDirectionDeg: 90);
            var wind = RelativeWindCalculator.Calculate(sample);
            var bitmap = Render(Frame(sample, Runway()));

            Assert.False(wind.HasWind);
            Assert.Equal("0.1 KT", HudVisual.FormatWindSpeed(wind));
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(724, 115, 8, 10)));
        });
    }

    private static HudPresentationFrame Frame(TelemetrySample sample, RunwayConfig? runway) =>
        HudPresentationFrame.FromSample(sample, true, 1, runway, 135, .2, 4.5);

    private static TelemetrySample Sample(
        double? cameraPitchRadians = 0,
        double? cameraYawRadians = 0,
        int? cameraState = 2,
        int? cameraViewType = 1,
        double windSpeedKts = 20,
        double windDirectionDeg = 90) => new()
        {
            Timestamp = DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            Latitude = 0,
            Longitude = 0,
            AltitudeFeet = 0,
            AglFeet = 1_000,
            HeadingTrueDeg = 0,
            PitchDeg = 0,
            AirspeedKts = 135,
            GroundSpeedKts = 130,
            VerticalSpeedFpm = -700,
            WindDirectionDeg = windDirectionDeg,
            WindVelocityKts = windSpeedKts,
            SimOnGround = false,
            AircraftTitle = "Test Aircraft",
            CameraState = cameraState,
            CameraViewType = cameraViewType,
            CameraGameplayPitchRadians = cameraPitchRadians,
            CameraGameplayYawRadians = cameraYawRadians,
        };

    private static RunwayConfig Runway(double latitude = .02, double longitude = 0) => new()
    {
        AirportIcao = "TEST",
        RunwayId = "36",
        ThresholdLatitude = latitude,
        ThresholdLongitude = longitude,
        ElevationFeet = 0,
        HeadingTrueDeg = 0,
        GlideslopeDeg = 3,
        LengthM = 3_000,
        WidthM = 45,
    };

    private static double Degrees(double value) => value * Math.PI / 180.0;

    private static bool HasVisiblePixel(BitmapSource bitmap, Int32Rect rectangle)
    {
        const int bytesPerPixel = 4;
        var stride = rectangle.Width * bytesPerPixel;
        var pixels = new byte[stride * rectangle.Height];
        bitmap.CopyPixels(rectangle, pixels, stride, 0);
        for (var index = 3; index < pixels.Length; index += bytesPerPixel)
        {
            if (pixels[index] > 4)
                return true;
        }
        return false;
    }

    private static BitmapSource Render(HudPresentationFrame frame)
    {
        var visual = new HudVisual();
        visual.UpdatePresentation(frame);
        visual.UpdateScale(1);
        visual.Measure(new Size(HudVisual.DesignWidth, HudVisual.DesignHeight));
        visual.Arrange(new Rect(0, 0, HudVisual.DesignWidth, HudVisual.DesignHeight));
        visual.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)HudVisual.DesignWidth,
            (int)HudVisual.DesignHeight,
            96,
            96,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
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
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
