using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using WpfFlowDirection = System.Windows.FlowDirection;

namespace ChallengeLab.App.Controls.Aether;

/// <summary>
/// Proportional Aether approach surface: glass pillars, dual-ring path core, wind disc,
/// sparklines, and a quality halo. Layout is solved from ActualWidth/Height each frame.
/// </summary>
public sealed class AetherSurface : FrameworkElement
{
    private readonly AetherHistoryBuffer _history = new();
    private readonly double?[] _sparkScratch = new double?[64];

    private AetherSnapshot? _snapshot;
    private double _displayScale = 1.0;
    private double _displayOpacity = 0.96;
    private double _fontScale = 1.0;

    private double _smoothPathError;
    private double _smoothDescentError;
    private double _smoothIas;
    private double _smoothVs;
    private double _smoothWindAngle;
    private bool _hasSmooth;

    public AetherSurface()
    {
        SnapsToDevicePixels = false;
        IsHitTestVisible = false;
    }

    internal double DisplayScale => _displayScale;
    internal double DisplayOpacity => _displayOpacity;
    internal double FontScale => _fontScale;

    internal void ApplySnapshot(AetherSnapshot? snapshot)
    {
        _snapshot = snapshot;
        if (snapshot is null || !snapshot.IsConnected)
        {
            _history.Clear();
            _hasSmooth = false;
        }
        else
        {
            _history.Push(snapshot);
            SmoothToward(snapshot);
        }

        InvalidateVisual();
    }

    internal void SetDisplayScale(double scale)
    {
        _displayScale = Math.Clamp(scale, 0.55, 1.3);
        InvalidateVisual();
    }

    internal void SetDisplayOpacity(double opacity)
    {
        _displayOpacity = Math.Clamp(opacity, 0.2, 1.0);
        InvalidateVisual();
    }

    internal void SetFontScale(double scale)
    {
        _fontScale = Math.Clamp(scale, 0.75, 1.35);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 40 || ActualHeight < 40)
            return;

        var snapshot = _snapshot;
        if (snapshot is null)
            return;

        var w = ActualWidth;
        var h = ActualHeight;
        var margin = Math.Min(w, h) * 0.035 * _displayScale;
        var pillarWidth = Math.Clamp(w * 0.12 * _displayScale, 88, 150);
        var ringRadius = Math.Clamp(Math.Min(w, h) * 0.11 * _displayScale, 70, 120);

        dc.PushOpacity(_displayOpacity);

        var quality = AetherTheme.Worst(
            snapshot.Energy.IasTone,
            snapshot.Path.PathTone,
            snapshot.Path.DescentTone);
        DrawAmbientHalo(dc, w, h, quality, snapshot.IsFlightActive);

        if (!snapshot.IsConnected)
        {
            DrawDisconnected(dc, w, h);
            dc.Pop();
            return;
        }

        if (!snapshot.IsFlightActive)
        {
            DrawWaiting(dc, w, h);
            dc.Pop();
            return;
        }

        // Wind disc — top center
        var windCenter = new Point(w * 0.5, margin + 52 * _displayScale);
        DrawWindDisc(dc, windCenter, 40 * _displayScale, snapshot.Wind);

        // Side pillars
        var leftRect = new Rect(
            margin,
            h * 0.22,
            pillarWidth,
            h * 0.52);
        var rightRect = new Rect(
            w - margin - pillarWidth,
            h * 0.22,
            pillarWidth,
            h * 0.52);
        DrawIasPillar(dc, leftRect, snapshot.Energy);
        DrawVsPillar(dc, rightRect, snapshot.Energy);

        // Dual-ring path core
        var core = new Point(w * 0.5, h * 0.48);
        DrawPathCore(dc, core, ringRadius, snapshot.Path);

        // Progress rail
        var railRect = new Rect(
            w * 0.28,
            h - margin - 36 * _displayScale,
            w * 0.44,
            28 * _displayScale);
        DrawProgressRail(dc, railRect, snapshot.Path);

        // Brand chip
        DrawText(dc, "AETHER", 10, AetherTheme.Violet,
            margin + 4, margin, TextAlignment.Left, AetherTheme.Micro);

