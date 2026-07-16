namespace ChallengeLab.Core.Scoring;

/// <summary>
/// Picks a nominal approach path angle from facility data (VASI/PAPI) with a 3° fallback.
/// Challenge mode does not use this — challenges always set <c>RunwayConfig.GlideslopeDeg</c>.
/// </summary>
public static class GlideslopeAngleResolver
{
    public const double DefaultDegrees = RunwayPathGeometry.DefaultGlideslopeDeg;
    public const string SourceDefault = "default";
    public const string SourceVasi = "vasi";
    public const string SourceChallenge = "challenge";

    public readonly record struct Resolution(double Degrees, string Source);

    /// <summary>
    /// Prefer the first valid VASI/PAPI angle; otherwise default 3°.
    /// </summary>
    public static Resolution Resolve(IEnumerable<double?>? candidateAnglesDeg)
    {
        if (candidateAnglesDeg is not null)
        {
            foreach (var candidate in candidateAnglesDeg)
            {
                if (candidate is not { } angle || !double.IsFinite(angle))
                    continue;
                if (angle < 1.5 || angle > 10.0)
                    continue;
                // Ignore near-zero / unset scenery junk
                if (angle < 0.5)
                    continue;
                return new Resolution(
                    Math.Round(RunwayPathGeometry.SanitizeGlideslopeDeg(angle), 2),
                    SourceVasi);
            }
        }

        return new Resolution(DefaultDegrees, SourceDefault);
    }

    /// <summary>
    /// Primary/secondary end helpers: prefer left then right VASI for that end.
    /// </summary>
    public static Resolution ResolveEnd(double? leftVasiDeg, double? rightVasiDeg) =>
        Resolve(new double?[] { leftVasiDeg, rightVasiDeg });
}
