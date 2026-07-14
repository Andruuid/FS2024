namespace ChallengeLab.Core.Scoring.Evaluators;

/// <summary>
/// Lateral offset from runway centerline at main-gear touchdown.
/// Full score within tolerance, then power-curve falloff to zero.
/// </summary>
public static class CenterlineScore
{
    /// <summary>
    /// Score in [0, 100] for absolute centerline deviation in metres.
    /// </summary>
    /// <param name="centerlineDeviationMeters">Signed or absolute lateral offset (m).</param>
    /// <param name="fullScoreToleranceMeters">t — full score if |d| ≤ t.</param>
    /// <param name="zeroScoreDeviationMeters">z — score is 0 if |d| ≥ z.</param>
    /// <param name="exponent">p — penalty curve exponent (1.5 recommended).</param>
    public static double Calculate(
        double centerlineDeviationMeters,
        double fullScoreToleranceMeters = 3.0,
        double zeroScoreDeviationMeters = 15.0,
        double exponent = 1.5)
    {
        var deviation = Math.Abs(centerlineDeviationMeters);
        var t = Math.Max(0, fullScoreToleranceMeters);
        var z = zeroScoreDeviationMeters;

        if (z <= t)
            z = t + 0.001;

        if (deviation <= t)
            return 100.0;

        if (deviation >= z)
            return 0.0;

        var normalizedDeviation = (deviation - t) / (z - t);
        var score = 100.0 * (1.0 - Math.Pow(normalizedDeviation, exponent));
        return Math.Clamp(score, 0.0, 100.0);
    }

    /// <summary>Score in [0, 1] for evaluator use.</summary>
    public static double Calculate01(
        double centerlineDeviationMeters,
        double fullScoreToleranceMeters = 3.0,
        double zeroScoreDeviationMeters = 15.0,
        double exponent = 1.5)
        => Calculate(centerlineDeviationMeters, fullScoreToleranceMeters, zeroScoreDeviationMeters, exponent) / 100.0;
}
