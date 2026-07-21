using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChallengeLab.App.Controls;
using ChallengeLab.App.Controls.Aether;
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
        Assert.Equal(1_000, frame.RadioAltitudeFeet);
    }

    [Fact]
    public void PresentationFrame_UsesOnlyAvailableFiniteRadioHeightAndClampsBelowGround()
    {
        Assert.Equal(247.6, Frame(Sample(radioHeightFeet: 247.6), Runway()).RadioAltitudeFeet);
        Assert.Equal(0, Frame(Sample(radioHeightFeet: -12), Runway()).RadioAltitudeFeet);
        Assert.Null(Frame(Sample(radioHeightFeet: 247.6, radioHeightAvailable: false), Runway())
            .RadioAltitudeFeet);
        Assert.Null(Frame(Sample(radioHeightFeet: double.NaN), Runway()).RadioAltitudeFeet);

        Assert.Equal("248", HudVisual.FormatRadioAltitude(247.6));
        Assert.Equal("0", HudVisual.FormatRadioAltitude(-12));
        Assert.Equal("—", HudVisual.FormatRadioAltitude(null));
        Assert.Equal("—", HudVisual.FormatRadioAltitude(double.NaN));
    }

    [Fact]
    public void CrabAnglePresentation_UsesHeadingMinusTrackAndRequiresMoreThanHalfDegree()
    {
        Assert.Null(CrabAnglePresentation.FromSample(Sample(
            headingTrueDeg: 10,
            groundTrackTrueDeg: 9.5)));
        Assert.Null(CrabAnglePresentation.FromSample(Sample(
            headingTrueDeg: 10,
            groundTrackTrueDeg: null)));

        var right = CrabAnglePresentation.FromSample(Sample(
            headingTrueDeg: 12.4,
            groundTrackTrueDeg: 10));
        var left = CrabAnglePresentation.FromSample(Sample(
            headingTrueDeg: 7.6,
            groundTrackTrueDeg: 10));

        Assert.Equal(2.4, right!.Value, 6);
        Assert.Equal(-2.4, left!.Value, 6);
        Assert.Equal("CRAB R 2.4°", CrabAnglePresentation.Format(right!.Value));
        Assert.Equal("CRAB L 2.4°", CrabAnglePresentation.Format(left!.Value));
        Assert.Equal("CRAB R", CrabAnglePresentation.FormatDirection(right.Value));
        Assert.Equal("2.4°", CrabAnglePresentation.FormatMagnitude(right.Value));
    }

    [Theory]
    [InlineData(-16.1, LandingMonitorStatus.Red)]
    [InlineData(-16, LandingMonitorStatus.Orange)]
    [InlineData(-8.1, LandingMonitorStatus.Orange)]
    [InlineData(-8, LandingMonitorStatus.Green)]
    [InlineData(5, LandingMonitorStatus.Green)]
    [InlineData(5.1, LandingMonitorStatus.Orange)]
    [InlineData(10, LandingMonitorStatus.Orange)]
    [InlineData(10.1, LandingMonitorStatus.Red)]
    public void BothHudAirspeedBands_AreAsymmetricAroundVapp(
        double deltaKts,
        LandingMonitorStatus expected)
    {
        var speed = ApproachSpeedPresentation.Calculate(140 + deltaKts, 140);

        Assert.Equal(140, speed.VappKts);
        Assert.Equal(deltaKts, speed.DeltaKts!.Value, 6);
        Assert.Equal(expected, speed.Status);
    }

    [Fact]
    public void BothHudFramesUseVappForTheLiveIasToneAndLabel()
    {
        var sample = Sample(airspeedKts: 146);
        var guidance = LandingMonitorCalculator.Calculate(sample, Runway(), 135, .2, 4.5);
        var hudFrame = HudPresentationFrame.FromGuidance(sample, true, 1, Runway(), guidance, 140);
        var aetherFrame = AetherMapper.FromGuidance(sample, true, 1, Runway(), guidance, 140);

        Assert.Equal(LandingMonitorStatus.Orange, hudFrame.ApproachSpeed.Status);
        Assert.Equal(140, hudFrame.ApproachSpeed.VappKts);
        Assert.Equal(AetherTone.Caution, aetherFrame.Energy.IasTone);
        Assert.Equal(140, aetherFrame.Energy.VappKts);
        Assert.Equal(6, aetherFrame.Energy.IasDeltaKts);
    }

    [Fact]
    public void BothHudFramesReceiveTheSameLiveCrabAngle()
    {
        var sample = Sample(headingTrueDeg: 3, groundTrackTrueDeg: 1);
        var hudFrame = Frame(sample, Runway());
        var aetherFrame = AetherMapper.FromGuidance(
            sample,
            isConnected: true,
            sequence: 1,
            runway: Runway(),
            guidance: hudFrame.Guidance);

        Assert.Equal(2, hudFrame.CrabAngleDeg!.Value, 6);
        Assert.Equal(2, aetherFrame.Wind.CrabAngleDeg!.Value, 6);
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
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(
                cameraPitchRadians: null,
                cameraYawRadians: null,
                cameraSubstate: 2),
            Runway(latitude: .01, longitude: 0))));
    }

    [Fact]
    public void ViewGate_HidesSideAndDownwardLooksWithoutRunwayTarget()
    {
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraYawRadians: Degrees(90)), runway: null)));
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraPitchRadians: Degrees(-60)), runway: null)));
    }

    [Fact]
    public void ViewGate_RequiresFarRunwayToRemainInsideTheForwardLandingCone()
    {
        var runway = Runway(latitude: .02, longitude: 0);

        Assert.True(new HudViewGate().ShouldShow(Frame(Sample(headingTrueDeg: 0), runway)));
        Assert.False(new HudViewGate().ShouldShow(Frame(Sample(headingTrueDeg: 90), runway)));
    }

    [Fact]
    public void BothHudGates_HideStaleUnlockedControllerLookUntilCameraReset()
    {
        var hudGate = new HudViewGate();
        var aetherGate = new AetherLookPolicy();
        var unlocked = Sample(cameraSubstate: 2);
        var unlockedFrame = Frame(unlocked, runway: null);
        var unlockedAether = AetherMapper.FromGuidance(
            unlocked,
            isConnected: true,
            sequence: 1,
            runway: null,
            unlockedFrame.Guidance);

        Assert.False(hudGate.ShouldShow(unlockedFrame));
        Assert.False(aetherGate.ShouldRender(unlockedAether));
        Assert.False(hudGate.ShouldShow(unlockedFrame));
        Assert.False(aetherGate.ShouldRender(unlockedAether));

        var reset = Sample(cameraSubstate: 1);
        var resetFrame = Frame(reset, runway: null);
        var resetAether = AetherMapper.FromGuidance(
            reset,
            isConnected: true,
            sequence: 2,
            runway: null,
            resetFrame.Guidance);

        Assert.True(hudGate.ShouldShow(resetFrame));
        Assert.True(aetherGate.ShouldRender(resetAether));
    }

    [Fact]
    public void ViewGate_HidesQuickviewSmartAndInstrumentCameraModes()
    {
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraViewType: 3), Runway())));
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraSubstate: 3), Runway())));
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraSubstate: 4), Runway())));
        Assert.False(new HudViewGate().ShouldShow(Frame(
            Sample(cameraSubstate: 5), Runway())));
    }

    [Fact]
    public void ViewGate_UsesUsableAnglesEvenWhenCameraIsUnlocked()
    {
        Assert.True(new HudViewGate().ShouldShow(Frame(
            Sample(cameraSubstate: 2, cameraYawRadians: Degrees(1)), Runway())));
    }

    [Fact]
    public void ViewGate_UsesHysteresisAtTheDirectionBoundary()
    {
        var gate = new HudViewGate();
        var runway = Runway(latitude: .01, longitude: 0);

        Assert.True(gate.ShouldShow(Frame(Sample(
            cameraYawRadians: Degrees(HudViewGate.EnterHorizontalDegrees - 1)), runway)));
        Assert.True(gate.ShouldShow(Frame(Sample(
            cameraYawRadians: Degrees(HudViewGate.ExitHorizontalDegrees - 1)), runway)));
        Assert.False(gate.ShouldShow(Frame(Sample(
            cameraYawRadians: Degrees(HudViewGate.ExitHorizontalDegrees + 1)), runway)));
        Assert.False(gate.ShouldShow(Frame(Sample(
            cameraYawRadians: Degrees(HudViewGate.EnterHorizontalDegrees + 1)), runway)));
        Assert.True(gate.ShouldShow(Frame(Sample(
            cameraYawRadians: Degrees(HudViewGate.EnterHorizontalDegrees - 1)), runway)));
    }

    [Fact]
    public void Hud1ViewGate_UsesWiderUpwardHysteresis()
    {
        var gate = new HudViewGate();

        Assert.Equal(35, HudViewGate.EnterUpwardDegrees);
        Assert.Equal(45, HudViewGate.ExitUpwardDegrees);
        Assert.True(gate.ShouldShow(Frame(Sample(), runway: null)));
        Assert.True(gate.ShouldShow(Frame(Sample(
            cameraPitchRadians: Degrees(HudViewGate.ExitUpwardDegrees - 1)), runway: null)));
        Assert.False(gate.ShouldShow(Frame(Sample(
            cameraPitchRadians: Degrees(HudViewGate.ExitUpwardDegrees + 1)), runway: null)));
        Assert.False(gate.ShouldShow(Frame(Sample(
            cameraPitchRadians: Degrees(HudViewGate.EnterUpwardDegrees + 1)), runway: null)));
        Assert.True(gate.ShouldShow(Frame(Sample(
            cameraPitchRadians: Degrees(HudViewGate.EnterUpwardDegrees - 1)), runway: null)));
    }

    [Fact]
    public void WiderUpwardLookTolerance_AppliesOnlyToHud1()
    {
        var sample = Sample(cameraPitchRadians: Degrees(30));
        var hudFrame = Frame(sample, runway: null);
        var aetherFrame = AetherMapper.FromGuidance(
            sample,
            isConnected: true,
            sequence: 1,
            runway: null,
            guidance: hudFrame.Guidance);

        Assert.True(new HudViewGate().ShouldShow(hudFrame));
        Assert.False(new AetherLookPolicy().ShouldRender(aetherFrame));
    }

    [Fact]
    public void ViewGate_UsesStricterHysteresisWhenLookingDownAtThePanel()
    {
        var gate = new HudViewGate();
        var runway = Runway(latitude: .01, longitude: 0);

        Assert.True(gate.ShouldShow(Frame(Sample(), runway)));
        Assert.True(gate.ShouldShow(Frame(Sample(
            cameraPitchRadians: Degrees(-(HudViewGate.ExitDownwardDegrees - 1))), runway)));
        Assert.False(gate.ShouldShow(Frame(Sample(
            cameraPitchRadians: Degrees(-(HudViewGate.ExitDownwardDegrees + 1))), runway)));
        Assert.False(gate.ShouldShow(Frame(Sample(
            cameraPitchRadians: Degrees(-(HudViewGate.EnterDownwardDegrees + 1))), runway)));
        Assert.True(gate.ShouldShow(Frame(Sample(
            cameraPitchRadians: Degrees(-(HudViewGate.EnterDownwardDegrees - 1))), runway)));
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
    public void Renderer_PaintsSixEdgeRegionsAndLeavesCenterTransparent()
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
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(995, 472, 220, 60)));
            Assert.True(HasVisiblePixel(bitmap, new Int32Rect(710, 640, 180, 80)));
            Assert.False(HasVisiblePixel(bitmap, new Int32Rect(680, 220, 280, 360)));
        });
    }

    [Fact]
    public void Hud1VerticalSpeed_UsesOneAndAHalfSecondTrailingAverageAndResets()
    {
        var start = DateTimeOffset.Parse("2026-07-18T12:00:00Z");
        var smoother = new HudVerticalSpeedSmoother();

        Assert.Equal(-900, smoother.Update(start, -900));
        Assert.Equal(-750, smoother.Update(start.AddSeconds(.4), -600));
        Assert.Equal(-900, smoother.Update(start.AddSeconds(1.4), -1_200));
        Assert.Equal(-700, smoother.Update(start.AddSeconds(1.6), -300));

        Assert.Null(smoother.Update(start.AddSeconds(1.7), double.NaN));
        Assert.Equal(-500, smoother.Update(start.AddSeconds(1.8), -500));
        Assert.Equal(-200, smoother.Update(start.AddSeconds(1.0), -200));
    }

    [Fact]
    public void Hud1VerticalSpeed_UpdatesOncePerFrameAndClearsWhenDisconnected()
    {
        RunSta(() =>
        {
            var start = DateTimeOffset.Parse("2026-07-18T12:00:00Z");
            var first = Frame(Sample(), Runway()) with
            {
                Sequence = 10,
                CapturedAt = start,
                Guidance = Frame(Sample(), Runway()).Guidance with { VerticalSpeedFpm = -1_000 },
            };
            var second = first with
            {
                Sequence = 11,
                CapturedAt = start.AddSeconds(.5),
                Guidance = first.Guidance with { VerticalSpeedFpm = -500 },
            };
            var visual = new HudVisual();

            visual.UpdatePresentation(first);
            Assert.Equal(-1_000, visual.DisplayVerticalSpeedFpm);
            visual.UpdatePresentation(first with
            {
                Guidance = first.Guidance with { VerticalSpeedFpm = 5_000 },
            });
            Assert.Equal(-1_000, visual.DisplayVerticalSpeedFpm);
            visual.UpdatePresentation(second);
            Assert.Equal(-750, visual.DisplayVerticalSpeedFpm);

            visual.UpdatePresentation(HudPresentationFrame.Disconnected(12));
            Assert.Null(visual.DisplayVerticalSpeedFpm);
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
    public void AetherFontScale_ClampsIndependentlyFromInstrumentScale()
    {
        RunSta(() =>
        {
            var surface = new AetherSurface();
            Assert.Equal(1.1, surface.FontScale);
            surface.SetDisplayScale(0.9);

            surface.SetFontScale(0.1);
            Assert.Equal(0.75, surface.FontScale);
            Assert.Equal(0.9, surface.DisplayScale);

            surface.SetFontScale(2);
            Assert.Equal(1.35, surface.FontScale);
            Assert.Equal(0.9, surface.DisplayScale);
        });
    }

    [Fact]
    public void Hud1FontScale_ClampsIndependentlyFromInstrumentScale()
    {
        RunSta(() =>
        {
            var visual = new HudVisual();
            Assert.Equal(1.1, visual.FontScale);
            visual.UpdateScale(0.9);

            visual.UpdateFontScale(0.1);
            Assert.Equal(0.75, visual.FontScale);
            Assert.Equal(0.9, visual.HudScale);

            visual.UpdateFontScale(2);
            Assert.Equal(1.35, visual.FontScale);
            Assert.Equal(0.9, visual.HudScale);
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
        int? cameraSubstate = null,
        int? cameraViewType = 1,
        double headingTrueDeg = 0,
        double? groundTrackTrueDeg = null,
        double airspeedKts = 135,
        double windSpeedKts = 20,
        double windDirectionDeg = 90,
        double radioHeightFeet = 1_000,
        bool radioHeightAvailable = true) => new()
        {
            Timestamp = DateTimeOffset.Parse("2026-07-18T12:00:00Z"),
            Latitude = 0,
            Longitude = 0,
            AltitudeFeet = 0,
            AglFeet = 1_000,
            HeadingTrueDeg = headingTrueDeg,
            GroundTrackTrueDeg = groundTrackTrueDeg,
            PitchDeg = 0,
            AirspeedKts = airspeedKts,
            GroundSpeedKts = 130,
            VerticalSpeedFpm = -700,
            WindDirectionDeg = windDirectionDeg,
            WindVelocityKts = windSpeedKts,
            RadioHeightFeet = radioHeightFeet,
            RadioHeightAvailable = radioHeightAvailable,
            SimOnGround = false,
            AircraftTitle = "Test Aircraft",
            CameraState = cameraState,
            CameraSubstate = cameraSubstate,
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
