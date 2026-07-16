using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Scoring;

public sealed record FlightTapeReplayResult(
    FlightTapeDocument Tape,
    LandingSession Session,
    ScoreResult Result,
    LandingSessionSettings SessionSettings,
    string ProfileHash);

/// <summary>
/// Offline re-evaluation: feed a saved flight tape through <see cref="LandingSession"/>
/// and score with the supplied evaluation key (typically the current live key).
/// </summary>
public static class FlightTapeReplayer
{
    public static FlightTapeReplayResult Replay(
        FlightTapeDocument tape,
        LandingEvaluationKey baseKey)
    {
        ArgumentNullException.ThrowIfNull(tape);
        ArgumentNullException.ThrowIfNull(baseKey);
        if (tape.Challenge is null)
            throw new InvalidOperationException("Flight tape has no embedded challenge.");
        if (tape.Samples is null || tape.Samples.Count == 0)
            throw new InvalidOperationException("Flight tape has no samples.");

        var challenge = tape.Challenge;
        var effective = EffectiveEvaluationProfileBuilder.Build(baseKey, challenge);
        var settings = effective.Key.ToSessionSettings();
        var engine = new ScoreEngine(effective.Key, effective.ProfileHash);

        var session = new LandingSession(challenge, settings);
        session.Arm();

        foreach (var sample in OrderSamples(tape.Samples))
            session.Ingest(sample);

        if (!session.IsComplete && session.Phase is not LandingPhase.Settled and not LandingPhase.Scored)
        {
            // Tape may end just before settle hold completed; still score whatever was captured
            // if touchdown happened (manual finalize path for incomplete settle).
            if (session.Snapshot.Touchdown is not null)
                session.RefreshDerivedMetrics(finalizing: true);
        }

        var result = engine.Evaluate(challenge, session.Snapshot);
        return new FlightTapeReplayResult(tape, session, result, settings, effective.ProfileHash);
    }

    public static IEnumerable<TelemetrySample> OrderSamples(IEnumerable<TelemetrySample> samples) =>
        samples
            .Select((s, index) => (Sample: s, Index: index))
            .OrderBy(x => ResolveSortKey(x.Sample))
            .ThenBy(x => x.Index)
            .Select(x => x.Sample);

    private static double ResolveSortKey(TelemetrySample sample)
    {
        if (double.IsFinite(sample.SimulationTimeSeconds))
            return sample.SimulationTimeSeconds;
        return sample.Timestamp.ToUnixTimeMilliseconds() / 1000.0;
    }
}
