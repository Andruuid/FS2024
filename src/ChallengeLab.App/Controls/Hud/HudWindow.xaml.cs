using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ChallengeLab.App.Controls.Hud;

/// <summary>Transparent, non-activating overlay that tracks the active MSFS client area.</summary>
public partial class HudWindow : Window
{
    private const int WindowStyleIndex = -20;
    private const long ExtendedTransparent = 0x00000020L;
    private const long ExtendedToolWindow = 0x00000080L;
    private const long ExtendedNoActivate = 0x08000000L;
    private const uint NoActivate = 0x0010;
    private const uint ShowWindow = 0x0040;
    private static readonly IntPtr TopmostWindow = new(-1);

    private readonly SimulatorWindowTracker _tracker = new();
    private readonly HudViewGate _viewGate = new();
    private readonly DispatcherTimer _placementTimer;
    private bool _userVisible;
    private bool _viewAllowed;
    private bool _hasBeenShown;

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
    }

    internal void SetUserVisible(bool visible)
    {
        _userVisible = visible;
        SynchronizePlacement();
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
        var handle = new WindowInteropHelper(this).Handle;
        var styles = GetWindowLongPtr(handle, WindowStyleIndex).ToInt64();
        SetWindowLongPtr(
            handle,
            WindowStyleIndex,
            new IntPtr(styles | ExtendedTransparent | ExtendedToolWindow | ExtendedNoActivate));
    }

    private void OnClosed(object? sender, EventArgs eventArgs)
    {
        _placementTimer.Stop();
        _placementTimer.Tick -= OnPlacementTick;
    }

    private void OnPlacementTick(object? sender, EventArgs eventArgs) => SynchronizePlacement();

    private void SynchronizePlacement()
    {
        if (!_userVisible
            || !_viewAllowed
            || !_tracker.TryGetActiveClientBounds(out var bounds))
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
