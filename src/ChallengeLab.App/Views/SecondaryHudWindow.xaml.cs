using System.Windows;
using System.Windows.Input;
using ChallengeLab.App.ViewModels;

namespace ChallengeLab.App.Views;

public partial class SecondaryHudWindow : Window
{
    private readonly SecondaryHudPositionStore _positionStore;
    private bool _positionRestored;

    public SecondaryHudWindow(MainViewModel vm, SecondaryHudPositionStore? positionStore = null)
    {
        InitializeComponent();
        DataContext = vm.SecondaryHud;
        _positionStore = positionStore ?? new SecondaryHudPositionStore();
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetDefaultPosition();
        Loaded += (_, _) => RestorePosition();
        Closed += (_, _) => SavePosition();
    }

    public event EventHandler? HiddenByUser;

    private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            return;
        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can race with mouse release.
        }
    }

    private void HideMonitor_Click(object sender, RoutedEventArgs e) => HideFromUser();

    public void HideFromUser()
    {
        SavePosition();
        Hide();
        HiddenByUser?.Invoke(this, EventArgs.Empty);
    }

    public void EnsureVisible()
    {
        if (!_positionRestored)
            RestorePosition();
        if (!IsVisible)
            Show();
        Activate();
    }

    private void SetDefaultPosition()
    {
        Left = Math.Max(
            SystemParameters.WorkArea.Left + 16,
            SystemParameters.WorkArea.Right - Width - 480);
        Top = SystemParameters.WorkArea.Top + 80;
    }

    private void RestorePosition()
    {
        if (_positionRestored) return;
        _positionRestored = true;
        if (_positionStore.Load() is not { } saved) return;
        var clamped = ClampToVisibleDesktop(saved, Width, Height);
        Left = clamped.Left;
        Top = clamped.Top;
    }

    private void SavePosition()
    {
        if (WindowState == WindowState.Normal)
            _positionStore.Save(Left, Top);
    }

    public static SecondaryHudPosition ClampToVisibleDesktop(
        SecondaryHudPosition position,
        double width,
        double height)
    {
        var leftEdge = SystemParameters.VirtualScreenLeft;
        var topEdge = SystemParameters.VirtualScreenTop;
        var rightEdge = leftEdge + SystemParameters.VirtualScreenWidth;
        var bottomEdge = topEdge + SystemParameters.VirtualScreenHeight;
        var safeWidth = double.IsFinite(width) && width > 0 ? width : 420;
        var safeHeight = double.IsFinite(height) && height > 0 ? height : 560;
        var maxLeft = Math.Max(leftEdge, rightEdge - safeWidth);
        var maxTop = Math.Max(topEdge, bottomEdge - safeHeight);
        return new SecondaryHudPosition(
            Math.Clamp(position.Left, leftEdge, maxLeft),
            Math.Clamp(position.Top, topEdge, maxTop));
    }
}
