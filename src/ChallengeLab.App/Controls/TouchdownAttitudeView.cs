using System.Globalization;
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

/// <summary>Cinematic touchdown attitude vignette driven by the captured bank and pitch.</summary>
public sealed class TouchdownAttitudeView : FrameworkElement
{
    private static readonly Brush SkyBrush = Gradient(
        Color.FromRgb(0x18, 0x4C, 0x70), Color.FromRgb(0x0D, 0x25, 0x3B));
    private static readonly Brush GroundBrush = Gradient(
        Color.FromRgb(0x30, 0x35, 0x2C), Color.FromRgb(0x0F, 0x18, 0x18));
    private static readonly Brush PanelBrush = FrozenBrush(Color.FromRgb(0x09, 0x10, 0x1D));
    private static readonly Brush RunwayBrush = FrozenBrush(Color.FromRgb(0x31, 0x39, 0x43));
    private static readonly Brush WhiteBrush = FrozenBrush(Color.FromRgb(0xF4, 0xF7, 0xFF));
    private static readonly Brush AmberBrush = FrozenBrush(Color.FromRgb(0xFF, 0xB0, 0x20));
    private static readonly Brush MutedBrush = FrozenBrush(Color.FromRgb(0x9A, 0xAA, 0xC4));
    private static readonly Pen BorderPen = FrozenPen(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen AircraftPen = FrozenPen(Color.FromRgb(0xFF, 0xB0, 0x20), 3);

    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data),
        typeof(LandingVisualizationData),
        typeof(TouchdownAttitudeView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public LandingVisualizationData? Data
    {
        get => (LandingVisualizationData?)GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 180 || ActualHeight < 150) return;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(PanelBrush, BorderPen, bounds, 12, 12);
        if (Data is not { } data) return;

        var viewport = new Rect(1, 1, ActualWidth - 2, ActualHeight - 40);
        dc.PushClip(new RectangleGeometry(viewport, 11, 11));
        var pitch = data.Version >= 4 ? data.TouchdownPitchDeg : -data.TouchdownPitchDeg;
        var horizonY = viewport.Top + viewport.Height * .49 + Math.Clamp(pitch, -15, 15) * 1.5;
        var center = new Point(viewport.Left + viewport.Width / 2, horizonY);
        dc.PushTransform(new RotateTransform(-data.TouchdownBankDeg, center.X, center.Y));
        dc.DrawRectangle(SkyBrush, null,
            new Rect(-ActualWidth, -ActualHeight, ActualWidth * 3, horizonY + ActualHeight));
        dc.DrawRectangle(GroundBrush, null,
            new Rect(-ActualWidth, horizonY, ActualWidth * 3, ActualHeight * 2));
        dc.DrawLine(FrozenPen(Color.FromArgb(0xCC, 0xEE, 0xF4, 0xFF), 1.4),
            new Point(-ActualWidth, horizonY), new Point(ActualWidth * 2, horizonY));
        DrawPerspectiveRunway(dc, viewport, horizonY);
        dc.Pop();

        DrawFixedAircraft(dc, viewport);
        DrawBankScale(dc, viewport, data.TouchdownBankDeg);
        dc.Pop();

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        DrawText(dc, "TOUCHDOWN ATTITUDE", 12, ActualHeight - 32, 9, MutedBrush, dpi, FontWeights.Bold);
        var values = $"BANK {Signed(data.TouchdownBankDeg)}°   ·   PITCH {Signed(pitch)}°";
        var text = CreateText(values, 11, WhiteBrush, dpi, FontWeights.SemiBold);
        dc.DrawText(text, new Point(ActualWidth - text.Width - 12, ActualHeight - 34));
    }

    private static void DrawPerspectiveRunway(DrawingContext dc, Rect viewport, double horizonY)
    {
        var centerX = viewport.Left + viewport.Width / 2;
        var bottom = viewport.Bottom + 10;
        var runway = new StreamGeometry();
        using (var context = runway.Open())
        {
            context.BeginFigure(new Point(centerX - 8, horizonY + 2), true, true);
            context.LineTo(new Point(centerX + 8, horizonY + 2), true, false);
            context.LineTo(new Point(centerX + viewport.Width * .32, bottom), true, false);
            context.LineTo(new Point(centerX - viewport.Width * .32, bottom), true, false);
        }
        runway.Freeze();
        dc.DrawGeometry(RunwayBrush, FrozenPen(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF), 1), runway);

        for (var index = 1; index <= 5; index++)
        {
            var t = index / 6.0;
            var perspective = t * t;
            var y = horizonY + 5 + perspective * (bottom - horizonY - 8);
            var width = 2 + perspective * 9;
            dc.DrawRectangle(WhiteBrush, null, new Rect(centerX - width / 2, y, width, 3 + perspective * 5));
        }
    }

    private static void DrawFixedAircraft(DrawingContext dc, Rect viewport)
    {
        var cx = viewport.Left + viewport.Width / 2;
        var cy = viewport.Top + viewport.Height * .55;
        dc.DrawLine(AircraftPen, new Point(cx - 42, cy), new Point(cx - 10, cy));
        dc.DrawLine(AircraftPen, new Point(cx + 10, cy), new Point(cx + 42, cy));
        dc.DrawLine(AircraftPen, new Point(cx - 10, cy), new Point(cx, cy + 8));
        dc.DrawLine(AircraftPen, new Point(cx, cy + 8), new Point(cx + 10, cy));
        dc.DrawEllipse(AmberBrush, null, new Point(cx, cy + 8), 2.5, 2.5);
    }

    private static void DrawBankScale(DrawingContext dc, Rect viewport, double bank)
    {
        var cx = viewport.Left + viewport.Width / 2;
        var cy = viewport.Top + 20;
        var radius = Math.Min(58, viewport.Width * .22);
        foreach (var angle in new[] { -30, -20, -10, 0, 10, 20, 30 })
        {
            var radians = (angle - 90) * Math.PI / 180.0;
            var inner = new Point(cx + Math.Cos(radians) * (radius - 6), cy + radius + Math.Sin(radians) * (radius - 6));
            var outer = new Point(cx + Math.Cos(radians) * radius, cy + radius + Math.Sin(radians) * radius);
            dc.DrawLine(BorderPen, inner, outer);
        }
        var markerAngle = (Math.Clamp(bank, -45, 45) - 90) * Math.PI / 180.0;
        dc.DrawEllipse(AmberBrush, null,
            new Point(cx + Math.Cos(markerAngle) * radius, cy + radius + Math.Sin(markerAngle) * radius), 3, 3);
    }

    private static string Signed(double value) => value >= 0 ? $"+{value:0.0}" : $"{value:0.0}";

    private static void DrawText(
        DrawingContext dc,
        string text,
        double x,
        double y,
        double size,
        Brush brush,
        double dpi,
        FontWeight weight) => dc.DrawText(CreateText(text, size, brush, dpi, weight), new Point(x, y));

    private static FormattedText CreateText(
        string text,
        double size,
        Brush brush,
        double dpi,
        FontWeight weight) => new(
        text,
        CultureInfo.InvariantCulture,
        WpfFlowDirection.LeftToRight,
        new Typeface(new MediaFontFamily("Segoe UI Variable, Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal),
        size,
        brush,
        dpi);

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
}