        dc.Pop();
    }

    private void SmoothToward(AetherSnapshot snapshot)
    {
        const double alpha = 0.28;
        var pathErr = snapshot.Path.PathErrorDeg ?? 0;
        var descentErr = snapshot.Path.DescentErrorDeg ?? 0;
        var ias = snapshot.Energy.IasKts ?? _smoothIas;
        var vs = snapshot.Energy.VerticalSpeedFpm ?? _smoothVs;
        var wind = snapshot.Wind.RelativeFromDeg;

        if (!_hasSmooth)
        {
            _smoothPathError = pathErr;
            _smoothDescentError = descentErr;
            _smoothIas = ias;
            _smoothVs = vs;
            _smoothWindAngle = wind;
            _hasSmooth = true;
            return;
        }

        _smoothPathError = Lerp(_smoothPathError, pathErr, alpha);
        _smoothDescentError = Lerp(_smoothDescentError, descentErr, alpha);
        _smoothIas = Lerp(_smoothIas, ias, alpha);
        _smoothVs = Lerp(_smoothVs, vs, alpha);
        _smoothWindAngle = LerpAngle(_smoothWindAngle, wind, alpha);
    }

    private static void DrawAmbientHalo(DrawingContext dc, double w, double h, AetherTone tone, bool active)
    {
        if (!active || tone is AetherTone.Quiet)
            return;

        var brush = AetherTheme.HaloBrush(tone);
        if (ReferenceEquals(brush, AetherTheme.Transparent))
            return;

        // Soft vignette bands at edges — center stays clear for the runway picture.
        dc.DrawRectangle(brush, null, new Rect(0, 0, w, h * 0.08));
        dc.DrawRectangle(brush, null, new Rect(0, h * 0.92, w, h * 0.08));
        dc.DrawRectangle(brush, null, new Rect(0, 0, w * 0.06, h));
        dc.DrawRectangle(brush, null, new Rect(w * 0.94, 0, w * 0.06, h));
    }

    private void DrawDisconnected(DrawingContext dc, double w, double h)
    {
        DrawGlassCard(dc, new Rect(w * 0.5 - 110, h * 0.5 - 28, 220, 56));
        DrawText(dc, "AETHER  ·  NO LINK", 14, AetherTheme.TextMuted,
            w * 0.5, h * 0.5 - 8, TextAlignment.Center, AetherTheme.Body);
    }

    private void DrawWaiting(DrawingContext dc, double w, double h)
    {
        DrawGlassCard(dc, new Rect(w * 0.5 - 120, h * 0.5 - 28, 240, 56));
        DrawText(dc, "AETHER  ·  STANDBY", 14, AetherTheme.Cyan,
            w * 0.5, h * 0.5 - 8, TextAlignment.Center, AetherTheme.Body);
    }

    private void DrawWindDisc(DrawingContext dc, Point center, double radius, AetherWind wind)
    {
        DrawGlassCard(dc, new Rect(center.X - radius - 70, center.Y - radius - 8, radius * 2 + 140, radius * 2 + 28));

        var ringPen = new Pen(AetherTheme.GlassStrokeSoft, 1.5) { DashStyle = DashStyles.Dot };
        ringPen.Freeze();
        dc.DrawEllipse(null, ringPen, center, radius, radius);
        dc.DrawEllipse(null, AetherTheme.GlassStrokePen, center, radius * 0.35, radius * 0.35);

        if (!wind.Available)
        {
            DrawText(dc, "— KT", 18, AetherTheme.TextMuted, center.X + radius + 18, center.Y - 18,
                TextAlignment.Left, AetherTheme.Mono);
            DrawText(dc, "XWIND —", 12, AetherTheme.TextDim, center.X + radius + 18, center.Y + 4,
                TextAlignment.Left, AetherTheme.Body);
            return;
        }

        // Relative wind vector (FROM direction: 0 = head, positive = from right)
        dc.PushTransform(new RotateTransform(_smoothWindAngle, center.X, center.Y));
        var tip = new Point(center.X, center.Y - radius * 0.88);
        var baseL = new Point(center.X - 7, center.Y - radius * 0.45);
        var baseR = new Point(center.X + 7, center.Y - radius * 0.45);
        var shaft = new Pen(AetherTheme.Cyan, 2.4)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        };
        dc.DrawLine(shaft, new Point(center.X, center.Y + radius * 0.15), tip);
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(tip, true, true);
            ctx.LineTo(baseL, true, false);
            ctx.LineTo(baseR, true, false);
        }
        head.Freeze();
        dc.DrawGeometry(AetherTheme.Cyan, null, head);
        dc.Pop();

        // Crosswind proportion arc
        var crossFrac = Math.Clamp(Math.Abs(wind.CrosswindKts) / Math.Max(wind.SpeedKts, 0.1), 0, 1);
        if (crossFrac > 0.02)
        {
            var sweep = 70 * crossFrac * (wind.CrosswindKts >= 0 ? 1 : -1);
            DrawArc(dc, center, radius * 0.72, -90, sweep, AetherTheme.Magenta, 3.0);
        }

        DrawText(dc, $"{wind.SpeedKts:0.0} KT", 18, AetherTheme.Cyan,
            center.X + radius + 18, center.Y - 18, TextAlignment.Left, AetherTheme.Mono);
        DrawText(dc, wind.CrosswindLabel, 12, AetherTheme.Violet,
            center.X + radius + 18, center.Y + 4, TextAlignment.Left, AetherTheme.Body);
    }

    private void DrawIasPillar(DrawingContext dc, Rect rect, AetherEnergy energy)
    {
        DrawGlassCard(dc, rect);
        DrawText(dc, "IAS", 11, AetherTheme.TextMuted,
            rect.X + 12, rect.Y + 10, TextAlignment.Left, AetherTheme.Micro);

        var tone = AetherTheme.ToneBrush(energy.IasTone);
        var value = energy.IasKts is { } ias ? $"{Math.Round(_smoothIas):0}" : "—";
        DrawText(dc, value, 36, tone,
            rect.X + rect.Width * 0.5, rect.Y + 36, TextAlignment.Center, AetherTheme.Mono);
        DrawText(dc, "KT", 11, AetherTheme.TextDim,
            rect.X + rect.Width * 0.5, rect.Y + 76, TextAlignment.Center, AetherTheme.Body);

        if (energy.IasDeltaKts is { } delta)
        {
            var sign = delta >= 0 ? "+" : "";
            var deltaBrush = Math.Abs(delta) < 3 ? AetherTheme.Good : tone;
            DrawText(dc, $"{sign}{delta:0} Δ", 13, deltaBrush,
                rect.X + rect.Width * 0.5, rect.Y + 96, TextAlignment.Center, AetherTheme.Mono);
        }

        // Vertical tape with target chevron
        var tape = new Rect(rect.X + 18, rect.Y + 120, rect.Width - 36, rect.Height - 150);
        DrawVerticalTape(dc, tape, energy.IasKts, energy.TargetIasKts, 40, true, tone);

        // Sparkline
        var spark = new Rect(rect.X + 14, rect.Bottom - 34, rect.Width - 28, 22);
        _history.CopyIas(_sparkScratch);
        DrawSparkline(dc, spark, _sparkScratch, _history.Count, AetherTheme.Cyan);
    }

    private void DrawVsPillar(DrawingContext dc, Rect rect, AetherEnergy energy)
    {
        DrawGlassCard(dc, rect);
        DrawText(dc, "V/S", 11, AetherTheme.TextMuted,
            rect.X + 12, rect.Y + 10, TextAlignment.Left, AetherTheme.Micro);

        var tone = AetherTheme.ToneBrush(energy.VerticalSpeedTone);
        var value = energy.VerticalSpeedFpm is { } vs
            ? $"{(Math.Abs(_smoothVs) < 0.5 ? 0 : Math.Round(_smoothVs)):0}"
            : "—";
        DrawText(dc, value, 32, tone,
            rect.X + rect.Width * 0.5, rect.Y + 36, TextAlignment.Center, AetherTheme.Mono);
        DrawText(dc, "FPM", 11, AetherTheme.TextDim,
            rect.X + rect.Width * 0.5, rect.Y + 76, TextAlignment.Center, AetherTheme.Body);

        if (energy.TargetVerticalSpeedFpm is { } tgt)
        {
            DrawText(dc, $"TGT {tgt:0}", 11, AetherTheme.TextMuted,
                rect.X + rect.Width * 0.5, rect.Y + 96, TextAlignment.Center, AetherTheme.Mono);
        }

        var tape = new Rect(rect.X + 18, rect.Y + 120, rect.Width - 36, rect.Height - 150);
        DrawVerticalTape(dc, tape, energy.VerticalSpeedFpm, energy.TargetVerticalSpeedFpm, 1200, false, tone);

        var spark = new Rect(rect.X + 14, rect.Bottom - 34, rect.Width - 28, 22);
        _history.CopyVerticalSpeed(_sparkScratch);
        DrawSparkline(dc, spark, _sparkScratch, _history.Count, AetherTheme.Violet);
    }

    private void DrawPathCore(DrawingContext dc, Point center, double radius, AetherPath path)
    {
        // Soft glass disc behind rings
        var card = new Rect(center.X - radius - 18, center.Y - radius - 18, radius * 2 + 36, radius * 2 + 56);
        DrawGlassCard(dc, card);

        // Track rings
        dc.DrawEllipse(null, AetherTheme.TrackPen, center, radius, radius);
        dc.DrawEllipse(null, AetherTheme.TrackPen, center, radius * 0.62, radius * 0.62);

        // Green band ticks on outer ring (±0.2° mapped to ±22°)
        DrawArc(dc, center, radius, -22, 44, AetherTheme.Good, 4.0);

        if (!path.HasTarget)
        {
            DrawText(dc, "NO RWY", 14, AetherTheme.TextMuted,
                center.X, center.Y - 8, TextAlignment.Center, AetherTheme.Body);
            DrawText(dc, "path unlocked", 11, AetherTheme.TextDim,
                center.X, center.Y + 12, TextAlignment.Center, AetherTheme.Body);
            return;
        }

        // Outer needle: path error (positive = high → rotate right)
        var pathNeedle = Math.Clamp(_smoothPathError / 1.2, -1, 1) * 70;
        DrawNeedle(dc, center, radius * 0.92, pathNeedle, AetherTheme.ToneBrush(path.PathTone), 3.2);

        // Inner needle: descent error
        var descentNeedle = Math.Clamp(_smoothDescentError / 1.2, -1, 1) * 70;
        DrawNeedle(dc, center, radius * 0.54, descentNeedle, AetherTheme.ToneBrush(path.DescentTone), 2.6);

        // Center readouts
        var pathVal = path.PathAngleDeg is { } p ? $"{p:0.0}°" : "—";
        var desVal = path.DescentAngleDeg is { } d ? $"{d:0.0}°" : "—";
        DrawText(dc, pathVal, 22, AetherTheme.ToneBrush(path.PathTone),
            center.X, center.Y - 22, TextAlignment.Center, AetherTheme.Mono);
        DrawText(dc, "PATH", 9, AetherTheme.TextDim,
            center.X, center.Y - 2, TextAlignment.Center, AetherTheme.Micro);
        DrawText(dc, desVal, 16, AetherTheme.ToneBrush(path.DescentTone),
            center.X, center.Y + 12, TextAlignment.Center, AetherTheme.Mono);
        DrawText(dc, "DESCENT", 9, AetherTheme.TextDim,
            center.X, center.Y + 30, TextAlignment.Center, AetherTheme.Micro);

        if (path.TargetAngleDeg is { } tgt)
        {
            DrawText(dc, $"TARGET {tgt:0.0}°", 10, AetherTheme.Violet,
                center.X, center.Y + radius + 6, TextAlignment.Center, AetherTheme.Mono);
        }

        // Status badges
        var pathBadge = PathBadge(path);
        var desBadge = DescentBadge(path);
        if (pathBadge is not null)
        {
            DrawBadge(dc, new Point(center.X - radius - 8, center.Y - radius * 0.2), pathBadge,
                AetherTheme.ToneBrush(path.PathTone));
        }

        if (desBadge is not null)
        {
            DrawBadge(dc, new Point(center.X + radius + 8, center.Y - radius * 0.2), desBadge,
                AetherTheme.ToneBrush(path.DescentTone), alignRight: false);
        }
    }

    private void DrawProgressRail(DrawingContext dc, Rect rect, AetherPath path)
    {
        DrawGlassCard(dc, rect);
        var inner = new Rect(rect.X + 10, rect.Y + 10, rect.Width - 20, 8);
        dc.DrawRoundedRectangle(AetherTheme.Track, null, inner, 4, 4);

        if (path.ProgressPercent is { } progress)
        {
            var fillW = inner.Width * Math.Clamp(progress / 100.0, 0, 1);
            if (fillW > 1)
            {
                var fill = new Rect(inner.X, inner.Y, fillW, inner.Height);
                var brush = path.InsideCollectionWindow ? AetherTheme.Cyan : AetherTheme.Violet;
                dc.DrawRoundedRectangle(brush, null, fill, 4, 4);
            }
        }

        var dist = path.DistanceNm is { } nm ? $"{nm:0.00} NM" : "— NM";
        var window = path.InsideCollectionWindow ? "CAPTURE" : "APPROACH";
        DrawText(dc, dist, 11, AetherTheme.TextPrimary,
            rect.X + 12, rect.Y + 2, TextAlignment.Left, AetherTheme.Mono);
        DrawText(dc, window, 10, path.InsideCollectionWindow ? AetherTheme.Good : AetherTheme.TextMuted,
            rect.Right - 12, rect.Y + 2, TextAlignment.Right, AetherTheme.Micro);
    }

    private static void DrawVerticalTape(
        DrawingContext dc,
        Rect tape,
        double? value,
        double? target,
        double halfRange,
        bool higherIsUp,
        Brush valueBrush)
    {
        dc.DrawRoundedRectangle(AetherTheme.GlassFillStrong, AetherTheme.GlassStrokeSoftPen, tape, 6, 6);
        var midY = tape.Y + tape.Height * 0.5;
        dc.DrawLine(new Pen(AetherTheme.TextDim, 1), new Point(tape.X + 4, midY), new Point(tape.Right - 4, midY));

        if (value is not { } live || !double.IsFinite(live))
            return;

        double CenterOffset(double v)
        {
            var frac = Math.Clamp(v / halfRange, -1, 1);
            return (higherIsUp ? -frac : frac) * (tape.Height * 0.42);
        }

        var liveY = midY + CenterOffset(live - (target ?? live));
        // When no target, pin live to center and show absolute ticks via target=null path:
        if (target is null)
            liveY = midY;

        if (target is { } tgt && double.IsFinite(tgt))
        {
            var tgtY = midY + CenterOffset(0); // target at center
            liveY = midY + CenterOffset(live - tgt);
            // Target chevron at center
            var chevron = new StreamGeometry();
            using (var ctx = chevron.Open())
            {
                ctx.BeginFigure(new Point(tape.X + 3, tgtY), true, true);
                ctx.LineTo(new Point(tape.X + 12, tgtY - 6), true, false);
                ctx.LineTo(new Point(tape.X + 12, tgtY + 6), true, false);
            }
            chevron.Freeze();
            dc.DrawGeometry(AetherTheme.Violet, null, chevron);
        }

        liveY = Math.Clamp(liveY, tape.Y + 6, tape.Bottom - 6);
        dc.DrawEllipse(valueBrush, null, new Point(tape.X + tape.Width * 0.55, liveY), 5, 5);
        dc.DrawLine(new Pen(valueBrush, 2),
            new Point(tape.X + 14, liveY),
            new Point(tape.Right - 8, liveY));
    }

    private static void DrawSparkline(DrawingContext dc, Rect rect, double?[] samples, int count, Brush color)
    {
        if (count < 2)
            return;

        double? min = null, max = null;
        for (var i = 0; i < count; i++)
        {
            if (samples[i] is not { } v || !double.IsFinite(v))
                continue;
            min = min is null ? v : Math.Min(min.Value, v);
            max = max is null ? v : Math.Max(max.Value, v);
        }

        if (min is null || max is null)
            return;

        var span = Math.Max(max.Value - min.Value, 1e-3);
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var started = false;
            for (var i = 0; i < count; i++)
            {
                if (samples[i] is not { } v || !double.IsFinite(v))
                    continue;
                var x = rect.X + rect.Width * (i / (double)Math.Max(count - 1, 1));
                var y = rect.Bottom - rect.Height * ((v - min.Value) / span);
                if (!started)
                {
                    ctx.BeginFigure(new Point(x, y), false, false);
                    started = true;
                }
                else
                {
                    ctx.LineTo(new Point(x, y), true, false);
                }
            }
        }

        geo.Freeze();
        var pen = new Pen(color, 1.5)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.DrawGeometry(null, pen, geo);
    }

    private static void DrawNeedle(DrawingContext dc, Point center, double length, double angleDeg, Brush brush, double thickness)
    {
        dc.PushTransform(new RotateTransform(angleDeg, center.X, center.Y));
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Triangle,
        };
        dc.DrawLine(pen, center, new Point(center.X, center.Y - length));
        dc.DrawEllipse(brush, null, new Point(center.X, center.Y - length), 3.5, 3.5);
        dc.Pop();
    }

    private static void DrawArc(
        DrawingContext dc,
        Point center,
        double radius,
        double startDeg,
        double sweepDeg,
        Brush brush,
        double thickness)
    {
        if (Math.Abs(sweepDeg) < 0.5)
            return;

        var startRad = (startDeg - 90) * Math.PI / 180.0;
        var endRad = (startDeg + sweepDeg - 90) * Math.PI / 180.0;
        var start = new Point(
            center.X + radius * Math.Cos(startRad),
            center.Y + radius * Math.Sin(startRad));
        var end = new Point(
            center.X + radius * Math.Cos(endRad),
            center.Y + radius * Math.Sin(endRad));

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(
                end,
                new System.Windows.Size(radius, radius),
                0,
                Math.Abs(sweepDeg) > 180,
                sweepDeg > 0 ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                true,
                false);
        }

        geo.Freeze();
        dc.DrawGeometry(null, new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
        }, geo);
    }

    private void DrawBadge(DrawingContext dc, Point anchor, string text, Brush brush, bool alignRight = true)
    {
        var ft = Format(text, 10 * _fontScale, brush, AetherTheme.Micro, TextAlignment.Left);
        var width = ft.Width + 14;
        var height = ft.Height + 8;
        var x = alignRight ? anchor.X - width : anchor.X;
        var rect = new Rect(x, anchor.Y - height * 0.5, width, height);
        dc.DrawRoundedRectangle(AetherTheme.GlassFillStrong, new Pen(brush, 1), rect, 6, 6);
        dc.DrawText(ft, new Point(rect.X + 7, rect.Y + 4));
    }

    private static void DrawGlassCard(DrawingContext dc, Rect rect)
    {
        dc.DrawRoundedRectangle(AetherTheme.GlassFill, AetherTheme.GlassStrokePen, rect, 14, 14);
        // Top edge highlight
        var highlight = new Pen(AetherTheme.GlassStrokeSoft, 1);
        dc.DrawLine(highlight,
            new Point(rect.X + 16, rect.Y + 1),
            new Point(rect.Right - 16, rect.Y + 1));
    }

    private static string? PathBadge(AetherPath path)
    {
        if (path.PathErrorDeg is null || path.TargetAngleDeg is null)
            return null;
        if (path.PathTone is AetherTone.Good or AetherTone.Quiet)
            return path.PathTone == AetherTone.Good ? "ON PATH" : null;
        return path.PathErrorDeg > 0 ? "HIGH" : "LOW";
    }

    private static string? DescentBadge(AetherPath path)
    {
        if (path.DescentErrorDeg is null || path.TargetAngleDeg is null)
            return null;
        if (path.DescentTone is AetherTone.Good or AetherTone.Quiet)
            return path.DescentTone == AetherTone.Good ? "ON ANGLE" : null;
        return path.DescentErrorDeg > 0 ? "STEEP" : "SHALLOW";
    }

    private void DrawText(
        DrawingContext dc,
        string text,
        double size,
        Brush brush,
        double x,
        double y,
        TextAlignment align,
        Typeface typeface)
    {
        var scaledSize = size * _fontScale;
        var body = Format(text, scaledSize, brush, typeface, align);
        var shadow = Format(text, scaledSize, AetherTheme.Shadow, typeface, align);
        dc.DrawText(shadow, new Point(x + 1.2, y + 1.2));
        dc.DrawText(body, new Point(x, y));
    }

    private static FormattedText Format(
        string text,
        double size,
        Brush brush,
        Typeface typeface,
        TextAlignment align) =>
        new(
            text,
            CultureInfo.InvariantCulture,
            WpfFlowDirection.LeftToRight,
            typeface,
            size,
            brush,
            1.0)
        {
            TextAlignment = align,
        };

    private static double Lerp(double from, double to, double t) => from + (to - from) * t;

    private static double LerpAngle(double from, double to, double t)
    {
        var delta = NormalizeSigned(to - from);
        return from + delta * t;
    }

    private static double NormalizeSigned(double degrees)
    {
        var n = (degrees + 180.0) % 360.0;
        if (n < 0)
            n += 360.0;
        return n - 180.0;
    }
}
