using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ChallengeLab.Core.Highscores;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using WpfFlowDirection = System.Windows.FlowDirection;

namespace ChallengeLab.App.Controls;

/// <summary>Dependency-free projected score/time plot shared by the HUD and landing report.</summary>
public sealed class LandingScoreGraph : FrameworkElement
{
    private static readonly Pen GridPen = FrozenPen(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF), 1);
    private static readonly Pen ScorePen = FrozenPen(Color.FromRgb(0x2D, 0xE2, 0xE6), 2);
    private static readonly Pen LossPen = FrozenPen(Color.FromRgb(0xFF, 0x6B, 0x6B), 2.5);
    private static readonly Brush LabelBrush = FrozenBrush(Color.FromRgb(0x8B, 0x9B, 0xB8));

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points),
        typeof(IReadOnlyList<ScoreHistoryPoint>),
        typeof(LandingScoreGraph),
        new FrameworkPropertyMetadata(
            Array.Empty<ScoreHistoryPoint>(),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ScoreHistoryPoint> Points
    {
        get => (IReadOnlyList<ScoreHistoryPoint>)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public static readonly DependencyProperty HorizonSecondsProperty = DependencyProperty.Register(
        nameof(HorizonSeconds),
        typeof(double),
        typeof(LandingScoreGraph),
        new FrameworkPropertyMetadata(30.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double HorizonSeconds
    {
        get => (double)GetValue(HorizonSecondsProperty);
        set => SetValue(HorizonSecondsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 50 || ActualHeight <= 40)
            return;

        const double left = 30;
        const double right = 8;
        const double top = 7;
        const double bottom = 20;
        var width = ActualWidth - left - right;
        var height = ActualHeight - top - bottom;
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        foreach (var score in new[] { 100, 50, 0 })
        {
            var y = top + (100 - score) / 100.0 * height;
            drawingContext.DrawLine(GridPen, new Point(left, y), new Point(left + width, y));
            DrawLabel(drawingContext, score.ToString(CultureInfo.InvariantCulture), 2, y - 7, pixelsPerDip);
        }

        var points = Points ?? Array.Empty<ScoreHistoryPoint>();
        var lastElapsed = points.Count > 0 ? points[^1].ElapsedSeconds : 0;
        var configuredHorizon = double.IsFinite(HorizonSeconds) && HorizonSeconds > 0
            ? HorizonSeconds
            : 30;
        // Keep the ETA-derived scale fixed. Only extend it if the real attempt outlives
        // the estimate plus its ten-second buffer, otherwise new points would disappear.
        var span = Math.Max(configuredHorizon, lastElapsed);
        DrawLabel(drawingContext, FormatAxisTime(0), left, top + height + 4, pixelsPerDip);
        var endLabel = FormatAxisTime(span);
        var endText = CreateText(endLabel, pixelsPerDip);
        drawingContext.DrawText(endText, new Point(left + width - endText.Width, top + height + 4));

        if (points.Count == 0)
        {
            var waiting = CreateText("Waiting for collection window", pixelsPerDip);
            drawingContext.DrawText(
                waiting,
                new Point(left + (width - waiting.Width) / 2, top + (height - waiting.Height) / 2));
            return;
        }

        drawingContext.PushClip(new RectangleGeometry(new Rect(left, top, width, height)));
        if (points.Count == 1)
        {
            drawingContext.DrawEllipse(
                ScorePen.Brush,
                null,
                ToPlotPoint(points[0], left, top, width, height, span),
                2.5,
                2.5);
        }
        else
        {
            for (var index = 1; index < points.Count; index++)
            {
                var previous = points[index - 1];
                var current = points[index];
                var pen = current.ScorePercent < previous.ScorePercent ? LossPen : ScorePen;
                drawingContext.DrawLine(
                    pen,
                    ToPlotPoint(previous, left, top, width, height, span),
                    ToPlotPoint(current, left, top, width, height, span));
            }
        }

        var last = points[^1];
        var lastPoint = ToPlotPoint(last, left, top, width, height, span);
        drawingContext.DrawEllipse(ScorePen.Brush, null, lastPoint, 3.5, 3.5);
        drawingContext.Pop();

        var finalLabel = CreateText($"{last.ScorePercent:0.0}%", pixelsPerDip);
        var labelX = Math.Clamp(lastPoint.X - finalLabel.Width - 6, left, left + width - finalLabel.Width);
        var labelY = Math.Clamp(lastPoint.Y - finalLabel.Height - 4, top, top + height - finalLabel.Height);
        drawingContext.DrawText(finalLabel, new Point(labelX, labelY));
    }

    private static Point ToPlotPoint(
        ScoreHistoryPoint point,
        double left,
        double top,
        double width,
        double height,
        double span) => new(
        left + Math.Clamp(point.ElapsedSeconds / span, 0, 1) * width,
        top + (100 - Math.Clamp(point.ScorePercent, 0, 100)) / 100.0 * height);

    private static void DrawLabel(
        DrawingContext drawingContext,
        string text,
        double x,
        double y,
        double pixelsPerDip) =>
        drawingContext.DrawText(CreateText(text, pixelsPerDip), new Point(x, y));

    private static FormattedText CreateText(string text, double pixelsPerDip) => new(
        text,
        CultureInfo.InvariantCulture,
        WpfFlowDirection.LeftToRight,
        new Typeface("Cascadia Mono, Consolas"),
        9,
        LabelBrush,
        pixelsPerDip);

    private static string FormatAxisTime(double elapsedSeconds)
    {
        var seconds = Math.Max(0, (int)Math.Round(elapsedSeconds));
        return $"{seconds / 60:00}:{seconds % 60:00}";
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
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
