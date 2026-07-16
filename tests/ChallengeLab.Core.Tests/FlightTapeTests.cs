using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class FlightTapeTests
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

    private static (ChallengeConfig Challenge, LandingEvaluationKey Key, LandingSessionSettings Settings) Load()
    {
        var loader = new ConfigLoader(FindConfig());
        var keyResult = loader.LoadEvaluationKey();
        Assert.True(keyResult.IsValid, string.Join("; ", keyResult.Errors));
        var challenge = loader.LoadChallenge("challenges/barcelona-crosswind-final.json");
        return (challenge, keyResult.Key!, keyResult.Key!.ToSessionSettings());
    }

    [Fact]
    public void SaveLoad_RoundTripsSamplesAndChallenge()
    {
        var (challenge, _, _) = Load();
        using var dir = new TempDir();
        var store = new FlightTapeStore(dir.Path);

        var samples = BuildSettlingLanding(challenge, DateTimeOffset.UtcNow);
        var result = FakeResult(challenge, score: 88.5);

        var path = store.Save(challenge, samples, result, attemptOrigin: "DefaultChallenge");
        Assert.True(File.Exists(path));

        var loaded = store.Load(path);
        Assert.Equal(FlightTapeStore.FormatId, loaded.Format);
        Assert.Equal(challenge.Id, loaded.ChallengeId);
        Assert.Equal(challenge.Title, loaded.Challenge!.Title);
        Assert.Equal(challenge.Runway.ThresholdLatitude, loaded.Challenge.Runway.ThresholdLatitude);
        Assert.Equal(samples.Count, loaded.Samples.Count);
        Assert.Equal(samples[0].Latitude, loaded.Samples[0].Latitude, 6);
        Assert.Equal(samples[^1].GroundSpeedKts, loaded.Samples[^1].GroundSpeedKts, 3);
        Assert.Equal(2, loaded.Samples[40].EngineCount);
        Assert.True(loaded.Samples[40].EngineCombustionByIndex![1]);
        Assert.True(loaded.Samples[40].ReverseThrustEngagedByIndex![2]);
        Assert.True(loaded.Samples[40].ReverseNozzlePositionByIndex![1] >= 0.01);
        Assert.Equal(0, loaded.Samples[40].ThrottleLeverPositionPercentByIndex![2]);
        Assert.Equal(88.5, loaded.OriginalScorePercent);
        Assert.Equal("A", loaded.OriginalGrade);

        var listed = store.List();
        Assert.Single(listed);
        Assert.Equal(path, listed[0].Path);
        Assert.Contains(challenge.Title, listed[0].DisplayName);
    }

    [Fact]
    public void Recorder_BuffersUntilFinish()
    {
        var (challenge, _, _) = Load();
        var recorder = new FlightTapeRecorder();
        Assert.False(recorder.IsActive);

        recorder.Start(challenge, "FreeFlight");
        Assert.True(recorder.IsActive);
        recorder.Add(Sample(challenge, DateTimeOffset.UtcNow, simT: 1, onGround: false, agl: 500, gs: 140));
        recorder.Add(Sample(challenge, DateTimeOffset.UtcNow.AddSeconds(0.2), simT: 1.2, onGround: false, agl: 400, gs: 140));
        Assert.Equal(2, recorder.SampleCount);

        var finished = recorder.Finish();
        Assert.NotNull(finished);
        Assert.Equal(2, finished!.Value.Samples.Count);
        Assert.Equal("FreeFlight", finished.Value.AttemptOrigin);
        Assert.False(recorder.IsActive);
        Assert.Equal(0, recorder.SampleCount);
    }

    [Fact]
    public void Replayer_SettledLanding_ProducesDeterministicScore()
    {
        var (challenge, key, _) = Load();
        var t0 = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = BuildSettlingLanding(challenge, t0);

        var tape = new FlightTapeDocument
        {
            Challenge = challenge,
            ChallengeId = challenge.Id,
            ChallengeTitle = challenge.Title,
            Samples = samples,
            SampleCount = samples.Count,
            Utc = t0
        };

        var first = FlightTapeReplayer.Replay(tape, key);
        var second = FlightTapeReplayer.Replay(tape, key);

        Assert.True(first.Session.IsComplete || first.Session.Snapshot.Touchdown is not null);
        Assert.Equal(first.Result.ScorePercent, second.Result.ScorePercent);
        Assert.Equal(first.Result.Grade, second.Result.Grade);
        Assert.Equal(first.Result.Criteria.Count, second.Result.Criteria.Count);
        Assert.Equal(MetricStatus.Informational,
            first.Result.Criteria.Single(c => c.Id == "reverse_thrust").Status);
        Assert.True(first.Session.Snapshot.ApproachSamples.Count > 0);
        Assert.NotNull(first.Session.Snapshot.Touchdown);
    }

    [Fact]
    public void Replayer_OldTapeWithoutReverseTelemetry_IsUnrankedUnderV21()
    {
        var (challenge, key, _) = Load();
        var t0 = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        var samples = BuildSettlingLanding(challenge, t0, includeReverseTelemetry: false);

        var replay = FlightTapeReplayer.Replay(
            new FlightTapeDocument
            {
                Challenge = challenge,
                ChallengeId = challenge.Id,
                ChallengeTitle = challenge.Title,
                Samples = samples,
                SampleCount = samples.Count,
                Utc = t0
            },
            key);

        Assert.False(replay.Result.IsRanked);
        Assert.Equal(MetricStatus.Unavailable,
            replay.Result.Criteria.Single(c => c.Id == "reverse_thrust").Status);
    }

    [Fact]
    public void SaveThenReplay_MatchesDirectReplay()
    {
        var (challenge, key, _) = Load();
        using var dir = new TempDir();
        var store = new FlightTapeStore(dir.Path);
        var samples = BuildSettlingLanding(challenge, DateTimeOffset.UtcNow);
        var liveResult = FakeResult(challenge, score: 70);

        var path = store.Save(challenge, samples, liveResult);
        var loaded = store.Load(path);

        var direct = FlightTapeReplayer.Replay(
            new FlightTapeDocument
            {
                Challenge = challenge,
                Samples = samples.ToList()
            },
            key);
        var fromDisk = FlightTapeReplayer.Replay(loaded, key);

        Assert.Equal(direct.Result.ScorePercent, fromDisk.Result.ScorePercent);
        Assert.Equal(direct.Result.IsRanked, fromDisk.Result.IsRanked);
        Assert.Equal(70, loaded.OriginalScorePercent);
    }

    private static ScoreResult FakeResult(ChallengeConfig challenge, double score) => new()
    {
        ChallengeId = challenge.Id,
        ChallengeTitle = challenge.Title,
        ScorePercent = score,
        Grade = ScoreResult.GradeFromPercent(score),
        IsRanked = true,
        ScoredAtUtc = DateTimeOffset.UtcNow,
        EvaluationKeyId = "test",
        EvaluationKeyVersion = 1,
        ScoringProfileHash = "abc"
    };

    /// <summary>
    /// Synthetic approach → touchdown → rollout that settles under the configured GS threshold.
    /// Uses SimulationTimeSeconds so replay is pause-aware and deterministic.
    /// </summary>
    private static List<TelemetrySample> BuildSettlingLanding(
        ChallengeConfig challenge,
        DateTimeOffset t0,
        bool includeReverseTelemetry = true)
    {
        var rwy = challenge.Runway;
        var hdg = rwy.HeadingTrueDeg;
        var elev = rwy.ElevationFeet;
        var samples = new List<TelemetrySample>();
        double simT = 10;

        // Airborne history past post-arm grace / min airborne samples.
        for (var i = 0; i < 40; i++)
        {
            var nm = 2.0 - i * 0.04;
            var (lat, lon) = PointAlongFinal(rwy, nm);
            var alt = RunwayPathGeometry.ExpectedAltitudeFeet(Math.Max(nm, 0.05), elev);
            samples.Add(Sample(
                challenge, t0.AddSeconds(simT - 10), simT,
                onGround: false, agl: Math.Max(alt - elev, 80),
                gs: 145, ias: 145, lat: lat, lon: lon, alt: alt, vs: -700,
                includeReverseTelemetry: includeReverseTelemetry));
            simT += 0.2;
        }

        // Touchdown at threshold.
        samples.Add(Sample(
            challenge, t0.AddSeconds(simT - 10), simT,
            onGround: true, agl: 0, gs: 130, ias: 138,
            lat: rwy.ThresholdLatitude, lon: rwy.ThresholdLongitude, alt: elev, vs: -140,
            g: 1.15, spoilers: 0.4, brake: 0.2, reverseSelected: true,
            includeReverseTelemetry: includeReverseTelemetry));
        simT += 0.2;

        // Rollout then settle well below settle GS for several seconds.
        for (var i = 1; i <= 40; i++)
        {
            var gs = i < 15 ? 90 - i * 2 : 25;
            var alongM = i * 25.0;
            var (lat, lon) = PointAlongRunway(rwy, alongM);
            samples.Add(Sample(
                challenge, t0.AddSeconds(simT - 10), simT,
                onGround: true, agl: 0, gs: gs, ias: gs,
                lat: lat, lon: lon, alt: elev, vs: 0,
                g: 1.0, spoilers: 0.8, brake: 0.5, reverseSelected: gs > 60,
                includeReverseTelemetry: includeReverseTelemetry));
            simT += 0.25;
        }

        return samples;
    }

    private static (double Lat, double Lon) PointAlongFinal(RunwayConfig rwy, double distanceNm)
    {
        var h = rwy.HeadingTrueDeg * Math.PI / 180.0;
        var m = distanceNm * 1852.0;
        var dLat = (m * Math.Cos(h)) / 111320.0;
        var dLon = (m * Math.Sin(h)) / (111320.0 * Math.Cos(rwy.ThresholdLatitude * Math.PI / 180.0));
        return (rwy.ThresholdLatitude - dLat, rwy.ThresholdLongitude - dLon);
    }

    private static (double Lat, double Lon) PointAlongRunway(RunwayConfig rwy, double distanceMeters)
    {
        var h = rwy.HeadingTrueDeg * Math.PI / 180.0;
        var dLat = (distanceMeters * Math.Cos(h)) / 111320.0;
        var dLon = (distanceMeters * Math.Sin(h)) /
                   (111320.0 * Math.Cos(rwy.ThresholdLatitude * Math.PI / 180.0));
        return (rwy.ThresholdLatitude + dLat, rwy.ThresholdLongitude + dLon);
    }

    private static TelemetrySample Sample(
        ChallengeConfig challenge,
        DateTimeOffset timestamp,
        double simT,
        bool onGround,
        double agl,
        double gs,
        double ias = 140,
        double? lat = null,
        double? lon = null,
        double? alt = null,
        double vs = -200,
        double g = 1.0,
        double spoilers = 0,
        double brake = 0,
        bool reverseSelected = false,
        bool includeReverseTelemetry = true)
    {
        var rwy = challenge.Runway;
        return new TelemetrySample
        {
            Timestamp = timestamp,
            SimulationTimeSeconds = simT,
            SimOnGround = onGround,
            Latitude = lat ?? rwy.ThresholdLatitude,
            Longitude = lon ?? rwy.ThresholdLongitude,
            AltitudeFeet = alt ?? (rwy.ElevationFeet + agl),
            AglFeet = agl,
            RadioHeightFeet = agl,
            RadioHeightAvailable = true,
            HeadingTrueDeg = rwy.HeadingTrueDeg,
            GroundTrackTrueDeg = rwy.HeadingTrueDeg,
            PitchDeg = onGround ? 0 : 2,
            BankDeg = 0,
            GroundSpeedKts = gs,
            AirspeedKts = ias,
            VerticalSpeedFpm = vs,
            GForce = g,
            GForceAvailable = true,
            GearHandlePosition = 1,
            IsGearWheels = true,
            FlapsHandleIndex = 3,
            SpoilersLeftPosition = spoilers,
            SpoilersRightPosition = spoilers,
            SpoilersSurfacePosition = spoilers,
            ManualBrakeLeftPosition = brake,
            ManualBrakeRightPosition = brake,
            SimulationRate = 1.0,
            PauseStateAvailable = true,
            EngineCount = includeReverseTelemetry ? 2 : null,
            EngineCombustionByIndex = includeReverseTelemetry
                ? new Dictionary<int, bool> { [1] = true, [2] = true }
                : null,
            ReverseThrustEngagedByIndex = includeReverseTelemetry
                ? new Dictionary<int, bool> { [1] = reverseSelected, [2] = reverseSelected }
                : null,
            ReverseNozzlePositionByIndex = includeReverseTelemetry
                ? new Dictionary<int, double> { [1] = reverseSelected ? 0.2 : 0, [2] = reverseSelected ? 0.2 : 0 }
                : null,
            ThrottleLeverPositionPercentByIndex = includeReverseTelemetry
                ? new Dictionary<int, double> { [1] = 0, [2] = 0 }
                : null,
            GearOnGroundByIndex = onGround
                ? new Dictionary<int, bool> { [0] = true, [1] = true, [2] = true }
                : new Dictionary<int, bool> { [0] = false, [1] = false, [2] = false }
        };
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "challenge-lab-tape-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // best effort
            }
        }
    }
}
