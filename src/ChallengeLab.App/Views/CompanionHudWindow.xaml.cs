using System.Windows;
using System.Windows.Input;
using ChallengeLab.App.ViewModels;

namespace ChallengeLab.App.Views;

public partial class CompanionHudWindow : Window
{
    public CompanionHudWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Default position: upper-right of primary screen
        Left = SystemParameters.WorkArea.Right - Width - 24;
        Top = SystemParameters.WorkArea.Top + 80;
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Hide();
}
