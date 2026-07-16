using System.IO;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ChallengeLab.App.Controls;
using ChallengeLab.App.ViewModels;
using ChallengeLab.Core.Highscores;
using ChallengeLab.Core.Models;
using ChallengeLab.Core.Scoring;

namespace ChallengeLab.App.Tests;

public sealed class LandingVisualizationTests
{
    [Fact]
    public void ViewModel_FormatsPositionMetricsAndEvidenceBasedCoaching()
    {
        var entry = Entry();
        entry.Criteria =
        [
            Criterion("touchdown_impact", "Touchdown impact", 88,
                "[Touchdown · 55%] Measured: -245 fpm and 1.31 g. Impact was controlled."),
            Criterion("touchdown_point", "Touchdown point", 72,
                "[Touchdown · 20%] Measured: touchdown 1250 ft from threshold; perfect point 1200 ft; error +50 ft (late)."),
            Criterion("centerline", "Centerline", 98,
                "[Touchdown · 10%] Measured: 3.2 m right of centerline.")
        ];

        var view = new LandingVisualizationViewModel(entry, entry.LandingVisualization!);

        Assert.Equal("TEST  ·  RUNWAY 09L", view.RunwayTitle);
        Assert.Equal("1250 FT FROM THRESHOLD", view.PositionHeadline);
        Assert.Contains("50 FT LATE", view.PositionDetail);
        Assert.Contains("3.2 M RIGHT", view.PositionDetail);
        Assert.Equal("-245", view.VerticalSpeed.Value);
        Assert.Equal("1.31", view.PeakG.Value);
        Assert.Equal("Centerline", view.StrengthTitle);
        Assert.Contains("98%", view.StrengthBody);
        Assert.Equal("Touchdown point", view.ImprovementTitle);
        Assert.Contains("perfect point", view.ImprovementBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("245 fpm", view.VerdictSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ViewModel_FailedGateTakesImprovementPrecedence()
    {
        var entry = Entry();
        entry.Criteria =
        [
            Criterion("touchdown_point", "Touchdown point", 45, "Touchdown point needs work."),
            new HighscoreCriterionDetail
            {
                Id = "gear", DisplayName = "Gear safety", Status = MetricStatus.GateFailed,
                Note = "Gear was not down; touchdown phase multiplier applied."
            }
        ];

        var view = new LandingVisualizationViewModel(entry, entry.LandingVisualization!);

        Assert.Equal("Gear safety", view.ImprovementTitle);
        Assert.StartsWith("FAILED GATE", view.ImprovementBody);
        Assert.Contains("Gear was not down", view.ImprovementBody);
    }

    [Fact]
    public void ViewModel_AllExcellentMetricsUsesConsistencyFallback()
    {
        var entry = Entry();
        entry.Criteria =
        [
            Criterion("touchdown_impact", "Touchdown impact", 99, "Excellent impact."),
            Criterion("touchdown_point", "Touchdown point", 97, "Excellent touchdown point."),
            Criterion("centerline", "Centerline", 100, "On centerline.")
        ];

        var view = new LandingVisualizationViewModel(entry, entry.LandingVisualization!);

        Assert.Equal("No major weakness detected", view.ImprovementTitle);
        Assert.Contains("at least 95%", view.ImprovementBody);
    }

    [Fact]
    public void HistoricalEntryDoesNotInventVisualization()
    {
        var report = new LandingReportViewModel(new HighscoreEntry
        {
            ChallengeTitle = "Historical", ScorePercent = 80, Grade = "B"
        });

        Assert.False(report.HasVisualization);
        Assert.Null(report.Visualization);
        Assert.Contains("not stored", report.VisualizationUnavailableText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunwayLayout_FocusesTheFirstThirdAndClipsOutsideTheLandingZone()
    {
        var data = Entry().LandingVisualization!;
        data.RunwayLengthM = 3000;
        data.RunwayWidthM = 40;
        data.TouchdownDistanceFromThresholdM = 0;
        data.TouchdownLateralOffsetM = 0;

        var threshold = PrecisionRunwayView.CalculateLayout(data, 800, 300);
        Assert.Equal(threshold.Runway.Left, threshold.Touchdown.X, 6);
        Assert.Equal(threshold.Runway.Top + threshold.Runway.Height / 2, threshold.Touchdown.Y, 6);
        Assert.Equal(data.RunwayLengthM / 3, threshold.VisibleDistanceM, 6);
        Assert.True(threshold.RunwayContinues);

        data.TouchdownDistanceFromThresholdM = data.RunwayLengthM / 6;
        data.TouchdownLateralOffsetM = 20;
        var rightEdge = PrecisionRunwayView.CalculateLayout(data, 800, 300);
        Assert.Equal(rightEdge.Runway.Left + rightEdge.Runway.Width / 2, rightEdge.Touchdown.X, 6);
        Assert.Equal(rightEdge.Runway.Bottom, rightEdge.Touchdown.Y, 6);

        data.TouchdownDistanceFromThresholdM = data.RunwayLengthM * .4;
        data.TouchdownLateralOffsetM = 0;
        var beyondLandingZone = PrecisionRunwayView.CalculateLayout(data, 800, 300);
        Assert.True(beyondLandingZone.ClippedAfter);
        Assert.True(beyondLandingZone.Touchdown.X > beyondLandingZone.Runway.Right);

        data.TouchdownDistanceFromThresholdM = -500 / 3.280839895;
        data.TouchdownLateralOffsetM = -100;
        var overflow = PrecisionRunwayView.CalculateLayout(data, 800, 300);
        Assert.True(overflow.ClippedBefore);
        Assert.True(overflow.ClippedLateral);
        Assert.True(overflow.Touchdown.X < overflow.Runway.Left);
        Assert.True(overflow.Touchdown.Y < overflow.Runway.Top);
    }

    [Fact]
    public void RunwayLayout_ShowsThePhysicalEndOnlyForRunwaysShorterThanTheFocusRange()
    {
        var data = Entry().LandingVisualization!;
        data.RunwayLengthM = 500;
        data.TouchdownDistanceFromThresholdM = 500;

        var layout = PrecisionRunwayView.CalculateLayout(data, 800, 300);

        Assert.False(layout.RunwayContinues);
        Assert.Equal(500, layout.VisibleDistanceM, 6);
        Assert.Equal(layout.Runway.Right, layout.Touchdown.X, 6);
        Assert.False(layout.ClippedAfter);
    }

    [Fact]
    public void VectorControlsRenderNonEmptyOutputOnStaThread()
    {
        RunSta(() =>
        {
            var focusedTouchdown = Entry().LandingVisualization!;
            focusedTouchdown.RunwayId = "32";
            focusedTouchdown.RunwayLengthM = 3392;
            focusedTouchdown.TouchdownDistanceFromThresholdM = 3548 / 3.280839895;
            focusedTouchdown.TouchdownLateralOffsetM = 5.5;
            AssertNonEmptyRender(
                new PrecisionRunwayView { Data = focusedTouchdown }, 800, 292, "precision-runway.png");
            AssertNonEmptyRender(
                new TouchdownAttitudeView { Data = Entry().LandingVisualization }, 600, 340, "touchdown-attitude.png");
        });
    }

    [Fact]
    public void HighscoreXaml_ContainsTwoResultTabsOneWayVisualBindingsAndDefaultReset()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(root, "src", "ChallengeLab.App", "Views", "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(root, "src", "ChallengeLab.App", "ViewModels", "MainViewModel.cs"));

        Assert.Contains("Header=\"LANDING VISUAL\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"DETAILED REPORT\"", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:PrecisionRunwayView", xaml, StringComparison.Ordinal);
        Assert.Contains("controls:TouchdownAttitudeView", xaml, StringComparison.Ordinal);
        Assert.Contains("LandingReport.Visualization.Data, Mode=OneWay", xaml, StringComparison.Ordinal);
        Assert.Contains("LandingReport.HasVisualization", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedResultTab = 0;", viewModel, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"MetricsHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PenaltiesHost\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PhaseSummaryHost\"", xaml, StringComparison.Ordinal);
    }

    private static HighscoreEntry Entry() => new()
    {
        Utc = new DateTimeOffset(2026, 7, 16, 20, 0, 0, TimeSpan.Zero),
        ChallengeId = "visualization-test",
        ChallengeTitle = "Visualization test",
        ScorePercent = 86.4,
        Grade = "A",
        EvaluationKeyId = "landing-evaluation-key",
        EvaluationKeyVersion = 22,
        ScoringProfileHash = "visual-test",
        RankedBucketId = "visualization-test|landing-evaluation-key|v22|visual-test",
        Criteria =
        [
            new HighscoreCriterionDetail
            {
                Id = "touchdown_impact", DisplayName = "Touchdown impact", ScorePercent = 88,
                Status = MetricStatus.Scored, PhaseId = "touchdown", PhaseDisplayName = "Touchdown",
                Note = "Measured: -245 fpm and 1.31 g."
            }
        ],
        Phases =
        [
            new HighscorePhaseDetail
            {
                PhaseId = "touchdown", DisplayName = "Touchdown", WeightPercent = 70,
                ScorePercent = 86.4, Used = true
            }
        ],
        LandingVisualization = new LandingVisualizationData
        {
            AirportIcao = "TEST",
            RunwayId = "09L",
            RunwayHeadingTrueDeg = 91,
            RunwayLengthM = 3000,
            RunwayWidthM = 45,
            TouchdownDistanceFromThresholdM = 1250 / 3.280839895,
            IdealTouchdownDistanceFromThresholdM = 1200 / 3.280839895,
            TouchdownLateralOffsetM = 3.2,
            TouchdownHeadingErrorDeg = -1.5,
            TouchdownBankDeg = 2.4,
            TouchdownPitchDeg = 5.1,
            TouchdownVerticalSpeedFpm = -245,
            TouchdownRawPeakG = 1.38,
            TouchdownRobustPeakG = 1.31,
            TouchdownAirspeedKts = 140,
            TargetTouchdownAirspeedKts = 138
        }
    };

    private static HighscoreCriterionDetail Criterion(string id, string name, double score, string note) => new()
    {
        Id = id,
        DisplayName = name,
        ScorePercent = score,
        Status = MetricStatus.Scored,
        Note = note,
        PhaseId = "touchdown",
        PhaseDisplayName = "Touchdown",
        MaxOverallPoints = 10
    };

    private static void AssertNonEmptyRender(
        FrameworkElement element,
        int width,
        int height,
        string captureName)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        Assert.Contains(pixels, value => value != 0);

        var captureDirectory = Environment.GetEnvironmentVariable("CHALLENGELAB_VISUAL_QA_DIR");
        if (!string.IsNullOrWhiteSpace(captureDirectory))
        {
            Directory.CreateDirectory(captureDirectory);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(captureDirectory, captureName));
            encoder.Save(stream);
        }
    }

    private static void RunSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ChallengeLab.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("repository root not found");
    }
}
