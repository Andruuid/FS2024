using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ChallengeLab.App.ViewModels;
using ChallengeLab.Core.Highscores;
using ChallengeLab.SimConnect;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace ChallengeLab.App.Views;

public partial class MainWindow : Window
{
    private const int WmUserSimConnect = 0x0402;
    private readonly MainViewModel _vm;
    private CompanionHudWindow? _hud;
    private SecondaryHudWindow? _secondaryHud;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(new SimConnectClient());
        DataContext = _vm;
        Title = AppBuild.WindowTitleDefault;

        _vm.RequestConnect += ConnectToSim;
        _vm.RequestShowHud += ShowHud;
        _vm.RequestToggleSecondaryHud += ToggleSecondaryHud;
        _vm.RequestToggleMain += ToggleMainWindow;

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
                ? AppBuild.WindowTitleDefault
                : _vm.WindowTitle;

            var report = _vm.LandingReport;
            if (report is null)
            {
                SummaryHost?.Children.Clear();
                MetricsHost?.Children.Clear();
                PenaltiesHost?.Children.Clear();
                PhaseSummaryHost?.Children.Clear();
                return;
            }

            if (ReportStatusText is not null)
            {
                ReportStatusText.Text = _vm.ReportStatus;
                ReportStatusText.Foreground = MediaBrushes.White;
                ReportStatusText.Background = MediaBrushes.Transparent;
                ReportStatusText.Visibility = Visibility.Collapsed;
            }

            if (ReportBodyBlock is not null)
            {
                ReportBodyBlock.Text = _vm.ReportBodyText;
                ReportBodyBlock.Visibility = Visibility.Collapsed;
            }

            if (SummaryHost is not null)
            {
                SummaryHost.Children.Clear();
                if (report.HasSummary)
                {
                    if (report.OverallPenaltyChain is { } overallPenaltyChain)
                        SummaryHost.Children.Add(CreateSummaryPenaltyChain(overallPenaltyChain, isOverall: true));
                    foreach (var phase in report.SummaryPhases)
                        SummaryHost.Children.Add(CreateSummaryPhaseSection(phase));
                }
                else
                {
                    SummaryHost.Children.Add(CreateSummaryUnavailableCard(report.SummaryUnavailableText));
                }
            }

