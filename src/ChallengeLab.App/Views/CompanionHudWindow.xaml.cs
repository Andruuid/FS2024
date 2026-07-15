using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ChallengeLab.App.ViewModels;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;
using WpfButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using WpfScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfThumb = System.Windows.Controls.Primitives.Thumb;

namespace ChallengeLab.App.Views;

public partial class CompanionHudWindow : Window
{
    private const double ExpandedWidth = 440;
    private const double ExpandedHeight = 420;
    private const double CompactWidth = 120;
    private const double CompactHeight = 72;

    private bool _compact;
    private double _expandedLeft;
    private double _expandedTop;

    public CompanionHudWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Default position: upper-right of primary screen
        Left = SystemParameters.WorkArea.Right - ExpandedWidth - 24;
        Top = SystemParameters.WorkArea.Top + 80;
        Width = ExpandedWidth;
        Height = ExpandedHeight;
    }

    /// <summary>Drag from chrome / header / empty areas (not from buttons).</summary>
    private void Chrome_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            return;
        if (IsFromInteractiveControl(e.OriginalSource as DependencyObject))
            return;

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove throws if mouse button is not down (rare race).
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => Chrome_MouseLeftButtonDown(sender, e);

    private static bool IsFromInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is WpfButton or WpfButtonBase or WpfScrollBar or WpfTextBox or WpfThumb)
                return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void CompactToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_compact)
            ExpandHud();
        else
            CollapseHud();
    }

    private void CollapseHud()
    {
        _compact = true;
        _expandedLeft = Left;
        _expandedTop = Top;

        // Fully transparent chrome — only the Show chip remains visible.
        ContentBody.Visibility = Visibility.Collapsed;
        HeaderBar.Visibility = Visibility.Collapsed;
        GoButton.Visibility = Visibility.Collapsed;
        CleanButton.Visibility = Visibility.Collapsed;
        RestartButton.Visibility = Visibility.Collapsed;
        MenuButton.Visibility = Visibility.Collapsed;

        HideShowButton.Content = "Show";
        HideShowButton.ToolTip = "Expand HUD · drag empty area around button to move";
        HideShowButton.Style = (Style)FindResource("AmberButton");
        HideShowButton.FontWeight = FontWeights.Bold;
        HideShowButton.Padding = new Thickness(18, 10, 18, 10);

        // Fully transparent chrome; only the Show chip is painted.
        ChromeBackground.Color = MediaColor.FromArgb(0, 0, 0, 0);
        ChromeBorder.BorderBrush = MediaBrushes.Transparent;
        ChromeBorder.BorderThickness = new Thickness(0);
        ChromeShadow.Opacity = 0;
        ChromeBorder.Margin = new Thickness(4);
        ((Grid)ChromeBorder.Child).Margin = new Thickness(4);

        Width = CompactWidth;
        Height = CompactHeight;
        // Keep roughly the same corner so the Show chip stays where Hide was.
        Left = _expandedLeft + ExpandedWidth - CompactWidth - 8;
        Top = _expandedTop + ExpandedHeight - CompactHeight - 8;
        if (Left < SystemParameters.WorkArea.Left)
            Left = SystemParameters.WorkArea.Left + 8;
        if (Top < SystemParameters.WorkArea.Top)
            Top = SystemParameters.WorkArea.Top + 8;
    }

    private void ExpandHud()
    {
        _compact = false;

        ContentBody.Visibility = Visibility.Visible;
        HeaderBar.Visibility = Visibility.Visible;
        GoButton.Visibility = Visibility.Visible;
        CleanButton.Visibility = Visibility.Visible;
        RestartButton.Visibility = Visibility.Visible;
        MenuButton.Visibility = Visibility.Visible;

        HideShowButton.Content = "Hide";
        HideShowButton.ToolTip = "Collapse HUD to a small Show chip (drag to move).";
        HideShowButton.Style = (Style)FindResource("GhostButton");
        HideShowButton.FontWeight = FontWeights.Normal;
        HideShowButton.Padding = new Thickness(12, 8, 12, 8);

        ChromeBackground.Color = MediaColor.FromArgb(0xE0, 0x12, 0x1A, 0x2E);
        ChromeBorder.BorderBrush = new SolidColorBrush(MediaColor.FromArgb(0x55, 0x2D, 0xE2, 0xE6));
        ChromeBorder.BorderThickness = new Thickness(1);
        ChromeShadow.Opacity = 0.55;
        ChromeBorder.Margin = new Thickness(8);
        ((Grid)ChromeBorder.Child).Margin = new Thickness(16);

        Width = ExpandedWidth;
        Height = ExpandedHeight;
        Left = _expandedLeft;
        Top = _expandedTop;
    }

    /// <summary>Ensure full HUD is visible (e.g. after Start Challenge).</summary>
    public void EnsureExpanded()
    {
        if (_compact)
            ExpandHud();
        if (!IsVisible)
            Show();
    }
}
