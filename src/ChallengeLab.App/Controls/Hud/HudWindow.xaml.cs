using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace ChallengeLab.App.Controls.Hud;

/// <summary>Transparent, non-activating overlay that tracks the active MSFS client area.</summary>
public partial class HudWindow : Window
{
    private const int WindowStyleIndex = -20;
    private const long ExtendedToolWindow = 0x00000080L;
    private const long ExtendedNoActivate = 0x08000000L;
    private const int WindowMessageNonClientHitTest = 0x0084;
    private const int WindowMessageMouseActivate = 0x0021;
    private const int HitTestTransparent = -1;
    private const int HitTestClient = 1;
    private const int MouseActivateNoActivate = 3;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindow = 0x0040;
    private static readonly IntPtr TopmostWindow = new(-1);

    private readonly SimulatorWindowTracker _tracker = new();
    private readonly HudViewGate _viewGate = new();
    private readonly DispatcherTimer _placementTimer;
    private HwndSource? _source;
    private bool _userVisible;
    private bool _viewAllowed;
    private bool _hasBeenShown;
    private bool _isApplyingScale;
    private bool _isApplyingOpacity;

    public HudWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        _placementTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _placementTimer.Tick += OnPlacementTick;
        _placementTimer.Start();
        ApplyScale(0.78);
        ApplyOpacity(0.95);
    }

    internal event Action<double>? ScaleChanged;
    internal event Action<double>? OpacityChanged;

    internal void SetUserVisible(bool visible)
    {
        _userVisible = visible;
        SynchronizePlacement();
    }

    internal void ApplyScale(double scale)
    {
        var bounded = Math.Clamp(scale, SizeSlider.Minimum, SizeSlider.Maximum);
        Visual.UpdateScale(bounded);
        _isApplyingScale = true;
        try
        {
            SizeSlider.Value = bounded;
        }
        finally
        {
            _isApplyingScale = false;
        }
    }

    internal void ApplyOpacity(double opacity)
    {
        var bounded = Math.Clamp(opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        Visual.UpdateOpacity(bounded);
        _isApplyingOpacity = true;
        try
        {
            OpacitySlider.Value = bounded;
        }
        finally
        {
            _isApplyingOpacity = false;
        }
    }

    internal void UpdatePresentation(HudPresentationFrame? frame)
    {
        Visual.UpdatePresentation(frame);
        var viewAllowed = _viewGate.ShouldShow(frame);
        if (_viewAllowed == viewAllowed)
            return;

        _viewAllowed = viewAllowed;
        SynchronizePlacement();
    }

    private void OnSourceInitialized(object? sender, EventArgs eventArgs)
    {
        _source = (HwndSource)PresentationSource.FromVisual(this)!;
        _source.AddHook(WindowProcedure);
        var handle = _source.Handle;
        var styles = GetWindowLongPtr(handle, WindowStyleIndex).ToInt64();
        SetWindowLongPtr(
            handle,
            WindowStyleIndex,
            new IntPtr(styles | ExtendedToolWindow | ExtendedNoActivate));
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _placementTimer.Stop();
        _placementTimer.Tick -= OnPlacementTick;
        if (_source is not null)
        {
            _source.RemoveHook(WindowProcedure);
            _source = null;
        }
    }

    private void OnPlacementTick(object? sender, EventArgs eventArgs) => SynchronizePlacement();

    private void SynchronizePlacement()
    {
        if (!_userVisible
            || !_viewAllowed
            || !_tracker.TryGetClientBounds(out var bounds))
        {
            if (_hasBeenShown && IsVisible)
                Hide();
            return;
        }

        if (!_hasBeenShown)
        {
            _hasBeenShown = true;
            Show();
        }
        else if (!IsVisible)
        {
            Show();
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        SetWindowPos(
            handle,
            TopmostWindow,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            NoActivate | ShowWindow);
    }

    private IntPtr WindowProcedure(
        IntPtr window,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == WindowMessageNonClientHitTest)
        {
            var packed = longParameter.ToInt64();
            var screenPoint = new Point(
                unchecked((short)(packed & 0xffff)),
                unchecked((short)((packed >> 16) & 0xffff)));
            handled = true;
            return IsPointInsideSizeControl(screenPoint)
                ? new IntPtr(HitTestClient)
                : new IntPtr(HitTestTransparent);
        }

        if (message == WindowMessageMouseActivate)
        {
            handled = true;
            return new IntPtr(MouseActivateNoActivate);
        }

        return IntPtr.Zero;
    }

    private bool IsPointInsideSizeControl(Point screenPoint)
    {
        if (!SizeControl.IsVisible || SizeControl.ActualWidth <= 0 || SizeControl.ActualHeight <= 0)
            return false;

        try
        {
            var local = SizeControl.PointFromScreen(screenPoint);
            return local.X >= 0 && local.Y >= 0
                   && local.X <= SizeControl.ActualWidth
                   && local.Y <= SizeControl.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (_isApplyingScale)
            return;

        Visual.UpdateScale(eventArgs.NewValue);
        ScaleChanged?.Invoke(eventArgs.NewValue);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> eventArgs)
    {
        if (_isApplyingOpacity)
            return;

        Visual.UpdateOpacity(eventArgs.NewValue);
        OpacityChanged?.Invoke(eventArgs.NewValue);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr window, int index);

    private static IntPtr GetWindowLongPtr(IntPtr window, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(window, index) : GetWindowLong32(window, index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr window, int index, IntPtr value);

    private static IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(window, index, value)
            : SetWindowLong32(window, index, value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
