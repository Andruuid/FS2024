using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ChallengeLab.App.Controls;

/// <summary>
/// Draws an aircraft-fixed wind field. The airplane always points up; positive
/// relative-from angles place the source on the aircraft's right side.
/// </summary>
public sealed class WindFlowIndicator : FrameworkElement
{
    private const double MinimumAnimatedWindKts = 0.5;
    private const double DirectionSmoothingSeconds = 0.25;
    private const double SpeedSmoothingSeconds = 0.35;

    private static readonly Brush AircraftBrush = FrozenBrush(Color.FromRgb(0xE8, 0xF2, 0xFF));
    private static readonly Pen AircraftOutlinePen = FrozenPen(Color.FromArgb(0xD0, 0x12, 0x1A, 0x2E), 1.4);
    private static readonly Pen[] WindPens =
    [
        FrozenPen(Color.FromArgb(0x70, 0x7A, 0xFF, 0xFF), 1.2),
        FrozenPen(Color.FromArgb(0x98, 0x7A, 0xFF, 0xFF), 1.5),
        FrozenPen(Color.FromArgb(0xC0, 0x2D, 0xE2, 0xE6), 1.8),
        FrozenPen(Color.FromArgb(0xE0, 0x2D, 0xE2, 0xE6), 2.0)
    ];

    private static readonly Geometry AircraftGeometry = CreateAircraftGeometry();
    private static readonly WindStreak[] Streaks =
    [
        new(-0.42, 0.04, 7, 0.7, 0),
        new(-0.27, 0.43, 10, 0.9, 1),
        new(-0.10, 0.72, 8, 1.1, 2),
        new(0.08, 0.20, 13, 1.3, 3),
        new(0.24, 0.58, 9, 0.7, 1),
        new(0.40, 0.86, 12, 1.1, 2),
        new(-0.34, 0.91, 6, 1.3, 3),
        new(0.33, 0.31, 7, 0.9, 0)
    ];

    private bool _isRendering;
    private long _lastFrameTicks;
    private double _elapsedSeconds;
    private double _displayAngle;
    private double _displaySpeed;
    private bool _displayStateInitialized;

    public WindFlowIndicator()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public static readonly DependencyProperty RelativeFromAngleProperty = DependencyProperty.Register(
        nameof(RelativeFromAngle),
        typeof(double),
        typeof(WindFlowIndicator),
        new FrameworkPropertyMetadata(0.0, OnAnimationPropertyChanged));

    public double RelativeFromAngle
    {
        get => (double)GetValue(RelativeFromAngleProperty);
        set => SetValue(RelativeFromAngleProperty, value);
    }

    public static readonly DependencyProperty WindSpeedKtsProperty = DependencyProperty.Register(
        nameof(WindSpeedKts),
        typeof(double),
        typeof(WindFlowIndicator),
        new FrameworkPropertyMetadata(0.0, OnAnimationPropertyChanged));

