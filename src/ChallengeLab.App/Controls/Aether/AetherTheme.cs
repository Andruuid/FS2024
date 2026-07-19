using System.Windows;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Pen = System.Windows.Media.Pen;

namespace ChallengeLab.App.Controls.Aether;

/// <summary>Frozen visual tokens for the Aether overlay.</summary>
internal static class AetherTheme
{
    public static readonly Typeface Display = new(
        new FontFamily("Segoe UI Variable Display, Segoe UI"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    public static readonly Typeface Body = new(
        new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontStyles.Normal,
        FontWeights.Medium,
        FontStretches.Normal);

    public static readonly Typeface Mono = new(
        new FontFamily("Cascadia Code, Consolas"),
        FontStyles.Normal,
        FontWeights.SemiBold,
        FontStretches.Normal);

    public static readonly Typeface Micro = new(
        new FontFamily("Segoe UI"),
        FontStyles.Normal,
        FontWeights.Bold,
        FontStretches.Normal);

    public static readonly Brush GlassFill = FreezeBrush("#66101828");
    public static readonly Brush GlassFillStrong = FreezeBrush("#99101828");
    public static readonly Brush GlassStroke = FreezeBrush("#55A78BFF");
    public static readonly Brush GlassStrokeSoft = FreezeBrush("#3388C8FF");
    public static readonly Brush TextPrimary = FreezeBrush("#F4F7FF");
    public static readonly Brush TextMuted = FreezeBrush("#9BB0D0");
    public static readonly Brush TextDim = FreezeBrush("#6A7F9E");
    public static readonly Brush Cyan = FreezeBrush("#5CE1FF");
    public static readonly Brush Violet = FreezeBrush("#B388FF");
    public static readonly Brush Magenta = FreezeBrush("#FF6BCB");
    public static readonly Brush Good = FreezeBrush("#3DFFB0");
    public static readonly Brush Caution = FreezeBrush("#FFC857");
    public static readonly Brush Alert = FreezeBrush("#FF5D7A");
    public static readonly Brush Shadow = FreezeBrush("#C0000000");
    public static readonly Brush HaloGood = FreezeBrush("#223DFFB0");
    public static readonly Brush HaloCaution = FreezeBrush("#28FFC857");
    public static readonly Brush HaloAlert = FreezeBrush("#30FF5D7A");
    public static readonly Brush Track = FreezeBrush("#33FFFFFF");
    public static readonly Brush Needle = FreezeBrush("#F8FBFF");
    public static readonly Brush SparkFill = FreezeBrush("#665CE1FF");

    public static readonly Pen GlassStrokePen = FreezePen(GlassStroke, 1.25);
    public static readonly Pen GlassStrokeSoftPen = FreezePen(GlassStrokeSoft, 1.0);
    public static readonly Pen TrackPen = FreezePen(Track, 2.0);
    public static readonly Pen SparkPen = FreezePen(Cyan, 1.6);

    public static Brush ToneBrush(AetherTone tone) => tone switch
    {
        AetherTone.Good => Good,
        AetherTone.Caution => Caution,
        AetherTone.Alert => Alert,
        _ => TextPrimary,
    };

    public static readonly Brush Transparent = FreezeBrush("#00000000");

    public static Brush HaloBrush(AetherTone tone) => tone switch
    {
        AetherTone.Good => HaloGood,
        AetherTone.Caution => HaloCaution,
        AetherTone.Alert => HaloAlert,
        _ => Transparent,
    };

    public static AetherTone Worst(params AetherTone[] tones)
    {
        var worst = AetherTone.Quiet;
        foreach (var tone in tones)
        {
            if ((int)tone > (int)worst)
                worst = tone;
        }

        return worst;
    }

    private static Brush FreezeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return brush;
    }

    private static Pen FreezePen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        pen.Freeze();
        return pen;
    }
}
