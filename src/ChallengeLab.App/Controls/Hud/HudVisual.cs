using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ChallengeLab.App.Controls;
using ChallengeLab.Core.Scoring;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using WpfFlowDirection = System.Windows.FlowDirection;

namespace ChallengeLab.App.Controls.Hud;

/// <summary>Minimal approach-guidance overlay with an intentionally empty center view.</summary>
public sealed class HudVisual : FrameworkElement
{
    internal const double DesignWidth = 1600;
    internal const double DesignHeight = 900;

    private static readonly Typeface UiTypeface = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Normal,
        FontStretches.Normal);
    private static readonly Typeface SemiboldTypeface = new(
        new FontFamily("Segoe UI Semibold"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);
    private static readonly Typeface MonoTypeface = new(
        new FontFamily("Cascadia Mono, Consolas"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    private static readonly Brush GreenBrush = FrozenBrush("#42FF9B");
    private static readonly Brush OrangeBrush = FrozenBrush("#FFC24A");
    private static readonly Brush RedBrush = FrozenBrush("#FF6685");
    private static readonly Brush NeutralBrush = FrozenBrush("#E8F7FF");
    private static readonly Brush LabelBrush = FrozenBrush("#74CFF2");
    private static readonly Brush WindBrush = FrozenBrush("#42E3FF");
    private static readonly Brush ShadowBrush = FrozenBrush("#C0000000");

    private HudPresentationFrame? _frame;
    private double _hudScale = 0.78;
    private double _hudOpacity = 0.95;
    private double _fontScale = 1.1;

    public HudVisual()
    {
        SnapsToDevicePixels = false;
        IsHitTestVisible = false;
    }

    internal void UpdatePresentation(HudPresentationFrame? frame)
    {
        _frame = frame;
        InvalidateVisual();
    }

    internal double HudScale => _hudScale;

    internal void UpdateScale(double scale)
    {
        _hudScale = Math.Clamp(scale, 0.55, 1.25);
        InvalidateVisual();
    }

    internal double HudOpacity => _hudOpacity;

    internal void UpdateOpacity(double opacity)
    {
        _hudOpacity = Math.Clamp(opacity, 0.2, 1.0);
        InvalidateVisual();
    }

    internal double FontScale => _fontScale;

    internal void UpdateFontScale(double scale)
    {
        _fontScale = Math.Clamp(scale, 0.75, 1.35);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_frame is null || ActualWidth < 1 || ActualHeight < 1)
            return;

        var fit = Math.Min(ActualWidth / DesignWidth, ActualHeight / DesignHeight);
        var scale = fit * _hudScale;
        var offsetX = (ActualWidth - DesignWidth * scale) / 2.0;
        var offsetY = (ActualHeight - DesignHeight * scale) / 2.0;
        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(scale, scale));
        drawingContext.PushOpacity(_hudOpacity);

        DrawWind(drawingContext, _frame.Wind, _frame.CrabAngleDeg);
        if (_frame.View.HasRunwayTarget && _frame.TargetGlideslopeDeg is not null)
        {
            DrawPathPosition(drawingContext, _frame);
            DrawDescentAngle(drawingContext, _frame);
        }
        DrawVerticalSpeed(drawingContext, _frame.Guidance);
        DrawAirspeed(drawingContext, _frame.Guidance);

        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void DrawWind(DrawingContext dc, RelativeWindReading wind, double? crabAngleDeg)
    {
        var center = new Point(706, 120);

        if (crabAngleDeg is { } crab)
        {
            DrawText(dc, CrabAnglePresentation.Format(crab), 14, LabelBrush,
                760, 68, TextAlignment.Left, semibold: true);
        }

        if (wind.IsAvailable)
            DrawWindArrow(dc, center, wind.RelativeFromAngleDeg);
        else
            DrawText(dc, "—", 22, NeutralBrush, center.X, center.Y - 14, TextAlignment.Center, mono: true);

        var crosswind = !wind.IsAvailable
            ? "XWIND —"
            : Math.Abs(wind.CrosswindKts) < 0.05
                ? "XWIND 0.0 KT"
                : $"XWIND {(wind.CrosswindKts > 0 ? "R" : "L")} {Math.Abs(wind.CrosswindKts):0.0} KT";
        var speed = FormatWindSpeed(wind);
        DrawText(dc, speed, 22, wind.IsAvailable ? WindBrush : NeutralBrush,
            760, 94, TextAlignment.Left, mono: true);
        DrawText(dc, crosswind, 18, wind.IsAvailable ? WindBrush : NeutralBrush,
            760, 128, TextAlignment.Left, mono: true);
    }

    internal static string FormatWindSpeed(RelativeWindReading wind) =>
        wind.IsAvailable ? $"{wind.WindSpeedKts:0.0} KT" : "— KT";

    private void DrawPathPosition(DrawingContext dc, HudPresentationFrame frame)
    {
        var guidance = frame.Guidance;
        var value = guidance.GlideslopeDeg is { } angle ? $"{angle:0.0}°" : "—";
        var status = PathPositionLabel(guidance.GlideslopeDeg, frame.TargetGlideslopeDeg);
        var color = StatusBrush(guidance.GlideslopeStatus);
        DrawText(dc, "PATH", 10, LabelBrush, 458, 310, TextAlignment.Left, semibold: true);
        DrawText(dc, value, 34, color, 458, 330, TextAlignment.Left, mono: true);
        DrawText(dc, status, 11, color, 650, 347, TextAlignment.Right, semibold: true);
        DrawText(dc, TargetText(frame.TargetGlideslopeDeg), 10, LabelBrush,
            458, 378, TextAlignment.Left, mono: true);
    }

    private void DrawDescentAngle(DrawingContext dc, HudPresentationFrame frame)
    {
        var guidance = frame.Guidance;
        var value = guidance.DescentAngleDeg is { } angle ? $"{angle:0.0}°" : "—";
        var status = DescentAngleLabel(guidance.DescentAngleDeg, frame.TargetGlideslopeDeg);
        var color = StatusBrush(guidance.DescentAngleStatus);
        DrawText(dc, "DESCENT", 10, LabelBrush, 458, 430, TextAlignment.Left, semibold: true);
        DrawText(dc, value, 34, color, 458, 450, TextAlignment.Left, mono: true);
        DrawText(dc, status, 11, color, 650, 467, TextAlignment.Right, semibold: true);
        DrawText(dc, TargetText(frame.TargetGlideslopeDeg), 10, LabelBrush,
            458, 498, TextAlignment.Left, mono: true);
    }

    private void DrawVerticalSpeed(DrawingContext dc, LandingMonitorReading guidance)
    {
        var verticalSpeed = guidance.VerticalSpeedFpm;
        var value = verticalSpeed is { } fpm
            ? $"{(Math.Abs(fpm) < 0.5 ? 0 : fpm):0}"
            : "—";
        var color = StatusBrush(guidance.DescentAngleStatus);
        DrawText(dc, value, 37, color, 1100, 393, TextAlignment.Right, mono: true);
        DrawText(dc, "VSpeed", 11, LabelBrush, 1118, 415, TextAlignment.Left, semibold: true);
    }

    private void DrawAirspeed(DrawingContext dc, LandingMonitorReading guidance)
    {
        var value = guidance.AirspeedKts is { } airspeed ? $"{airspeed:0}" : "—";
        var color = StatusBrush(guidance.AirspeedStatus);
        DrawText(dc, "IAS", 10, LabelBrush, 724, 650, TextAlignment.Left, semibold: true);
        DrawText(dc, value, 37, color, 800, 658, TextAlignment.Center, mono: true);
        DrawText(dc, "KT", 10, LabelBrush, 874, 677, TextAlignment.Right, semibold: true);
    }

    private static void DrawWindArrow(DrawingContext dc, Point center, double relativeFromDegrees)
    {
        dc.PushTransform(new RotateTransform(relativeFromDegrees, center.X, center.Y));

        var shadowPen = new Pen(ShadowBrush, 4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        var arrowPen = new Pen(WindBrush, 2.25)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        var tail = new Point(center.X, center.Y + 12);
        var shoulder = new Point(center.X, center.Y - 9);
        dc.DrawLine(shadowPen, tail, shoulder);
        dc.DrawLine(arrowPen, tail, shoulder);

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - 23), true, true);
            context.LineTo(new Point(center.X - 7, center.Y - 8), true, false);
            context.LineTo(new Point(center.X + 7, center.Y - 8), true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(ShadowBrush, new Pen(ShadowBrush, 2), geometry);
        dc.DrawGeometry(WindBrush, null, geometry);
        dc.Pop();
    }

    private static string PathPositionLabel(double? measuredDeg, double? targetDeg)
    {
        if (measuredDeg is null || targetDeg is null)
            return "—";
        var error = measuredDeg.Value - targetDeg.Value;
        if (error < -LandingMonitorCalculator.GlideslopeGreenHalfBandDeg)
            return "LOW";
        if (error > LandingMonitorCalculator.GlideslopeGreenHalfBandDeg)
            return "HIGH";
        return "ON PATH";
    }

    private static string DescentAngleLabel(double? measuredDeg, double? targetDeg)
    {
        if (measuredDeg is null || targetDeg is null)
            return "—";
        var error = measuredDeg.Value - targetDeg.Value;
        if (error < -LandingMonitorCalculator.DescentAngleGreenHalfBandDeg)
            return "SHALLOW";
        if (error > LandingMonitorCalculator.DescentAngleGreenHalfBandDeg)
            return "STEEP";
        return "ON ANGLE";
    }

    private static string TargetText(double? targetDeg) =>
        targetDeg is { } value ? $"TARGET {value:0.0}°" : "TARGET —";

    private static Brush StatusBrush(LandingMonitorStatus status) => status switch
    {
        LandingMonitorStatus.Green => GreenBrush,
        LandingMonitorStatus.Orange => OrangeBrush,
        LandingMonitorStatus.Red => RedBrush,
        _ => NeutralBrush,
    };

    private void DrawText(
        DrawingContext dc,
        string text,
        double fontSize,
        Brush brush,
        double x,
        double y,
        TextAlignment alignment,
        bool semibold = false,
        bool mono = false)
    {
        var typeface = mono ? MonoTypeface : semibold ? SemiboldTypeface : UiTypeface;
        var scaledFontSize = fontSize * _fontScale;
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            WpfFlowDirection.LeftToRight,
            typeface,
            scaledFontSize,
            brush,
            1.0)
        {
            TextAlignment = alignment,
        };
        var shadow = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            WpfFlowDirection.LeftToRight,
            typeface,
            scaledFontSize,
            ShadowBrush,
            1.0)
        {
            TextAlignment = alignment,
        };
        dc.DrawText(shadow, new Point(x + 1.4, y + 1.4));
        dc.DrawText(formatted, new Point(x, y));
    }

    private static Brush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
        brush.Freeze();
        return brush;
    }

}
