using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ChallengeLab.App.ViewModels;
using ChallengeLab.Core.Highscores;
using ChallengeLab.SimConnect;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfProgressBar = System.Windows.Controls.ProgressBar;

namespace ChallengeLab.App.Views;

public partial class MainWindow : Window
{
    private const int WmUserSimConnect = 0x0402;
    private readonly MainViewModel _vm;
    private CompanionHudWindow? _hud;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new SimConnectClient());
        DataContext = _vm;
        Title = "Challenge Lab — BUILD 2220";

        _vm.RequestConnect += ConnectToSim;
        _vm.RequestShowHud += ShowHud;
        _vm.RequestActivateMain += () =>
        {
            Activate();
            WindowState = WindowState.Normal;
        };

        // Always paint report in code — no reliance on fragile ItemsControl bindings
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.LandingReport)
                or nameof(MainViewModel.ReportStatus)
                or nameof(MainViewModel.ReportBodyText)
                or nameof(MainViewModel.ReportMetrics)
                or nameof(MainViewModel.WindowTitle))
            {
                Dispatcher.InvokeAsync(PaintReportPanel);
            }
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void HighscoresGrid_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HighscoresGrid.SelectedItem is HighscoreEntry entry)
            _vm.SelectedHighscore = entry;

        // Paint immediately on selection (same UI thread)
        PaintReportPanel();
    }

    /// <summary>
    /// Imperatively fill the report panel. Bypasses WPF binding/ItemsControl layout bugs.
    /// </summary>
    private void PaintReportPanel()
    {
        try
        {
            Title = string.IsNullOrWhiteSpace(_vm.WindowTitle)
                ? "Challenge Lab — BUILD 2220"
                : _vm.WindowTitle;

            var report = _vm.LandingReport;
            if (report is null)
            {
                if (ReportStatusText is not null)
                    ReportStatusText.Text = "(no highscore selected)";
                if (ReportBodyBlock is not null)
                    ReportBodyBlock.Text = "";
                MetricsHost?.Children.Clear();
                return;
            }

            // Direct property assignment — binding cannot swallow this
            if (ReportStatusText is not null)
            {
                ReportStatusText.Text = string.IsNullOrWhiteSpace(_vm.ReportStatus)
                    ? $"BUILD 2220 FALLBACK | metrics={_vm.ReportMetrics.Count} | bodyLen={_vm.ReportBodyText?.Length ?? 0}"
                    : _vm.ReportStatus;
                ReportStatusText.Foreground = MediaBrushes.Black;
                ReportStatusText.Background = MediaBrushes.Transparent;
                ReportStatusText.FontSize = 16;
                ReportStatusText.FontWeight = FontWeights.Bold;
                ReportStatusText.Visibility = Visibility.Visible;
            }

            if (ReportBodyBlock is not null)
            {
                var body = _vm.ReportBodyText;
                if (string.IsNullOrWhiteSpace(body))
                    body = BuildBodyFallback(report);

                ReportBodyBlock.Text = body;
                ReportBodyBlock.Foreground = MediaBrushes.Yellow;
                ReportBodyBlock.FontSize = 13;
                ReportBodyBlock.Visibility = Visibility.Visible;
            }

            if (MetricsHost is not null)
            {
                MetricsHost.Children.Clear();
                var metrics = _vm.ReportMetrics;
                if (metrics.Count == 0 && report.Metrics.Count > 0)
                    metrics = report.Metrics;

                foreach (var m in metrics)
                    MetricsHost.Children.Add(CreateMetricCard(m));

                if (metrics.Count == 0)
                {
                    MetricsHost.Children.Add(new TextBlock
                    {
                        Text = "No metric cards to show. Check yellow text dump above / Session log.",
                        Foreground = MediaBrushes.OrangeRed,
                        FontSize = 14,
                        FontWeight = FontWeights.Bold,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Title = "Challenge Lab — BUILD 2220 PAINT ERROR";
            if (ReportStatusText is not null)
                ReportStatusText.Text = "PAINT ERROR: " + ex.Message;
            if (ReportBodyBlock is not null)
                ReportBodyBlock.Text = ex.ToString();
        }
    }

    private static string BuildBodyFallback(LandingReportViewModel report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"FALLBACK BODY | report metrics={report.MetricCount}");
        foreach (var m in report.Metrics)
        {
            sb.AppendLine($"* {m.DisplayName}: {m.ScoreDisplay} | {m.RawDisplay}");
            sb.AppendLine($"  {m.Note}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static Border CreateMetricCard(ReportMetricViewModel m)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = $"{m.DisplayName}   [{m.Verdict}]   {m.ScoreDisplay}",
            FontWeight = FontWeights.Bold,
            FontSize = 15,
            Foreground = MediaBrushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Value: {m.RawDisplay}    {m.InfluenceDisplay}",
            FontFamily = new MediaFontFamily("Consolas"),
            FontSize = 13,
            Foreground = MediaBrushes.LightYellow,
            Margin = new Thickness(0, 6, 0, 0)
        });
        stack.Children.Add(new WpfProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Max(0, Math.Min(100, m.BarValue)),
            Height = 12,
            Margin = new Thickness(0, 8, 0, 0),
            Visibility = m.IsScored ? Visibility.Visible : Visibility.Collapsed
        });
        stack.Children.Add(new TextBlock
        {
            Text = "EXPLANATION",
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = MediaBrushes.Cyan,
            Margin = new Thickness(0, 10, 0, 4)
        });
        stack.Children.Add(new TextBlock
        {
            Text = m.Note,
            FontSize = 13,
            Foreground = MediaBrushes.White,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20
        });

        return new Border
        {
            Child = stack,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x1E, 0x2A, 0x48)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x4A, 0x90, 0xA4)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14),
            Margin = new Thickness(0, 0, 0, 12)
        };
    }

    private void ConnectToSim()
    {
        var helper = new WindowInteropHelper(this);
        if (helper.Handle == IntPtr.Zero)
            helper.EnsureHandle();
        _vm.AttachWindowHandle(helper.Handle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var source = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
        source?.AddHook(WndProc);
        ConnectToSim();
        PaintReportPanel();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _hud?.Close();
        _vm.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmUserSimConnect)
        {
            _vm.PumpSimConnect();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void ShowHud()
    {
        if (_hud is null)
        {
            _hud = new CompanionHudWindow(_vm) { Owner = this };
            _hud.Closed += (_, _) => _hud = null;
        }

        if (!_hud.IsVisible)
            _hud.Show();
        _hud.Activate();
    }
}