            if (PhaseSummaryHost is not null)
            {
                PhaseSummaryHost.Children.Clear();
                foreach (var phase in report.Phases)
                    PhaseSummaryHost.Children.Add(CreatePhaseCard(phase));
            }
            if (PhaseSummarySection is not null)
                PhaseSummarySection.Visibility = report.Phases.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (PenaltiesHost is not null)
            {
                PenaltiesHost.Children.Clear();
                foreach (var penalty in report.Penalties)
                    PenaltiesHost.Children.Add(CreatePenaltyCard(penalty));
            }
            if (PenaltySection is not null)
                PenaltySection.Visibility = report.HasPenalties
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (MetricsHost is not null)
            {
                MetricsHost.Children.Clear();
                var metrics = report.DetailMetrics;

                foreach (var m in metrics)
                    MetricsHost.Children.Add(CreateMetricCard(m));

                if (metrics.Count == 0 && !report.HasDetail)
                {
                    MetricsHost.Children.Add(new TextBlock
                    {
                        Text = "No detailed metric breakdown was stored for this landing.",
                        Foreground = MediaBrushes.LightGray,
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 8, 0, 0)
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Title = $"Challenge Lab — {AppBuild.Tag} PAINT ERROR";
            if (ReportStatusText is not null)
            {
                ReportStatusText.Text = "PAINT ERROR: " + ex.Message;
                ReportStatusText.Foreground = MediaBrushes.OrangeRed;
                ReportStatusText.Visibility = Visibility.Visible;
            }
            if (ReportBodyBlock is not null)
                ReportBodyBlock.Text = ex.ToString();
        }
    }

    private static Border CreateSummaryPhaseSection(SummaryPhaseViewModel phase)
    {
        var stack = new StackPanel();
        stack.Children.Add(CreateSummaryBarRow(
            $"TOTAL {phase.DisplayName.ToUpperInvariant()}",
            phase.ScoreDisplay,
            phase.BarValue,
            phase.ScoreBand,
            barHeight: 20,
            isPhase: true,
            detail: ""));

        if (phase.PenaltyChain is { } penaltyChain)
            stack.Children.Add(CreateSummaryPenaltyChain(penaltyChain, isOverall: false));

        if (phase.Metrics.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Subcategory data was not stored for this phase.",
                FontSize = 11,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0x8B, 0x9B, 0xB8)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 9, 0, 0)
            });
        }
        else
        {
            foreach (var metric in phase.Metrics)
                stack.Children.Add(CreateSummaryMetricRow(metric));
        }

        var section = new Border
        {
            Child = stack,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x10, 0x18, 0x2B)),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x55, 0x4A, 0x90, 0xA4)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 10),
            ToolTip = CreateSummaryToolTip(phase.ToolTip)
        };
        ConfigureSummaryToolTip(section);
        return section;
    }

    private static Border CreateSummaryMetricRow(SummaryMetricViewModel metric)
    {
        var row = new Border
        {
            Child = CreateSummaryBarRow(
                metric.DisplayName,
                metric.ScoreDisplay,
                metric.BarValue,
                metric.ScoreBand,
                barHeight: 12,
                isPhase: false,
                detail: metric.DetailDisplay),
            Background = new SolidColorBrush(MediaColor.FromArgb(0x99, 0x0D, 0x15, 0x27)),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(9, 6, 9, 6),
            Margin = new Thickness(0, 7, 0, 0),
            ToolTip = CreateSummaryToolTip(metric.ToolTip)
        };
        ConfigureSummaryToolTip(row);
        return row;
    }

    private static Grid CreateSummaryBarRow(
        string title,
        string scoreDisplay,
        double value,
        SummaryScoreBand band,
        double barHeight,
        bool isPhase,
        string detail)
    {
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var titleBlock = new TextBlock
        {
            FontSize = isPhase ? 13 : 11.5,
            FontWeight = isPhase ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = MediaBrushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        titleBlock.Inlines.Add(new System.Windows.Documents.Run(title));
        if (!string.IsNullOrWhiteSpace(detail))
        {
            titleBlock.Inlines.Add(new System.Windows.Documents.Run($" · {detail}")
            {
                FontWeight = FontWeights.Normal,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0x8B, 0x9B, 0xB8))
            });
        }
        row.Children.Add(titleBlock);

        var bar = CreateSummaryProgressBar(
            value,
            band,
            barHeight,
            new Thickness(10, 0, 10, 0));
        Grid.SetColumn(bar, 1);
        row.Children.Add(bar);

        var score = new TextBlock
        {
            Text = scoreDisplay,
            FontSize = isPhase ? 18 : 13,
            FontWeight = FontWeights.Bold,
            Foreground = SummaryScoreBrush(band),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(score, 2);
        row.Children.Add(score);
        return row;
    }

    private static Border CreateSummaryPenaltyChain(
        SummaryPenaltyChainViewModel chain,
        bool isOverall)
    {
        var equation = new WrapPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        equation.Children.Add(new TextBlock
        {
            Text = isOverall ? "OVERALL PENALTY MATH" : "PENALTY MATH",
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0x7A, 0x8E)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0)
        });
        equation.Children.Add(CreatePenaltyPill(
            $"RAW {chain.RawScoreDisplay}",
            new SolidColorBrush(MediaColor.FromArgb(0x33, 0x2D, 0xE2, 0xE6)),
            new SolidColorBrush(MediaColor.FromArgb(0x66, 0x2D, 0xE2, 0xE6)),
            new SolidColorBrush(MediaColor.FromRgb(0xB8, 0xF0, 0xF2)),
            toolTip: null));

        foreach (var penalty in chain.Penalties)
        {
            equation.Children.Add(CreateEquationSeparator("×"));
            var pill = CreatePenaltyPill(
                $"{penalty.DisplayName} {penalty.MultiplierDisplay}",
                new SolidColorBrush(MediaColor.FromArgb(0x55, 0xFF, 0x4D, 0x6A)),
                new SolidColorBrush(MediaColor.FromArgb(0x99, 0xFF, 0x4D, 0x6A)),
                new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xC2, 0xCC)),
                CreateSummaryToolTip(penalty.ToolTip));
            ConfigureSummaryToolTip(pill);
            equation.Children.Add(pill);
        }

        equation.Children.Add(CreateEquationSeparator("="));
        equation.Children.Add(CreatePenaltyPill(
            $"TOTAL {chain.FinalScoreDisplay}",
            new SolidColorBrush(MediaColor.FromArgb(0x33, 0xFF, 0xB0, 0x20)),
            SummaryScoreBrush(chain.FinalScoreBand),
            SummaryScoreBrush(chain.FinalScoreBand),
            toolTip: null));
        if (!string.IsNullOrWhiteSpace(chain.PointLossDisplay))
        {
            equation.Children.Add(new TextBlock
            {
                Text = chain.PointLossDisplay,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(MediaColor.FromRgb(0xFF, 0x7A, 0x8E)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            });
        }

        return new Border
        {
            Child = equation,
            Background = new SolidColorBrush(MediaColor.FromArgb(0x44, 0x3A, 0x16, 0x20)),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x77, 0xFF, 0x4D, 0x6A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(8, 6, 8, 6),
            Margin = isOverall ? new Thickness(0, 0, 0, 10) : new Thickness(0, 8, 0, 1)
        };
    }

    private static Border CreatePenaltyPill(
        string text,
        MediaBrush background,
        MediaBrush border,
        MediaBrush foreground,
        object? toolTip) => new()
    {
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = foreground,
            VerticalAlignment = VerticalAlignment.Center
        },
        Background = background,
        BorderBrush = border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(7, 3, 7, 3),
        ToolTip = toolTip
    };

    private static TextBlock CreateEquationSeparator(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(MediaColor.FromRgb(0xC5, 0xD0, 0xE6)),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(6, 0, 6, 0)
    };

    private static WpfProgressBar CreateSummaryProgressBar(
        double value,
        SummaryScoreBand band,
        double height,
        Thickness margin) => new()
    {
        Minimum = 0,
        Maximum = 100,
        Value = Math.Clamp(value, 0, 100),
        Height = height,
        Margin = margin,
        Foreground = SummaryScoreBrush(band),
        Background = new SolidColorBrush(MediaColor.FromArgb(0x55, 0x4A, 0x55, 0x68)),
        BorderThickness = new Thickness(0),
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
        SnapsToDevicePixels = true
    };

    private static Border CreateSummaryUnavailableCard(string message) => new()
    {
        Child = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = new SolidColorBrush(MediaColor.FromRgb(0xC5, 0xD0, 0xE6)),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center
        },
        Background = new SolidColorBrush(MediaColor.FromRgb(0x10, 0x18, 0x2B)),
        BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x55, 0x4A, 0x90, 0xA4)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(20),
        Margin = new Thickness(0, 8, 0, 0)
    };

    private static WpfToolTip CreateSummaryToolTip(string text) => new()
    {
        Content = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = MediaBrushes.White,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 390,
            LineHeight = 16
        },
        Background = new SolidColorBrush(MediaColor.FromRgb(0x1A, 0x25, 0x40)),
        BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0x4A, 0x90, 0xA4)),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(10)
    };

    private static void ConfigureSummaryToolTip(FrameworkElement element)
    {
        ToolTipService.SetInitialShowDelay(element, 150);
        ToolTipService.SetShowDuration(element, 30_000);
        ToolTipService.SetBetweenShowDelay(element, 50);
    }

    private static MediaBrush SummaryScoreBrush(SummaryScoreBand band) => band switch
    {
        SummaryScoreBand.Green => new SolidColorBrush(MediaColor.FromRgb(0x62, 0xE6, 0xA7)),
        SummaryScoreBand.Orange => new SolidColorBrush(MediaColor.FromRgb(0xFF, 0xB0, 0x20)),
        SummaryScoreBand.Red => new SolidColorBrush(MediaColor.FromRgb(0xFF, 0x4D, 0x6A)),
        _ => new SolidColorBrush(MediaColor.FromRgb(0x8B, 0x9B, 0xB8))
    };

    private static Border CreatePhaseCard(ReportPhaseViewModel phase)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = phase.DisplayName.ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = MediaBrushes.LightGray
        });
        stack.Children.Add(new TextBlock
        {
            Text = phase.ScoreDisplay,
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = MediaBrushes.White,
            Margin = new Thickness(0, 3, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = phase.WeightDisplay,
            FontSize = 10,
            Foreground = MediaBrushes.LightBlue,
            Margin = new Thickness(0, 2, 0, 0)
        });

        return new Border
        {
            Child = stack,
            Width = 150,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x1A, 0x25, 0x40)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 8, 8)
        };
    }

    private static Border CreatePenaltyCard(PenaltyViewModel penalty)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = penalty.DisplayName,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = MediaBrushes.White,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"{penalty.ScopeDisplay}  ·  {penalty.MultiplierDisplay}  ·  Value: {penalty.RawDisplay}",
            FontSize = 11,
            FontFamily = new MediaFontFamily("Consolas"),
            Foreground = MediaBrushes.LightPink,
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = penalty.Note,
            FontSize = 12,
            Foreground = MediaBrushes.White,
            Margin = new Thickness(0, 7, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18
        });

        return new Border
        {
            Child = stack,
            Background = new SolidColorBrush(MediaColor.FromRgb(0x3A, 0x16, 0x20)),
            BorderBrush = new SolidColorBrush(MediaColor.FromRgb(0xA8, 0x36, 0x4C)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 8)
        };
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
        _secondaryHud?.Close();
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
            // No Owner — so hiding the main Menu window does not hide the HUD.
            _hud = new CompanionHudWindow(_vm);
            _hud.Closed += (_, _) => _hud = null;
        }

        _hud.EnsureExpanded();
        if (!_hud.IsVisible)
            _hud.Show();
        _hud.Activate();
    }

    private void ToggleSecondaryHud()
    {
        if (_secondaryHud is { IsVisible: true })
        {
            _secondaryHud.HideFromUser();
            return;
        }

        if (_secondaryHud is null)
        {
            // Deliberately unowned so it remains visible when the Menu window is hidden.
            _secondaryHud = new SecondaryHudWindow(_vm);
            _secondaryHud.HiddenByUser += (_, _) => _vm.SetSecondaryHudVisible(false);
            _secondaryHud.Closed += (_, _) =>
            {
                _vm.SetSecondaryHudVisible(false);
                _secondaryHud = null;
            };
        }

        _secondaryHud.EnsureVisible();
        _vm.SetSecondaryHudVisible(true);
    }

    /// <summary>
    /// Menu toggle: show main window if hidden/minimized, otherwise hide it.
    /// HUD stays visible either way (HUD is not owned by main).
    /// </summary>
    private void ToggleMainWindow()
    {
        if (!IsVisible || WindowState == WindowState.Minimized)
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            return;
        }

        // Visible and normal → hide main (HUD remains).
        Hide();
        // Keep HUD on top after main goes away.
        if (_hud is { IsVisible: true })
            _hud.Activate();
    }
}
