using ChallengeLab.Core.Career;
using ChallengeLab.Core.Config;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;

namespace ChallengeLab.Core.Tests;

public sealed class CareerModeTests
{
    [Fact]
    public void ShippedCareerConfig_IsValidAndHasExpectedLadder()
    {
        var loader = new ConfigLoader(FindConfig());
        var catalog = loader.LoadCatalog();
        var challenges = loader.LoadAllChallenges(catalog);

        var validation = CareerConfigValidationResult.Validate(catalog.Career, challenges);

        Assert.True(validation.IsValid, string.Join(" | ", validation.Errors));
        Assert.Equal(80, catalog.Career!.PassScorePercent);
        Assert.Equal(3, catalog.Career.AssignmentChallengeIds.Count);
        Assert.Equal(5, catalog.Career.Ranks.Count);
        Assert.Equal(5, catalog.Career.Ranks.Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(5, catalog.Career.Ranks.Select(r => r.RewardChallengeId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(catalog.Career.AssignmentChallengeIds,
            id => Assert.True(challenges.Single(c => c.Id == id).Available));
        Assert.All(catalog.Career.Ranks,
            rank => Assert.False(challenges.Single(c => c.Id == rank.RewardChallengeId).Available));
    }

    [Fact]
    public void Validation_RejectsBrokenCareerWithoutAffectingChallengeLoading()
    {
        var loader = new ConfigLoader(FindConfig());
        var challenges = loader.LoadAllChallenges();
        var config = BuildConfig();
        config.PassScorePercent = double.NaN;
        config.AssignmentChallengeIds[2] = config.AssignmentChallengeIds[0];
        config.Ranks[1].Id = config.Ranks[0].Id;
        config.Ranks[2].RewardChallengeId = "missing";
        config.Ranks[3].RewardChallengeId = config.Ranks[0].RewardChallengeId;
        config.Ranks[4].RewardChallengeId = config.AssignmentChallengeIds[0];

        var result = CareerConfigValidationResult.Validate(config, challenges);

        Assert.False(result.IsValid);
        Assert.NotEmpty(result.Errors);
        Assert.Contains(challenges, c => c.Available);
    }

    [Fact]
    public void FreshState_AcceptsOnceAndRoundTripsRevealedMission()
    {
        using var files = new TemporaryCareerFiles();
        var random = new SequenceRandom(1, 2);
        var service = CreateService(files.Path, random);

        Assert.Equal("Cadet", service.CurrentRank!.Title);
        Assert.Equal(0, service.State.CompletedStageCount);
        Assert.Null(service.AcceptedAssignment);

        var accepted = service.AcceptAssignment();
        var acceptedAgain = service.AcceptAssignment();

        Assert.Same(accepted, acceptedAgain);
        Assert.Equal(1, random.CallCount);
        Assert.NotNull(service.State.AcceptedAtUtc);

        var reloaded = CreateService(files.Path, new SequenceRandom(0));
        Assert.Equal(accepted.Id, reloaded.AcceptedAssignment!.Id);
        Assert.Equal(accepted.Id, reloaded.AcceptAssignment().Id);
    }

    [Fact]
    public void Acceptance_AvoidsImmediatelyPreviousAssignment()
    {
        using var files = new TemporaryCareerFiles();
        var service = CreateService(files.Path, new SequenceRandom(0, 0));
        var first = service.AcceptAssignment();
        service.RecordAttempt(LandingAttemptOrigin.CareerAssignment, first.Id, true, 80);

        var second = service.AcceptAssignment();

        Assert.NotEqual(first.Id, second.Id);
    }

    [Fact]
    public void OnlyMatchingCareerAttemptsCount_AndThresholdIsInclusive()
    {
        using var files = new TemporaryCareerFiles();
        var service = CreateService(files.Path, new SequenceRandom(0));
        var assignment = service.AcceptAssignment();

        Assert.Null(service.RecordAttempt(LandingAttemptOrigin.DefaultChallenge, assignment.Id, true, 100));
        Assert.Null(service.RecordAttempt(LandingAttemptOrigin.FreeFlight, assignment.Id, true, 100));
        Assert.Null(service.RecordAttempt(LandingAttemptOrigin.CareerAssignment, "other", true, 100));
        Assert.Equal(0, service.State.AttemptCount);

        var low = service.RecordAttempt(LandingAttemptOrigin.CareerAssignment, assignment.Id, true, 79.9);
        Assert.Equal(CareerOutcomeKind.Retry, low!.Kind);
        Assert.Equal(0, service.State.CompletedStageCount);
        Assert.Equal(assignment.Id, service.AcceptedAssignment!.Id);

        var unranked = service.RecordAttempt(
            LandingAttemptOrigin.CareerAssignment,
            assignment.Id,
            false,
            null,
            "Impact telemetry degraded");
        Assert.Equal(CareerOutcomeKind.Unranked, unranked!.Kind);
        Assert.Contains("Impact telemetry", unranked.Message);
        Assert.Equal(assignment.Id, service.AcceptedAssignment!.Id);

        var passed = service.RecordAttempt(LandingAttemptOrigin.CareerAssignment, assignment.Id, true, 80.0);
        Assert.Equal(CareerOutcomeKind.Passed, passed!.Kind);
        Assert.Equal(1, service.State.CompletedStageCount);
        Assert.Null(service.AcceptedAssignment);
        Assert.Equal(3, service.State.AttemptCount);
    }

    [Fact]
    public void FivePasses_UnlockExactlyOneRewardEach_AndCompleteWithoutSixthMission()
    {
        using var files = new TemporaryCareerFiles();
        var service = CreateService(files.Path, new SequenceRandom(0, 0, 0, 0, 0));

        for (var stage = 0; stage < 5; stage++)
        {
            var assignment = service.AcceptAssignment();
            var outcome = service.RecordAttempt(
                LandingAttemptOrigin.CareerAssignment,
                assignment.Id,
                true,
                95);

            Assert.Equal(stage + 1, service.State.CompletedStageCount);
            Assert.Equal(stage + 1, service.UnlockedRanks.Count);
            Assert.Equal(stage == 4 ? CareerOutcomeKind.Complete : CareerOutcomeKind.Passed, outcome!.Kind);
        }

        Assert.True(service.IsComplete);
        Assert.Null(service.CurrentRank);
        Assert.Throws<InvalidOperationException>(() => service.AcceptAssignment());
    }

    [Fact]
    public void IdenticalRandomSequences_ProduceIdenticalAssignments()
    {
        using var files1 = new TemporaryCareerFiles();
        using var files2 = new TemporaryCareerFiles();
        var one = CreateService(files1.Path, new SequenceRandom(2, 1, 0));
        var two = CreateService(files2.Path, new SequenceRandom(2, 1, 0));

        var sequence1 = CompleteAndCollect(one, 3);
        var sequence2 = CompleteAndCollect(two, 3);

        Assert.Equal(sequence1, sequence2);
    }

    [Fact]
    public void CorruptAndObsoleteState_ArePreservedAndRecovered()
    {
        using var corruptFiles = new TemporaryCareerFiles();
        File.WriteAllText(corruptFiles.Path, "{ this is not json");
        var recovered = CreateService(corruptFiles.Path, new SequenceRandom(0));
        Assert.Equal(0, recovered.State.CompletedStageCount);
        Assert.Contains("corrupt", recovered.RecoveryMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(corruptFiles.Directory, "career.corrupt-*.json"));

        using var obsoleteFiles = new TemporaryCareerFiles();
        var store = new CareerProgressStore(obsoleteFiles.Path);
        store.Save(new CareerProgressState
        {
            AcceptedAssignmentId = "removed-challenge",
            AcceptedAtUtc = DateTimeOffset.UtcNow,
            CompletedStageCount = 2
        });
        var reset = CreateService(obsoleteFiles.Path, new SequenceRandom(0));
        Assert.Equal(0, reset.State.CompletedStageCount);
        Assert.Null(reset.AcceptedAssignment);
        Assert.Contains("incompatible", reset.RecoveryMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Directory.GetFiles(obsoleteFiles.Directory, "career.obsolete-*.json"));
    }

    [Fact]
    public void Highscores_RoundTripOptionalCareerMetadata_AndLegacyRowsRemainBlank()
    {
        using var files = new TemporaryCareerFiles();
        var store = new HighscoreStore(files.Path);
        store.Add(new ScoreResult
        {
            ChallengeId = "barcelona-crosswind-final",
            ChallengeTitle = "Barcelona",
            IsRanked = true,
            ScorePercent = 88.4,
            Grade = "A"
        }, 2, "first-officer", "First Officer");

        var json = File.ReadAllText(files.Path);
        Assert.Contains("CareerStageNumber", json, StringComparison.Ordinal);
        Assert.Contains("First Officer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("CareerDisplay", json, StringComparison.Ordinal);

        var reloaded = new HighscoreStore(files.Path);
        var entry = Assert.Single(reloaded.Entries);
        Assert.Equal(2, entry.CareerStageNumber);
        Assert.Equal("first-officer", entry.CareerRankId);
        Assert.Equal("Career 2 · First Officer", entry.CareerDisplay);

        File.WriteAllText(files.Path,
            "[{\"Utc\":\"2026-01-01T00:00:00Z\",\"ChallengeId\":\"legacy\",\"ChallengeTitle\":\"Legacy\",\"ScorePercent\":80,\"Grade\":\"B\"}]");
        var legacy = Assert.Single(new HighscoreStore(files.Path).Entries);
        Assert.Null(legacy.CareerStageNumber);
        Assert.Equal("", legacy.CareerDisplay);
    }

    private static List<string> CompleteAndCollect(CareerProgressionService service, int count)
    {
        var ids = new List<string>();
        for (var index = 0; index < count; index++)
        {
            var assignment = service.AcceptAssignment();
            ids.Add(assignment.Id);
            service.RecordAttempt(LandingAttemptOrigin.CareerAssignment, assignment.Id, true, 90);
        }
        return ids;
    }

    private static CareerProgressionService CreateService(string path, IRandomIndexProvider random)
    {
        var loader = new ConfigLoader(FindConfig());
        var catalog = loader.LoadCatalog();
        var challenges = loader.LoadAllChallenges(catalog);
        return new CareerProgressionService(catalog.Career!, challenges, new CareerProgressStore(path), random);
    }

    private static CareerConfig BuildConfig()
    {
        var loader = new ConfigLoader(FindConfig());
        return loader.LoadCatalog().Career!;
    }

    private static string FindConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "config", "catalog.json");
            if (File.Exists(candidate)) return Path.GetDirectoryName(candidate)!;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("config not found");
    }

    private sealed class SequenceRandom(params int[] values) : IRandomIndexProvider
    {
        private readonly Queue<int> _values = new(values);
        public int CallCount { get; private set; }

        public int Next(int exclusiveUpperBound)
        {
            CallCount++;
            return _values.Count == 0 ? 0 : _values.Dequeue() % exclusiveUpperBound;
        }
    }

    private sealed class TemporaryCareerFiles : IDisposable
    {
        public TemporaryCareerFiles()
        {
            Directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChallengeLabCareerTests", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "career.json");
        }

        public string Directory { get; }
        public string Path { get; }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch { /* best effort test cleanup */ }
        }
    }
}
