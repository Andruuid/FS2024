using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class LandingSessionTests
{
    private static string FindConfig()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "catalog.json")))
                return Path.Combine(dir.FullName, "config");
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("config not found");
    }

    private static (ChallengeConfig Challenge, LandingSessionSettings Settings) Load()
    {
        var loader = new ConfigLoader(FindConfig());
        var keyResult = loader.LoadEvaluationKey();
        Assert.True(keyResult.IsValid, string.Join("; ", keyResult.Errors));
        var challenge = loader.LoadChallenge("challenges/barcelona-crosswind-final.json");
        return (challenge, keyResult.Key!.ToSessionSettings());
    }

    private static TelemetrySample Sample(
        DateTimeOffset t,
        bool onGround,
        double agl = 2000,
        double vs = -200,
        double lat = 43.40,
        double lon = 5.25,
        double gs = 140,
        double ias = 145)
        => new()
        {
            Timestamp = t,
            SimOnGround = onGround,
            AglFeet = agl,
            RadioHeightFeet = agl,
            VerticalSpeedFpm = vs,
            Latitude = lat,
            Longitude = lon,
            AltitudeFeet = 43 + agl,
            GroundSpeedKts = gs,
            AirspeedKts = ias,
            HeadingTrueDeg = 313,
            GroundTrackTrueDeg = 313,
            PitchDeg = 2,
            BankDeg = 0,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            GForce = 1.1
        };

    [Fact]
    public void ArmOnGround_DoesNotCaptureTouchdownDuringGraceOrWhileGrounded()
    {
        var (challenge, settings) = Load();
        Assert.True(settings.PostArmIgnoreSeconds >= 1);
        Assert.True(settings.RequireAirborneBeforeTouchdown);

        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        // Stream grounded samples well past the post-arm grace window.
        for (var i = 0; i < 60; i++)
        {
            session.Ingest(Sample(t0.AddSeconds(i * 0.2), onGround: true, agl: 0, gs: 20));
        }

        Assert.Null(session.Snapshot.Touchdown);
        Assert.False(session.IsComplete);
        Assert.NotEqual(LandingPhase.Scored, session.Phase);
    }

    [Fact]
    public void RealApproachThenTouchdown_CapturesAfterAirborneHistory()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        // Stay past grace in the air with enough airborne samples.
        var t = t0.AddSeconds(settings.PostArmIgnoreSeconds + 0.5);
        for (var i = 0; i < settings.MinAirborneSamples + 5; i++)
        {
            session.Ingest(Sample(t.AddMilliseconds(i * 100), onGround: false, agl: 500 - i * 20, vs: -400));
        }

        // Rising edge to ground.
        var tdTime = t.AddSeconds(2);
        session.Ingest(Sample(tdTime, onGround: true, agl: 0, vs: -150, gs: 120, ias: 140));

        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.Equal(-150, session.Snapshot.VerticalSpeedAtTouchdownFpm, 3);
        Assert.Equal(LandingPhase.Rollout, session.Phase);
    }

    [Fact]
    public void GroundEdgeBeforeAirborneGate_IsIgnored_ThenRealTdWorks()
    {
        var (challenge, settings) = Load();
        // Force strict airborne gate
        settings = settings with
        {
            PostArmIgnoreSeconds = 0.5,
            RequireAirborneBeforeTouchdown = true,
            MinAirborneSamples = 5,
            MinAirborneAglFeet = 80
        };

        var session = new LandingSession(challenge, settings);
        var t0 = DateTimeOffset.UtcNow;
        session.Arm();

        // After grace, still grounded (restart-on-runway) — no TD.
        session.Ingest(Sample(t0.AddSeconds(1.0), onGround: true, agl: 0, gs: 10));
        session.Ingest(Sample(t0.AddSeconds(1.2), onGround: true, agl: 0, gs: 10));
        Assert.Null(session.Snapshot.Touchdown);

        // Get airborne (restart succeeded later / fly out).
        for (var i = 0; i < 8; i++)
            session.Ingest(Sample(t0.AddSeconds(2 + i * 0.15), onGround: false, agl: 300, vs: -100));

        // Real touchdown edge.
        session.Ingest(Sample(t0.AddSeconds(4), onGround: true, agl: 0, vs: -180, gs: 130));
        Assert.NotNull(session.Snapshot.Touchdown);
        Assert.Equal(-180, session.Snapshot.VerticalSpeedAtTouchdownFpm, 3);
    }
}
