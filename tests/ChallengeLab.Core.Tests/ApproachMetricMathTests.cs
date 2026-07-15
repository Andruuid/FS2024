using ChallengeLab.Core.Config;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.Core.Tests;

public sealed class ApproachMetricMathTests
{
    private const double EarthRadiusMeters = 6_371_000.0;

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    public void MovingPerfectPath_IsStableAcrossIrregularVisualFrameRates(double rateHz)
    {
        var snapshot = CalculateApproach(
            rateHz,
            altitudeError: _ => 0,
            lateralOffset: _ => 0,
            irregularCadence: true);

        Assert.True(snapshot.ApproachPathSampleCount > 100);
        Assert.InRange(snapshot.ApproachMetricDurationSec, 19.9, 20.1);
        Assert.InRange(snapshot.ApproachGlideslopeMeanAbsFt, 0, 0.01);
        Assert.InRange(snapshot.ApproachPathRms, 0, 0.01);
        Assert.InRange(snapshot.ApproachVerticalVariationFtPerSec, 0, 0.001);
        Assert.InRange(snapshot.ApproachLateralWeaveIndex, 0, 0.000001);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(-200)]
    public void ConstantPathError_ProducesMaeWithoutPumping(double altitudeErrorFeet)
    {
        var snapshot = CalculateApproach(
            60,
            altitudeError: _ => altitudeErrorFeet,
            lateralOffset: _ => 0);

        Assert.InRange(snapshot.ApproachGlideslopeMeanAbsFt, 199.99, 200.01);
        Assert.InRange(snapshot.ApproachPathRms, 199.99, 200.01);
        Assert.InRange(snapshot.ApproachVerticalVariationFtPerSec, 0, 0.001);
    }

    [Fact]
    public void EqualTimeHighAndLow_DoesNotCancelGlideslopeMae()
    {
        var snapshot = CalculateApproach(
            60,
            altitudeError: progress => progress < 0.5 ? 200 : -200,
            lateralOffset: _ => 0);

        Assert.InRange(snapshot.ApproachGlideslopeMeanAbsFt, 190, 205);
        // One step change: total vertical variation is non-zero.
        Assert.True(snapshot.ApproachVerticalVariationFtPerSec > 5);
    }

    [Fact]
    public void VerticalTotalVariation_CountsOneWayCaptureAndPumping()
    {
        var monotonic = CalculateApproach(
            60,
            altitudeError: progress => 200 * (1 - progress),
            lateralOffset: _ => 0);
        var pumping = CalculateApproach(
            60,
            altitudeError: progress => 100 * Math.Sin(progress * 4 * Math.PI),
            lateralOffset: _ => 0);

        // 200 ft over ~20 s → ~10 ft/s total variation for a smooth capture.
        Assert.InRange(monotonic.ApproachVerticalVariationFtPerSec, 8, 12);
        Assert.True(pumping.ApproachVerticalVariationFtPerSec > 30,
            $"Expected pumping variation above 30 ft/s, got {pumping.ApproachVerticalVariationFtPerSec}");
        Assert.True(pumping.ApproachVerticalVariationFtPerSec > monotonic.ApproachVerticalVariationFtPerSec);
    }

    [Fact]
    public void LateralTotalVariation_CountsInterceptAndSTurns()
    {
        var parallel = CalculateApproach(
            60,
            altitudeError: _ => 0,
            lateralOffset: _ => 100);
        var intercept = CalculateApproach(
            60,
            altitudeError: _ => 0,
            lateralOffset: progress => 100 * (1 - progress));
        var weaving = CalculateApproach(
            60,
            altitudeError: _ => 0,
            lateralOffset: progress => 100 * Math.Sin(progress * 4 * Math.PI));

        Assert.InRange(parallel.ApproachLateralWeaveIndex, 0, 0.000001);
        // Closing 100 m offset over short final is real path change.
        Assert.True(intercept.ApproachLateralWeaveIndex > 0.01,
            $"Expected intercept weave above 0.01 m/m, got {intercept.ApproachLateralWeaveIndex}");
        Assert.True(weaving.ApproachLateralWeaveIndex > 0.10,
            $"Expected S-turn weave above 0.10 m/m, got {weaving.ApproachLateralWeaveIndex}");
        Assert.True(weaving.ApproachLateralWeaveIndex > intercept.ApproachLateralWeaveIndex);
    }

