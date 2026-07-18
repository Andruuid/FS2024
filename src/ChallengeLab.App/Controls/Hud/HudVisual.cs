using System.Globalization;
using System.Windows;
using System.Windows.Media;
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

    private static readonly Brush GreenBrush = FrozenBrush("#3DDC97");
    private static readonly Brush OrangeBrush = FrozenBrush("#FFB020");
    private static readonly Brush RedBrush = FrozenBrush("#FF4D6A");
    private static readonly Brush NeutralBrush = FrozenBrush("#A8B4C7");
    private static readonly Brush LabelBrush = FrozenBrush("#B9C7D9");
    private static readonly Brush PanelBrush = FrozenBrush("#A6121A28");
    private static readonly Brush PanelBorderBrush = FrozenBrush("#7A61758A");
    private static readonly Brush WindBrush = FrozenBrush("#64E8FF");

    private HudPresentationFrame? _frame;

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

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (_frame is null || ActualWidth < 1 || ActualHeight < 1)
            return;

        var fit = Math.Min(ActualWidth / DesignWidth, ActualHeight / DesignHeight);
        var offsetX = (ActualWidth - DesignWidth * fit) / 2.0;
        var offsetY = (ActualHeight - DesignHeight * fit) / 2.0;
        drawingContext.PushTransform(new TranslateTransform(offsetX, offsetY));
        drawingContext.PushTransform(new ScaleTransform(fit, fit));
        drawingContext.PushOpacity(0.9);

        DrawWind(drawingContext, _frame.Wind);
        DrawPathPosition(drawingContext, _frame);
        DrawDescentAngle(drawingContext, _frame);
        DrawVerticalSpeed(drawingContext, _frame.Guidance);
        DrawAirspeed(drawingContext, _frame.Guidance);

        drawingContext.Pop();
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private static void DrawWind(DrawingContext dc, RelativeWindReading wind)
    {
        var panel = new Rect(550, 28, 500, 116);
        DrawPanel(dc, panel);
        DrawText(dc, "WIND FROM", 11, LabelBrush, 584, 47, TextAlignment.Left, semibold: true);

        var center = new Point(678, 94);
        dc.DrawEllipse(null, new Pen(WithOpacity(WindBrush, 0.45), 1.5), center, 35, 35);
        DrawAircraftReference(dc, center);

        if (wind.IsAvailable && wind.HasWind)
            DrawWindArrow(dc, center, wind.RelativeFromAngleDeg);
        else
            DrawText(dc, "—", 24, NeutralBrush, center.X, center.Y - 15, TextAlignment.Center, mono: true);

        var crosswind = !wind.IsAvailable
            ? "XWIND —"
            : Math.Abs(wind.CrosswindKts) < 0.05
                ? "XWIND 0.0 KT"
                : $"XWIND {(wind.CrosswindKts > 0 ? "R" : "L")} {Math.Abs(wind.CrosswindKts):0.0} KT";
        var total = !wind.IsAvailable
            ? "WIND —"
            : wind.HasWind
                ? $"WIND {wind.WindSpeedKts:0.0} KT"
                : "CALM";
        DrawText(dc, crosswind, 25, wind.IsAvailable ? WindBrush : NeutralBrush,
            758, 58, TextAlignment.Left, mono: true);
        DrawText(dc, total, 12, LabelBrush, 760, 103, TextAlignment.Left, semibold: true);
    }

    private static void DrawPathPosition(DrawingContext dc, HudPresentationFrame frame)
    {
        var panel = new Rect(76, 244, 330, 154);
        DrawPanel(dc, panel);
        var guidance = frame.Guidance;
        var value = guidance.GlideslopeDeg is { } angle ? $"{angle:0.0}°" : "—";
        var status = PathPositionLabel(guidance.GlideslopeDeg, frame.TargetGlideslopeDeg);
        var color = StatusBrush(guidance.GlideslopeStatus);
        DrawText(dc, "PATH POSITION", 12, LabelBrush, 104, 269, TextAlignment.Left, semibold: true);
        DrawText(dc, value, 43, color, 104, 298, TextAlignment.Left, mono: true);
        DrawText(dc, status, 13, color, 376, 315, TextAlignment.Right, semibold: true);
        DrawText(dc, TargetText(frame.TargetGlideslopeDeg), 11, LabelBrush,
            104, 367, TextAlignment.Left, mono: true);
    }

    private static void DrawDescentAngle(DrawingContext dc, HudPresentationFrame frame)
    {
        var panel = new Rect(76, 425, 330, 154);
        DrawPanel(dc, panel);
        var guidance = frame.Guidance;
        var value = guidance.DescentAngleDeg is { } angle ? $"{angle:0.0}°" : "—";
        var status = DescentAngleLabel(guidance.DescentAngleDeg, frame.TargetGlideslopeDeg);
        var color = StatusBrush(guidance.DescentAngleStatus);
        DrawText(dc, "DESCENT ANGLE", 12, LabelBrush, 104, 450, TextAlignment.Left, semibold: true);
        DrawText(dc, value, 43, color, 104, 479, TextAlignment.Left, mono: true);
        DrawText(dc, status, 13, color, 376, 496, TextAlignment.Right, semibold: true);
        DrawText(dc, TargetText(frame.TargetGlideslopeDeg), 11, LabelBrush,
            104, 548, TextAlignment.Left, mono: true);
    }

    private static void DrawVerticalSpeed(DrawingContext dc, LandingMonitorReading guidance)
    {
        var panel = new Rect(1194, 319, 330, 176);
        DrawPanel(dc, panel);
        var verticalSpeed = guidance.VerticalSpeedFpm;
        var value = verticalSpeed is { } fpm
            ? $"{(Math.Abs(fpm) < 0.5 ? 0 : fpm):0}"
            : "—";
        var color = StatusBrush(guidance.DescentAngleStatus);
        DrawText(dc, "VERTICAL SPEED", 12, LabelBrush, 1222, 348, TextAlignment.Left, semibold: true);
        DrawText(dc, value, 45, color, 1222, 382, TextAlignment.Left, mono: true);
        DrawText(dc, "FPM", 13, LabelBrush, 1495, 405, TextAlignment.Right, semibold: true);
        var target = guidance.TargetVerticalSpeedFpm is { } targetFpm
            ? $"TARGET {targetFpm:0} FPM"
            : "TARGET —";
        DrawText(dc, target, 11, LabelBrush, 1222, 458, TextAlignment.Left, mono: true);
    }

    private static void DrawAirspeed(DrawingContext dc, LandingMonitorReading guidance)
    {
        var panel = new Rect(625, 747, 350, 122);
        DrawPanel(dc, panel);
        var value = guidance.AirspeedKts is { } airspeed ? $"{airspeed:0}" : "—";
        var color = StatusBrush(guidance.AirspeedStatus);
        DrawText(dc, "IAS", 12, LabelBrush, 657, 773, TextAlignment.Left, semibold: true);
        DrawText(dc, value, 48, color, 800, 785, TextAlignment.Center, mono: true);
        DrawText(dc, "KT", 13, LabelBrush, 944, 809, TextAlignment.Right, semibold: true);
    }

    private static void DrawPanel(DrawingContext dc, Rect rectangle) =>
        dc.DrawRoundedRectangle(PanelBrush, new Pen(PanelBorderBrush, 1.2), rectangle, 14, 14);

    private static void DrawAircraftReference(DrawingContext dc, Point center)
    {
        var pen = new Pen(WithOpacity(WindBrush, 0.85), 2);
        dc.DrawLine(pen, new Point(center.X, center.Y - 14), new Point(center.X, center.Y + 16));
        dc.DrawLine(pen, new Point(center.X - 11, center.Y + 5), new Point(center.X + 11, center.Y + 5));
        dc.DrawLine(pen, new Point(center.X, center.Y - 14), new Point(center.X - 5, center.Y - 6));
        dc.DrawLine(pen, new Point(center.X, center.Y - 14), new Point(center.X + 5, center.Y - 6));
    }

    private static void DrawWindArrow(DrawingContext dc, Point center, double relativeFromDegrees)
    {
        var radians = (relativeFromDegrees - 90) * Math.PI / 180.0;
        var source = new Point(
            center.X + Math.Cos(radians) * 31,
            center.Y + Math.Sin(radians) * 31);
        var target = new Point(
            center.X + Math.Cos(radians) * 17,
            center.Y + Math.Sin(radians) * 17);
        var pen = new Pen(WindBrush, 3);
        dc.DrawLine(pen, source, target);

        var direction = Math.Atan2(target.Y - source.Y, target.X - source.X);
        var left = new Point(
            target.X - Math.Cos(direction - 0.55) * 9,
            target.Y - Math.Sin(direction - 0.55) * 9);
        var right = new Point(
            target.X - Math.Cos(direction + 0.55) * 9,
            target.Y - Math.Sin(direction + 0.55) * 9);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(target, true, true);
            context.LineTo(left, true, false);
            context.LineTo(right, true, false);
        }
        geometry.Freeze();
        dc.DrawGeometry(WindBrush, null, geometry);
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

    private static void DrawText(
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
        var formatted = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            WpfFlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush,
            1.0)
        {
            TextAlignment = alignment,
        };
        dc.DrawText(formatted, new Point(x, y));
    }

    private static Brush FrozenBrush(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
        brush.Freeze();
        return brush;
    }

    private static Brush WithOpacity(Brush source, double opacity)
    {
        var clone = source.Clone();
        clone.Opacity = opacity;
        clone.Freeze();
        return clone;
    }
}
