namespace ChallengeLab.Core.Scoring;

/// <summary>Asymmetric aiming-marker-relative main-gear touchdown scorer.</summary>
public static class LandingScorer
{
    public static double Score(
        double aimingMarkerFt,
        double touchdownFt,
        double idealNearOffsetFt = 300.0,
        double idealFarOffsetFt = 500.0,
        double shortSpanFt = 800.0,
        double longSpanFt = 500.0)
    {
        Validate(aimingMarkerFt, touchdownFt, idealNearOffsetFt, idealFarOffsetFt, shortSpanFt, longSpanFt);

        var idealNear = aimingMarkerFt + idealNearOffsetFt;
        var idealFar = aimingMarkerFt + idealFarOffsetFt;
        var zeroShort = idealNear - shortSpanFt;
        var zeroLong = idealFar + longSpanFt;

        double fraction;
        if (touchdownFt < idealNear)
            fraction = (touchdownFt - zeroShort) / shortSpanFt;
        else if (touchdownFt > idealFar)
            fraction = (zeroLong - touchdownFt) / longSpanFt;
        else
            fraction = 1.0;

        return Math.Round(Math.Clamp(fraction, 0.0, 1.0) * 100.0, 1);
    }

    private static void Validate(
        double aimingMarkerFt,
        double touchdownFt,
        double idealNearOffsetFt,
        double idealFarOffsetFt,
        double shortSpanFt,
        double longSpanFt)
    {
        if (!double.IsFinite(aimingMarkerFt) || aimingMarkerFt < 0)
            throw new ArgumentOutOfRangeException(nameof(aimingMarkerFt));
        if (!double.IsFinite(touchdownFt)) throw new ArgumentOutOfRangeException(nameof(touchdownFt));
        if (!double.IsFinite(idealNearOffsetFt) || idealNearOffsetFt < 0)
            throw new ArgumentOutOfRangeException(nameof(idealNearOffsetFt));
        if (!double.IsFinite(idealFarOffsetFt) || idealFarOffsetFt <= idealNearOffsetFt)
            throw new ArgumentOutOfRangeException(nameof(idealFarOffsetFt));
        if (!double.IsFinite(shortSpanFt) || shortSpanFt <= 0)
            throw new ArgumentOutOfRangeException(nameof(shortSpanFt));
        if (!double.IsFinite(longSpanFt) || longSpanFt <= 0)
            throw new ArgumentOutOfRangeException(nameof(longSpanFt));
    }
}