    [Fact]
    public void SignedApproachDistance_ExcludesPostThresholdAirborneSamples()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 11; i++)
        {
            session.Snapshot.ApproachSamples.Add(CreateApproachSample(
                challenge,
                start.AddSeconds(i * 0.2),
                approachDistanceNm: 1.0 - i * 0.05,
                lateralMeters: 0,
                altitudeErrorFeet: 20));
        }

        for (var i = 0; i < 11; i++)
        {
            session.Snapshot.ApproachSamples.Add(CreateApproachSample(
                challenge,
                start.AddSeconds(2.2 + i * 0.2),
                approachDistanceNm: -0.25 - i * 0.02,
                lateralMeters: 0,
                altitudeErrorFeet: 1_000));
        }

        session.RefreshDerivedMetrics();

        Assert.Equal(11, session.Snapshot.ApproachPathSampleCount);
        Assert.InRange(session.Snapshot.ApproachGlideslopeMeanAbsFt, 19.99, 20.01);
    }

    [Fact]
    public void GapsAndOutOfOrderSamples_AreSeparateSegments()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var start = DateTimeOffset.UtcNow;

        AddSegment(session, challenge, start, 0, 100, 1.5);
        AddSegment(session, challenge, start, 4, -100, 1.3); // gap over two seconds
        AddSegment(session, challenge, start, 3, 50, 1.1, durationSeconds: 0.5); // out of order

        session.RefreshDerivedMetrics();

        Assert.InRange(session.Snapshot.ApproachMetricDurationSec, 2.49, 2.51);
        Assert.InRange(session.Snapshot.ApproachGlideslopeMeanAbsFt, 89.9, 90.1);
        Assert.InRange(session.Snapshot.ApproachVerticalVariationFtPerSec, 0, 0.001);
    }

    [Fact]
    public void LivePreviewAndSettledFinal_UseIdenticalApproachMetrics()
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var start = DateTimeOffset.UtcNow;
        session.Arm();

        for (var i = 0; i <= 40; i++)
        {
            var progress = i / 40.0;
            session.Ingest(CreateApproachSample(
                challenge,
                start.AddSeconds(5 + i * 0.15),
                approachDistanceNm: 1.5 - progress,
                lateralMeters: 30 * Math.Sin(progress * 2 * Math.PI),
                altitudeErrorFeet: 50 * Math.Sin(progress * 2 * Math.PI)));
        }

        var live = CaptureMetrics(session.Snapshot);
        var touchdownTime = start.AddSeconds(12);
        session.Ingest(CreateGroundSample(challenge, touchdownTime, groundSpeedKts: 120));
        session.Ingest(CreateGroundSample(challenge, touchdownTime.AddSeconds(0.5), groundSpeedKts: 20));
        session.Ingest(CreateGroundSample(challenge, touchdownTime.AddSeconds(1.6), groundSpeedKts: 20));

        Assert.True(session.IsComplete);
        Assert.Equal(live, CaptureMetrics(session.Snapshot));
    }

    [Fact]
    public void MetricAvailabilityGuards_StillRequireDurationAndTrackDistance()
    {
        var (challenge, _) = Load();
        var snapshot = new LandingSnapshot
        {
            ApproachPathSampleCount = 2,
            ApproachMetricDurationSec = 0.49,
            ApproachLateralDistanceM = 9.99
        };

        Assert.False(MetricResolver.Resolve(
            "approachGlideslopeMeanAbsFt", snapshot, challenge).IsAvailable);
        Assert.False(MetricResolver.Resolve(
            "approachVerticalVariationFtPerSec", snapshot, challenge).IsAvailable);
        Assert.False(MetricResolver.Resolve(
            "approachLateralWeaveIndex", snapshot, challenge).IsAvailable);

        snapshot.ApproachMetricDurationSec = 0.5;
        snapshot.ApproachLateralDistanceM = 10;

        Assert.True(MetricResolver.Resolve(
            "approachGlideslopeMeanAbsFt", snapshot, challenge).IsAvailable);
        Assert.True(MetricResolver.Resolve(
            "approachVerticalVariationFtPerSec", snapshot, challenge).IsAvailable);
        Assert.True(MetricResolver.Resolve(
            "approachLateralWeaveIndex", snapshot, challenge).IsAvailable);
    }

    private static LandingSnapshot CalculateApproach(
        double rateHz,
        Func<double, double> altitudeError,
        Func<double, double> lateralOffset,
        bool irregularCadence = false)
    {
        var (challenge, settings) = Load();
        var session = new LandingSession(challenge, settings);
        var start = DateTimeOffset.UtcNow;
        const double durationSeconds = 20;
        const double startDistanceNm = 2.8;
        const double endDistanceNm = 0.3;

        double elapsed = 0;
        var sampleIndex = 0;
        while (elapsed < durationSeconds)
        {
            AddCalculatedSample(elapsed);
            var cadenceFactor = irregularCadence
                ? 1.0 + 0.2 * Math.Sin((sampleIndex + 1) * 1.7)
                : 1.0;
            elapsed += cadenceFactor / rateHz;
            sampleIndex++;
        }

        AddCalculatedSample(durationSeconds);
        session.RefreshDerivedMetrics();
        return session.Snapshot;

        void AddCalculatedSample(double seconds)
        {
            var progress = seconds / durationSeconds;
            var distanceNm = startDistanceNm + (endDistanceNm - startDistanceNm) * progress;
            session.Snapshot.ApproachSamples.Add(CreateApproachSample(
                challenge,
                start.AddSeconds(seconds),
                distanceNm,
                lateralOffset(progress),
                altitudeError(progress)));
        }
    }

    private static void AddSegment(
        LandingSession session,
        ChallengeConfig challenge,
        DateTimeOffset start,
        double startSeconds,
        double altitudeErrorFeet,
        double approachDistanceNm,
        double durationSeconds = 1.0)
    {
        session.Snapshot.ApproachSamples.Add(CreateApproachSample(
            challenge,
            start.AddSeconds(startSeconds),
            approachDistanceNm,
            lateralMeters: 0,
            altitudeErrorFeet));
        session.Snapshot.ApproachSamples.Add(CreateApproachSample(
            challenge,
            start.AddSeconds(startSeconds + durationSeconds),
            approachDistanceNm - 0.05,
            lateralMeters: 0,
            altitudeErrorFeet));
    }

    private static TelemetrySample CreateApproachSample(
        ChallengeConfig challenge,
        DateTimeOffset timestamp,
        double approachDistanceNm,
        double lateralMeters,
        double altitudeErrorFeet)
    {
        var runway = challenge.Runway;
        var headingRadians = runway.HeadingTrueDeg * Math.PI / 180.0;
        var runwayAlongMeters = -approachDistanceNm * 1_852.0;
        var northMeters =
            runwayAlongMeters * Math.Cos(headingRadians) - lateralMeters * Math.Sin(headingRadians);
        var eastMeters =
            runwayAlongMeters * Math.Sin(headingRadians) + lateralMeters * Math.Cos(headingRadians);
        var referenceLatitudeRadians = runway.ThresholdLatitude * Math.PI / 180.0;
        var latitude = runway.ThresholdLatitude
                       + northMeters / EarthRadiusMeters * 180.0 / Math.PI;
        var longitude = runway.ThresholdLongitude
                        + eastMeters / (EarthRadiusMeters * Math.Cos(referenceLatitudeRadians))
                        * 180.0 / Math.PI;
        var altitudeFeet = runway.ElevationFeet + approachDistanceNm * 318.0 + altitudeErrorFeet;

        return new TelemetrySample
        {
            Timestamp = timestamp,
            SimOnGround = false,
            Latitude = latitude,
            Longitude = longitude,
            AltitudeFeet = altitudeFeet,
            AglFeet = altitudeFeet - runway.ElevationFeet,
            RadioHeightFeet = altitudeFeet - runway.ElevationFeet,
            HeadingTrueDeg = runway.HeadingTrueDeg,
            GroundTrackTrueDeg = runway.HeadingTrueDeg,
            GroundSpeedKts = 140,
            AirspeedKts = 140,
            VerticalSpeedFpm = -700,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            GForce = 1
        };
    }

    private static TelemetrySample CreateGroundSample(
        ChallengeConfig challenge,
        DateTimeOffset timestamp,
        double groundSpeedKts)
    {
        var runway = challenge.Runway;
        return new TelemetrySample
        {
            Timestamp = timestamp,
            SimOnGround = true,
            Latitude = runway.ThresholdLatitude,
            Longitude = runway.ThresholdLongitude,
            AltitudeFeet = runway.ElevationFeet,
            AglFeet = 0,
            RadioHeightFeet = 0,
            HeadingTrueDeg = runway.HeadingTrueDeg,
            GroundTrackTrueDeg = runway.HeadingTrueDeg,
            GroundSpeedKts = groundSpeedKts,
            AirspeedKts = groundSpeedKts,
            VerticalSpeedFpm = groundSpeedKts >= 100 ? -150 : 0,
            GearHandlePosition = 1,
            FlapsHandleIndex = 3,
            GForce = 1
        };
    }

    private static ApproachValues CaptureMetrics(LandingSnapshot snapshot) => new(
        snapshot.ApproachPathSampleCount,
        snapshot.ApproachMetricDurationSec,
        snapshot.ApproachLateralDistanceM,
        snapshot.ApproachGlideslopeMeanAbsFt,
        snapshot.ApproachVerticalVariationFtPerSec,
        snapshot.ApproachLateralWeaveIndex,
        snapshot.ApproachPathRms);

    private static (ChallengeConfig Challenge, LandingSessionSettings Settings) Load()
    {
        var loader = new ConfigLoader(FindConfig());
        var keyResult = loader.LoadEvaluationKey();
        Assert.True(keyResult.IsValid, string.Join("; ", keyResult.Errors));
        var challenge = loader.LoadChallenge("challenges/barcelona-crosswind-final.json");
        return (challenge, keyResult.Key!.ToSessionSettings());
    }

    private static string FindConfig()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "config", "catalog.json")))
                return Path.Combine(directory.FullName, "config");
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("config not found");
    }

    private readonly record struct ApproachValues(
        int SampleCount,
        double DurationSeconds,
        double DistanceMeters,
        double MeanAbsoluteErrorFeet,
        double VerticalVariationFeetPerSecond,
        double LateralWeaveIndex,
        double RootMeanSquareErrorFeet);
}