    public double WindSpeedKts
    {
        get => (double)GetValue(WindSpeedKtsProperty);
        set => SetValue(WindSpeedKtsProperty, value);
    }

    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive),
        typeof(bool),
        typeof(WindFlowIndicator),
        new FrameworkPropertyMetadata(false, OnAnimationPropertyChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? Math.Min(72, availableSize.Width) : 72;
        var height = double.IsFinite(availableSize.Height) ? Math.Min(72, availableSize.Height) : 72;
        return new Size(Math.Max(0, width), Math.Max(0, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (ActualWidth <= 1 || ActualHeight <= 1)
            return;

        if (IsActive && double.IsFinite(WindSpeedKts) && WindSpeedKts >= MinimumAnimatedWindKts)
            DrawWind(drawingContext);
        DrawAircraft(drawingContext);
    }

    private void DrawWind(DrawingContext drawingContext)
    {
        var center = new Point(ActualWidth / 2.0, ActualHeight / 2.0);
        var extent = Math.Sqrt(ActualWidth * ActualWidth + ActualHeight * ActualHeight) + 24;
        var travel = extent + 24;
        var minY = center.Y - travel / 2.0;
        var speed = PixelsPerSecond(_displayStateInitialized ? _displaySpeed : WindSpeedKts);
        var angle = _displayStateInitialized ? _displayAngle : NormalizeSignedAngle(RelativeFromAngle);

        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight), 8, 8));
        drawingContext.PushTransform(new RotateTransform(angle, center.X, center.Y));
        foreach (var streak in Streaks)
        {
            var x = center.X + streak.CrossPosition * extent;
            var distance = PositiveModulo(
                streak.StartPhase * travel + _elapsedSeconds * speed * streak.SpeedMultiplier,
                travel);
            var y = minY + distance;
            drawingContext.DrawLine(
                WindPens[streak.PenIndex],
                new Point(x, y - streak.Length / 2.0),
                new Point(x, y + streak.Length / 2.0));
        }
        drawingContext.Pop();
        drawingContext.Pop();
    }

    private void DrawAircraft(DrawingContext drawingContext)
    {
        var scale = Math.Min(ActualWidth / 46.0, ActualHeight / 58.0);
        var bounds = AircraftGeometry.Bounds;
        var x = (ActualWidth - bounds.Width * scale) / 2.0 - bounds.X * scale;
        var y = (ActualHeight - bounds.Height * scale) / 2.0 - bounds.Y * scale;
        drawingContext.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
        drawingContext.DrawGeometry(AircraftBrush, AircraftOutlinePen, AircraftGeometry);
        drawingContext.Pop();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => UpdateAnimationSubscription();

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopRendering();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        UpdateAnimationSubscription();

    private void UpdateAnimationSubscription()
    {
        var shouldRender = IsLoaded
                           && IsVisible
                           && IsActive
                           && double.IsFinite(WindSpeedKts)
                           && WindSpeedKts >= MinimumAnimatedWindKts;
        if (shouldRender)
            StartRendering();
        else
            StopRendering();
    }

    private void StartRendering()
    {
        if (_isRendering)
            return;

        InitializeDisplayStateIfNeeded();
        _lastFrameTicks = 0;
        CompositionTarget.Rendering += OnRendering;
        _isRendering = true;
    }

    private void StopRendering()
    {
        if (_isRendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _isRendering = false;
        }
        _lastFrameTicks = 0;
        InvalidateVisual();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        if (_lastFrameTicks == 0)
        {
            _lastFrameTicks = now;
            return;
        }

        var elapsed = Math.Clamp(
            (now - _lastFrameTicks) / (double)Stopwatch.Frequency,
            0,
            0.05);
        _lastFrameTicks = now;

        var targetAngle = NormalizeSignedAngle(RelativeFromAngle);
        var angleAlpha = 1.0 - Math.Exp(-elapsed / DirectionSmoothingSeconds);
        _displayAngle = NormalizeSignedAngle(
            _displayAngle + NormalizeSignedAngle(targetAngle - _displayAngle) * angleAlpha);

        var targetSpeed = Math.Max(0, double.IsFinite(WindSpeedKts) ? WindSpeedKts : 0);
        var speedAlpha = 1.0 - Math.Exp(-elapsed / SpeedSmoothingSeconds);
        _displaySpeed += (targetSpeed - _displaySpeed) * speedAlpha;
        _elapsedSeconds = PositiveModulo(_elapsedSeconds + elapsed, 3600);
        InvalidateVisual();
    }

    private void InitializeDisplayStateIfNeeded()
    {
        if (_displayStateInitialized)
            return;
        _displayAngle = NormalizeSignedAngle(RelativeFromAngle);
        _displaySpeed = Math.Max(0, double.IsFinite(WindSpeedKts) ? WindSpeedKts : 0);
        _displayStateInitialized = true;
    }

    private static void OnAnimationPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var indicator = (WindFlowIndicator)d;
        indicator.InitializeDisplayStateIfNeeded();
        indicator.UpdateAnimationSubscription();
        indicator.InvalidateVisual();
    }

    private static double PixelsPerSecond(double windSpeedKts) =>
        Math.Clamp(30.0 + Math.Max(0, windSpeedKts) * 2.0, 30.0, 160.0);

    private static double NormalizeSignedAngle(double degrees)
    {
        if (!double.IsFinite(degrees))
            return 0;
        var normalized = degrees % 360.0;
        if (normalized > 180) normalized -= 360;
        if (normalized < -180) normalized += 360;
        return normalized;
    }

    private static double PositiveModulo(double value, double modulus)
    {
        var result = value % modulus;
        return result < 0 ? result + modulus : result;
    }

    private static Geometry CreateAircraftGeometry()
    {
        var geometry = Geometry.Parse(
            "M 16,0 C 18,0 19,2 19,5 L 19,15 L 30,24 L 30,28 L 19,23 " +
            "L 19,35 L 25,40 L 25,43 L 16,40 L 7,43 L 7,40 L 13,35 " +
            "L 13,23 L 2,28 L 2,24 L 13,15 L 13,5 C 13,2 14,0 16,0 Z");
        geometry.Freeze();
        return geometry;
    }

    private static Brush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color color, double thickness)
    {
        var pen = new Pen(FrozenBrush(color), thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round
        };
        pen.Freeze();
        return pen;
    }

    private readonly record struct WindStreak(
        double CrossPosition,
        double StartPhase,
        double Length,
        double SpeedMultiplier,
        int PenIndex);
}
