using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using ChallengeLab.Core.Models;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfFlowDirection = System.Windows.FlowDirection;

namespace ChallengeLab.App.Controls;

/// <summary>Responsive standards-based runway schematic with an exact runway-relative touchdown marker.</summary>
public sealed class PrecisionRunwayView : FrameworkElement
{
    private const double FeetPerMeter = 3.280839895;
    private const double MinimumLandingZoneDisplayFeet = 2_500;
    private const double RunwayDisplayFraction = 1.0 / 3.0;
    private const double TargetTrailingContextFeet = 800;
    private static readonly Brush GroundBrush = Gradient(
        Color.FromRgb(0x0A, 0x1D, 0x1B), Color.FromRgb(0x12, 0x2B, 0x25));
    private static readonly Brush RunwayBrush = Gradient(
        Color.FromRgb(0x3A, 0x42, 0x49), Color.FromRgb(0x21, 0x28, 0x31));
    private static readonly Brush WhiteBrush = FrozenBrush(Color.FromRgb(0xF2, 0xF4, 0xED));
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromRgb(0x8B, 0x9B, 0xB8));
    private static readonly Brush CyanBrush = FrozenBrush(Color.FromRgb(0x2D, 0xE2, 0xE6));
    private static readonly Brush AmberBrush = FrozenBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
    private static readonly Brush GreenBrush = FrozenBrush(Color.FromRgb(0x62, 0xE6, 0xA7));
    private static readonly Brush IdealBandBrush = FrozenBrush(Color.FromArgb(0x38, 0x62, 0xE6, 0xA7));
    private static readonly Pen EdgePen = FrozenPen(Color.FromArgb(0xD8, 0xF2, 0xF4, 0xED), 1.4);
    private static readonly Pen FaintPen = FrozenPen(Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen CenterGuidePen = DashedPen(Color.FromArgb(0xB0, 0x2D, 0xE2, 0xE6), 1.2, 3, 3);
    private static readonly Pen TargetPen = FrozenPen(Color.FromArgb(0xE0, 0x62, 0xE6, 0xA7), 2);
    private static readonly StreamGeometry AircraftGeometry = CreateAircraftGeometry();

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(LandingVisualizationData),
        typeof(PrecisionRunwayView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public LandingVisualizationData? Data
    {
        get => (LandingVisualizationData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 260 || ActualHeight < 190)
            return;

        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(GroundBrush, FaintPen, bounds, 14, 14);
        DrawGroundTexture(dc, bounds);

        var data = Data;
        if (data is null || data.RunwayLengthM <= 0 || data.RunwayWidthM <= 0)
            return;

        var layout = CalculateLayout(data, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(
            FrozenBrush(Color.FromArgb(0x55, 0x00, 0x00, 0x00)),
            null,
            new Rect(layout.Runway.X + 5, layout.Runway.Y + 8, layout.Runway.Width, layout.Runway.Height),
            3,
            3);
        dc.DrawRectangle(RunwayBrush, EdgePen, layout.Runway);
        DrawRunwayTexture(dc, layout.Runway);
        DrawEdgeLights(dc, layout.Runway);
        DrawCenterline(dc, layout);
        DrawThreshold(dc, layout.Runway, data.RunwayWidthM, left: true);
        if (!layout.RunwayContinues)
            DrawThreshold(dc, layout.Runway, data.RunwayWidthM, left: false);
        DrawAimingAndTouchdownZoneMarkings(dc, data, layout);
        DrawRunwayDesignations(dc, data, layout);
        if (layout.RunwayContinues)
            DrawContinuationBreak(dc, layout.Runway);
        DrawDistanceScale(dc, layout);
        DrawTarget(dc, data, layout);
        DrawTouchdown(dc, data, layout);

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawText(dc, "APPROACH  ->", 18, 17, 10, MutedBrush, dpi);
        var visibleFeet = layout.VisibleDistanceM * FeetPerMeter;
        var viewportLabel = CreateText(
            $"LANDING ZONE VIEW  0-{visibleFeet:0} FT" +
            (layout.RunwayContinues ? "  /  RUNWAY CONTINUES" : "  /  FULL RUNWAY"),
            9,
            MutedBrush,
            dpi,
            FontWeights.Bold);
        dc.DrawText(viewportLabel, new Point(
            Math.Max(18, ActualWidth - viewportLabel.Width - 18),
            17));
        DrawText(dc, "CALIBRATED 500 FT SCALE  /  RUNWAY WIDTH EXPANDED", 18, ActualHeight - 20, 9, MutedBrush, dpi);
    }

    public static RunwayPlotLayout CalculateLayout(
        LandingVisualizationData data,
        double width,
        double height)
    {
        var left = Math.Max(48, width * .065);
        var right = Math.Max(48, width * .055);
        var runwayWidth = Math.Max(1, width - left - right);
        var runwayHeight = Math.Clamp(height * .36, 80, 122);
        var top = Math.Max(68, (height - runwayHeight) * .46);
        var runway = new Rect(left, top, runwayWidth, runwayHeight);

        var idealM = data.IdealTouchdownFarDistanceFromThresholdM is { } far
                     && double.IsFinite(far)
            ? Math.Max(0, far)
            : double.IsFinite(data.IdealTouchdownDistanceFromThresholdM)
                ? Math.Max(0, data.IdealTouchdownDistanceFromThresholdM)
                : 0;
        var requestedVisibleM = Math.Max(
            data.RunwayLengthM * RunwayDisplayFraction,
            Math.Max(
                MinimumLandingZoneDisplayFeet / FeetPerMeter,
                idealM + TargetTrailingContextFeet / FeetPerMeter));
        var visibleDistanceM = Math.Min(data.RunwayLengthM, requestedVisibleM);
        var runwayContinues = data.RunwayLengthM > visibleDistanceM + .01;
        var alongRatio = visibleDistanceM > 0
            ? data.TouchdownDistanceFromThresholdM / visibleDistanceM
            : 0;
        var rawX = runway.Left + alongRatio * runway.Width;
        var minX = Math.Max(12, runway.Left - Math.Min(35, runway.Width * .06));
        var maxX = Math.Min(width - 12, runway.Right + Math.Min(35, runway.Width * .06));
        var rawY = runway.Top + runway.Height / 2
                   + data.TouchdownLateralOffsetM / data.RunwayWidthM * runway.Height;
        var minY = Math.Max(18, runway.Top - runway.Height * .42);
        var maxY = Math.Min(height - 34, runway.Bottom + runway.Height * .42);

        return new RunwayPlotLayout(
            runway,
            new Point(Math.Clamp(rawX, minX, maxX), Math.Clamp(rawY, minY, maxY)),
            visibleDistanceM,
            runwayContinues,
            rawX < minX,
            rawX > maxX,
            rawY < minY || rawY > maxY);
    }

    public static AimingMarkerPlotLayout CalculateApproachAimingMarkerLayout(
        LandingVisualizationData data,
        RunwayPlotLayout layout)
    {
        var lengthM = IsPositiveFinite(data.AimingMarkerNominalLengthM)
            ? data.AimingMarkerNominalLengthM!.Value
            : 45.0;
        var startM = IsNonNegativeFinite(data.AimingMarkerStartDistanceFromThresholdM)
            ? data.AimingMarkerStartDistanceFromThresholdM!.Value
            : 305.0 - lengthM / 2.0;
        var x = XForDistance(startM, layout.VisibleDistanceM, layout.Runway);
        var width = Math.Max(5, lengthM / layout.VisibleDistanceM * layout.Runway.Width);
        return new AimingMarkerPlotLayout(startM, lengthM, x, width);
    }

    public static IdealTouchdownBandLayout CalculateIdealTouchdownBandLayout(
        LandingVisualizationData data,
        RunwayPlotLayout layout)
    {
        if (data.IdealTouchdownNearDistanceFromThresholdM is { } near
            && data.IdealTouchdownFarDistanceFromThresholdM is { } far
            && double.IsFinite(near)
            && double.IsFinite(far)
            && near >= 0
            && far > near)
        {
            return new IdealTouchdownBandLayout(
                near,
                far,
                XForDistance(near, layout.VisibleDistanceM, layout.Runway),
                XForDistance(far, layout.VisibleDistanceM, layout.Runway),
                true);
        }

        var legacy = double.IsFinite(data.IdealTouchdownDistanceFromThresholdM)
            ? Math.Max(0, data.IdealTouchdownDistanceFromThresholdM)
            : 0;
        var x = XForDistance(legacy, layout.VisibleDistanceM, layout.Runway);
        return new IdealTouchdownBandLayout(legacy, legacy, x, x, false);
    }

    private static void DrawGroundTexture(DrawingContext dc, Rect bounds)
    {
        for (var x = -bounds.Height; x < bounds.Width; x += 34)
            dc.DrawLine(FaintPen, new Point(x, bounds.Bottom), new Point(x + bounds.Height, bounds.Top));
    }

    private static void DrawRunwayTexture(DrawingContext dc, Rect runway)
    {
        var seamPen = FrozenPen(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF), 1);
        for (var x = runway.Left + 48; x < runway.Right; x += 64)
            dc.DrawLine(seamPen, new Point(x, runway.Top + 2), new Point(x, runway.Bottom - 2));
        dc.DrawLine(FaintPen,
            new Point(runway.Left, runway.Top + runway.Height * .27),
            new Point(runway.Right, runway.Top + runway.Height * .27));
        dc.DrawLine(FaintPen,
            new Point(runway.Left, runway.Bottom - runway.Height * .27),
            new Point(runway.Right, runway.Bottom - runway.Height * .27));
    }

    private static void DrawEdgeLights(DrawingContext dc, Rect runway)
    {
        var count = Math.Max(10, (int)(runway.Width / 28));
        for (var index = 0; index <= count; index++)
        {
            var x = runway.Left + index * runway.Width / count;
            dc.DrawEllipse(WhiteBrush, null, new Point(x, runway.Top - 2.5), 1.6, 1.6);
            dc.DrawEllipse(WhiteBrush, null, new Point(x, runway.Bottom + 2.5), 1.6, 1.6);
        }
    }

    private static void DrawCenterline(DrawingContext dc, RunwayPlotLayout layout)
    {
        const double stripeM = 30;
        const double gapM = 20;
        var runway = layout.Runway;
        var y = runway.Top + runway.Height / 2;
        for (var distance = 170.0; distance < layout.VisibleDistanceM - 20; distance += stripeM + gapM)
        {
            var x = XForDistance(distance, layout.VisibleDistanceM, runway);
            var length = Math.Max(2, stripeM / layout.VisibleDistanceM * runway.Width);
            dc.DrawRectangle(WhiteBrush, null, new Rect(x, y - 1.4, Math.Min(length, runway.Right - x), 2.8));
        }
    }

    private static void DrawThreshold(DrawingContext dc, Rect runway, double runwayWidthM, bool left)
    {
        var stripeCount = runwayWidthM switch
        {
            < 21 => 4,
            < 27 => 6,
            < 38 => 8,
            < 53 => 12,
            _ => 16
        };
        var x = left ? runway.Left + 8 : runway.Right - 14;
        var usable = runway.Height * .8;
        var top = runway.Top + runway.Height * .1;
        var gap = 2.1;
        var stripeHeight = Math.Max(1.5, (usable - gap * (stripeCount - 1)) / stripeCount);
        for (var index = 0; index < stripeCount; index++)
            dc.DrawRectangle(WhiteBrush, null,
                new Rect(x, top + index * (stripeHeight + gap), 6, stripeHeight));
    }

    private static void DrawAimingAndTouchdownZoneMarkings(
        DrawingContext dc,
        LandingVisualizationData data,
        RunwayPlotLayout layout)
    {
        // Older saved landings did not retain aiming-block geometry. Preserve their prior
        // appearance (45 m blocks centered at 305 m) while Free Mode uses its runway estimate.
        var marker = CalculateApproachAimingMarkerLayout(data, layout);
        DrawAimingBlocks(dc, layout, marker.StartDistanceM, marker.LengthM);

        var groups = new[] { 3, 3, 2, 2, 1, 1 };
        for (var index = 0; index < groups.Length; index++)
        {
            var distance = 150.0 * (index + 1);
            if (distance >= Math.Min(layout.VisibleDistanceM - 40, data.RunwayLengthM / 2 - 80)) break;
            DrawTouchdownZoneGroup(dc, layout, distance, groups[index]);
        }

        if (!layout.RunwayContinues)
        {
            DrawAimingBlocks(
                dc,
                layout,
                data.RunwayLengthM - marker.StartDistanceM - marker.LengthM,
                marker.LengthM);
            for (var index = 0; index < groups.Length; index++)
            {
                var distance = 150.0 * (index + 1);
                if (distance >= data.RunwayLengthM / 2 - 80) break;
                DrawTouchdownZoneGroup(dc, layout, data.RunwayLengthM - distance, groups[index]);
            }
        }
    }

    private static void DrawAimingBlocks(
        DrawingContext dc,
        RunwayPlotLayout layout,
        double startDistanceM,
        double lengthM)
    {
        var runway = layout.Runway;
        if (!double.IsFinite(startDistanceM) || !double.IsFinite(lengthM) || lengthM <= 0 ||
            startDistanceM + lengthM <= 0 || startDistanceM >= layout.VisibleDistanceM)
            return;

        var visibleStartM = Math.Max(0, startDistanceM);
        var visibleEndM = Math.Min(layout.VisibleDistanceM, startDistanceM + lengthM);
        var x = XForDistance(visibleStartM, layout.VisibleDistanceM, runway);
        var blockWidth = Math.Max(5, (visibleEndM - visibleStartM) / layout.VisibleDistanceM * runway.Width);
        var blockHeight = Math.Max(4, runway.Height * .12);
        var inset = runway.Height * .18;
        dc.DrawRectangle(WhiteBrush, null,
            new Rect(x, runway.Top + inset, Math.Min(blockWidth, runway.Right - x), blockHeight));
        dc.DrawRectangle(WhiteBrush, null,
            new Rect(x, runway.Bottom - inset - blockHeight, Math.Min(blockWidth, runway.Right - x), blockHeight));
    }

    private static bool IsPositiveFinite(double? value) =>
        value is > 0 && double.IsFinite(value.Value);

    private static bool IsNonNegativeFinite(double? value) =>
        value is >= 0 && double.IsFinite(value.Value);

    private static void DrawTouchdownZoneGroup(
        DrawingContext dc,
        RunwayPlotLayout layout,
        double distanceM,
        int bars)
    {
        var runway = layout.Runway;
        var x = XForDistance(distanceM, layout.VisibleDistanceM, runway);
        var barWidth = Math.Max(2.5, 22 / layout.VisibleDistanceM * runway.Width);
        var barHeight = Math.Max(2.2, runway.Height * .035);
        for (var index = 0; index < bars; index++)
        {
            var yOffset = runway.Height * (.18 + index * .065);
            dc.DrawRectangle(WhiteBrush, null,
                new Rect(x - barWidth / 2, runway.Top + yOffset, barWidth, barHeight));
            dc.DrawRectangle(WhiteBrush, null,
                new Rect(x - barWidth / 2, runway.Bottom - yOffset - barHeight, barWidth, barHeight));
        }
    }

    private static void DrawRunwayDesignations(
        DrawingContext dc,
        LandingVisualizationData data,
        RunwayPlotLayout layout)
    {
        var runway = layout.Runway;
        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        var leftText = CreateText(data.RunwayId.ToUpperInvariant(), Math.Clamp(runway.Height * .18, 13, 22), WhiteBrush, dpi, FontWeights.Bold);
        var leftX = XForDistance(Math.Min(105, layout.VisibleDistanceM * .12), layout.VisibleDistanceM, runway);
        dc.DrawText(leftText, new Point(leftX - leftText.Width / 2, runway.Top + (runway.Height - leftText.Height) / 2));

        if (layout.RunwayContinues)
            return;

        var reciprocal = ReciprocalRunwayId(data.RunwayId, data.RunwayHeadingTrueDeg);
        var rightText = CreateText(reciprocal, Math.Clamp(runway.Height * .18, 13, 22), WhiteBrush, dpi, FontWeights.Bold);
        var rightX = XForDistance(Math.Max(0, data.RunwayLengthM - Math.Min(105, data.RunwayLengthM * .12)), layout.VisibleDistanceM, runway);
        dc.PushTransform(new RotateTransform(180, rightX, runway.Top + runway.Height / 2));
        dc.DrawText(rightText, new Point(rightX - rightText.Width / 2, runway.Top + (runway.Height - rightText.Height) / 2));
        dc.Pop();
    }

    private static void DrawContinuationBreak(DrawingContext dc, Rect runway)
    {
        var mask = FrozenBrush(Color.FromArgb(0xE8, 0x21, 0x28, 0x31));
        var slashPen = FrozenPen(Color.FromArgb(0xD8, 0xF2, 0xF4, 0xED), 1.4);
        dc.DrawRectangle(mask, null, new Rect(runway.Right - 19, runway.Top + 1, 18, runway.Height - 2));
        for (var y = runway.Top + 13; y < runway.Bottom + 12; y += 18)
            dc.DrawLine(slashPen, new Point(runway.Right - 18, y + 8), new Point(runway.Right - 2, y - 8));
    }

    private static void DrawDistanceScale(DrawingContext dc, RunwayPlotLayout layout)
    {
        var runway = layout.Runway;
        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        var visibleFeet = layout.VisibleDistanceM * FeetPerMeter;
        const double minorStepFeet = 250;

        for (var feet = 0.0; feet <= visibleFeet + .1; feet += minorStepFeet)
        {
            var isMajor = Math.Abs(feet % 500) < .1;
            var x = XForDistance(feet / FeetPerMeter, layout.VisibleDistanceM, runway);
            dc.DrawLine(
                isMajor ? EdgePen : FaintPen,
                new Point(x, runway.Bottom + 6),
                new Point(x, runway.Bottom + (isMajor ? 13 : 10)));

            if (!isMajor || feet < 1 || feet > visibleFeet - 100)
                continue;

            var label = CreateText($"{feet:0}", 8, MutedBrush, dpi, FontWeights.Bold);
            dc.DrawText(label, new Point(x - label.Width / 2, runway.Bottom + 14));
        }

        var threshold = CreateText("THR  0 FT", 8, MutedBrush, dpi, FontWeights.Bold);
        dc.DrawText(threshold, new Point(runway.Left - threshold.Width / 2, runway.Bottom + 14));
    }

    private static void DrawTarget(
        DrawingContext dc,
        LandingVisualizationData data,
        RunwayPlotLayout layout)
    {
        var band = CalculateIdealTouchdownBandLayout(data, layout);
        if (band.HasBand)
        {
            dc.DrawRectangle(
                IdealBandBrush,
                null,
                new Rect(
                    band.NearX,
                    layout.Runway.Top,
                    Math.Max(2, band.FarX - band.NearX),
                    layout.Runway.Height));
            dc.DrawLine(TargetPen, new Point(band.NearX, layout.Runway.Top - 7), new Point(band.NearX, layout.Runway.Bottom + 7));
            dc.DrawLine(TargetPen, new Point(band.FarX, layout.Runway.Top - 7), new Point(band.FarX, layout.Runway.Bottom + 7));
        }
        else
        {
            dc.DrawLine(TargetPen, new Point(band.NearX, layout.Runway.Top - 7), new Point(band.NearX, layout.Runway.Bottom + 7));
        }
        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        var label = CreateText(
            band.HasBand
                ? $"IDEAL  {band.NearDistanceM * FeetPerMeter:0}-{band.FarDistanceM * FeetPerMeter:0} FT"
                : $"IDEAL  {band.NearDistanceM * FeetPerMeter:0} FT",
            9,
            GreenBrush,
            dpi,
            FontWeights.Bold);
        dc.DrawText(label, new Point(
            Math.Clamp((band.NearX + band.FarX) / 2 - label.Width / 2, 8, layout.Runway.Right - label.Width),
            layout.Runway.Bottom + 31));
    }

    private static void DrawTouchdown(
        DrawingContext dc,
        LandingVisualizationData data,
        RunwayPlotLayout layout)
    {
        var marker = layout.Touchdown;
        var centerY = layout.Runway.Top + layout.Runway.Height / 2;
        dc.DrawLine(CenterGuidePen, marker, new Point(marker.X, centerY));

        var band = CalculateIdealTouchdownBandLayout(data, layout);
        var targetDistanceM = data.TouchdownDistanceFromThresholdM < band.NearDistanceM
            ? band.NearDistanceM
            : data.TouchdownDistanceFromThresholdM > band.FarDistanceM
                ? band.FarDistanceM
                : data.TouchdownDistanceFromThresholdM;
        var targetX = XForDistance(targetDistanceM, layout.VisibleDistanceM, layout.Runway);
        var measureY = layout.Runway.Top - 14;
        dc.DrawLine(CenterGuidePen, new Point(targetX, measureY), new Point(marker.X, measureY));
        dc.DrawLine(CenterGuidePen, new Point(targetX, measureY - 4), new Point(targetX, measureY + 4));
        dc.DrawLine(CenterGuidePen, new Point(marker.X, measureY - 4), new Point(marker.X, measureY + 4));

        dc.DrawEllipse(FrozenBrush(Color.FromArgb(0x35, 0x2D, 0xE2, 0xE6)), null, marker, 24, 24);
        dc.DrawEllipse(FrozenBrush(Color.FromArgb(0x60, 0x2D, 0xE2, 0xE6)), null, marker, 17, 17);
        dc.PushTransform(new TranslateTransform(marker.X, marker.Y));
        dc.PushTransform(new RotateTransform(data.TouchdownHeadingErrorDeg));
        dc.DrawGeometry(FrozenBrush(Color.FromArgb(0x60, 0x00, 0x00, 0x00)), null, AircraftGeometry);
        dc.PushTransform(new TranslateTransform(0, -1.5));
        dc.DrawGeometry(WhiteBrush, FrozenPen(Color.FromRgb(0x2D, 0xE2, 0xE6), 1.4), AircraftGeometry);
        dc.Pop();
        dc.Pop();
        dc.Pop();

        if (layout.ClippedBefore || layout.ClippedAfter || layout.ClippedLateral)
            DrawOverflowIndicator(dc, layout, data);
        DrawTouchdownCallout(dc, layout, data);
    }

    private static void DrawOverflowIndicator(
        DrawingContext dc,
        RunwayPlotLayout layout,
        LandingVisualizationData data)
    {
        var marker = layout.Touchdown;
        var direction = layout.ClippedBefore ? -1 : layout.ClippedAfter ? 1 : 0;
        if (direction != 0)
        {
            var triangle = new StreamGeometry();
            using (var context = triangle.Open())
            {
                context.BeginFigure(new Point(marker.X + direction * 17, marker.Y), true, true);
                context.LineTo(new Point(marker.X + direction * 7, marker.Y - 6), true, false);
                context.LineTo(new Point(marker.X + direction * 7, marker.Y + 6), true, false);
            }
            triangle.Freeze();
            dc.DrawGeometry(AmberBrush, null, triangle);
        }
        if (layout.ClippedLateral)
            dc.DrawEllipse(AmberBrush, null, marker, 3, 3);
    }

    private static void DrawTouchdownCallout(
        DrawingContext dc,
        RunwayPlotLayout layout,
        LandingVisualizationData data)
    {
        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        var side = Math.Abs(data.TouchdownLateralOffsetM) < .05
            ? "CENTERLINE"
            : $"{Math.Abs(data.TouchdownLateralOffsetM):0.0} M {(data.TouchdownLateralOffsetM < 0 ? "L" : "R")}";
        var label = CreateText(
            $"TD  {data.TouchdownDistanceFromThresholdM * FeetPerMeter:0} FT  ·  {side}",
            10,
            WhiteBrush,
            dpi,
            FontWeights.Bold);
        var x = Math.Clamp(layout.Touchdown.X - label.Width / 2 - 8, 8, layout.Runway.Right - label.Width - 8);
        var y = Math.Max(35, layout.Runway.Top - 48);
        var box = new Rect(x, y, label.Width + 16, label.Height + 8);
        dc.DrawRoundedRectangle(
            FrozenBrush(Color.FromArgb(0xE8, 0x0B, 0x10, 0x20)),
            FrozenPen(Color.FromArgb(0xB0, 0x2D, 0xE2, 0xE6), 1),
            box,
            8,
            8);
        dc.DrawText(label, new Point(box.X + 8, box.Y + 4));
    }

    private static string ReciprocalRunwayId(string runwayId, double heading)
    {
        var match = Regex.Match(runwayId.Trim(), @"^(\d{1,2})([LRC]?)$", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
        {
            var reciprocal = ((number + 17) % 36) + 1;
            var suffix = match.Groups[2].Value.ToUpperInvariant() switch
            {
                "L" => "R",
                "R" => "L",
                "C" => "C",
                _ => ""
            };
            return reciprocal.ToString("00", CultureInfo.InvariantCulture) + suffix;
        }

        var fallback = (int)Math.Round(((heading + 180) % 360) / 10.0, MidpointRounding.AwayFromZero);
        if (fallback == 0) fallback = 36;
        return fallback.ToString("00", CultureInfo.InvariantCulture);
    }

    private static double XForDistance(double distanceM, double runwayLengthM, Rect runway) =>
        runway.Left + Math.Clamp(distanceM / Math.Max(1, runwayLengthM), 0, 1) * runway.Width;

    private static void DrawText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        double size,
        Brush brush,
        double dpi) => dc.DrawText(CreateText(text, size, brush, dpi), new Point(x, y));

    private static FormattedText CreateText(
        string text,
        double size,
        Brush brush,
        double dpi,
        FontWeight? weight = null) => new(
        text,
        CultureInfo.InvariantCulture,
        WpfFlowDirection.LeftToRight,
        new Typeface(new MediaFontFamily("Segoe UI Variable, Segoe UI"),
            FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
        size,
        brush,
        dpi);

    private static StreamGeometry CreateAircraftGeometry()
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new Point(21, 0), true, true);
        context.BezierTo(new Point(15, -3), new Point(9, -3), new Point(4, -3), true, false);
        context.LineTo(new Point(-1, -18), true, false);
        context.LineTo(new Point(-5, -18), true, false);
        context.LineTo(new Point(-4, -3), true, false);
        context.LineTo(new Point(-14, -2), true, false);
        context.LineTo(new Point(-18, -8), true, false);
        context.LineTo(new Point(-21, -8), true, false);
        context.LineTo(new Point(-19, 0), true, false);
        context.LineTo(new Point(-21, 8), true, false);
        context.LineTo(new Point(-18, 8), true, false);
        context.LineTo(new Point(-14, 2), true, false);
        context.LineTo(new Point(-4, 3), true, false);
        context.LineTo(new Point(-5, 18), true, false);
        context.LineTo(new Point(-1, 18), true, false);
        context.LineTo(new Point(4, 3), true, false);
        context.BezierTo(new Point(9, 3), new Point(15, 3), new Point(21, 0), true, false);
        geometry.Freeze();
        return geometry;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Brush Gradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, 90);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness);
        pen.Freeze();
        return pen;
    }

    private static Pen DashedPen(Color color, double thickness, double dash, double gap)
    {
        var pen = new Pen(FrozenBrush(color), thickness)
        {
            DashStyle = new DashStyle(new[] { dash, gap }, 0)
        };
        pen.Freeze();
        return pen;
    }
}

public readonly record struct RunwayPlotLayout(
    Rect Runway,
    Point Touchdown,
    double VisibleDistanceM,
    bool RunwayContinues,
    bool ClippedBefore,
    bool ClippedAfter,
    bool ClippedLateral);

public readonly record struct AimingMarkerPlotLayout(
    double StartDistanceM,
    double LengthM,
    double StartX,
    double Width);

public readonly record struct IdealTouchdownBandLayout(
    double NearDistanceM,
    double FarDistanceM,
    double NearX,
    double FarX,
    bool HasBand);
