using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Point = System.Windows.Point;

namespace ChallengeLab.App.Controls.Aether;

/// <summary>Transparent, non-activating Aether overlay locked to the MSFS client area.</summary>
public partial class AetherOverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    private const long WsExNoActivate = 0x08000000L;
    private const int WmNcHitTest = 0x0084;
    private const int WmMouseActivate = 0x0021;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const int MaNoActivate = 3;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private readonly AetherSimViewport _viewport = new();
    private readonly AetherLookPolicy _look = new();
    private readonly DispatcherTimer _placeTimer;
    private HwndSource? _hwndSource;
    private bool _userVisible;
    private bool _lookAllows;
    private bool _hasShown;
    private bool _syncingScale;
    private bool _syncingOpacity;

    public AetherOverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
        _placeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160),
        };
        _placeTimer.Tick += (_, _) => SyncPlacement();
        _placeTimer.Start();
        ApplyScale(1.0);
        ApplyOpacity(0.96);
    }

    internal event Action<double>? ScaleChanged;
    internal event Action<double>? OpacityChanged;

    internal void SetUserVisible(bool visible)
    {
        _userVisible = visible;
        SyncPlacement();
    }

    internal void ApplyScale(double scale)
    {
        var bounded = Math.Clamp(scale, ScaleSlider.Minimum, ScaleSlider.Maximum);
        Surface.SetDisplayScale(bounded);
        _syncingScale = true;
        try
        {
            ScaleSlider.Value = bounded;
        }
        finally
        {
            _syncingScale = false;
        }
    }

    internal void ApplyOpacity(double opacity)
    {
        var bounded = Math.Clamp(opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        Surface.SetDisplayOpacity(bounded);
        _syncingOpacity = true;
        try
        {
            OpacitySlider.Value = bounded;
        }
        finally
        {
            _syncingOpacity = false;
        }
    }

    internal void ApplySnapshot(AetherSnapshot? snapshot)
    {
        Surface.ApplySnapshot(snapshot);
        var allows = _look.ShouldRender(snapshot);
        if (_lookAllows == allows)
            return;

        _lookAllows = allows;
        SyncPlacement();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource)PresentationSource.FromVisual(this)!;
        _hwndSource.AddHook(WndProc);
        var handle = _hwndSource.Handle;
        var styles = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(styles | WsExToolWindow | WsExNoActivate));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _placeTimer.Stop();
        if (_hwndSource is not null)
        {
            _hwndSource.RemoveHook(WndProc);
            _hwndSource = null;
        }
    }

    private void SyncPlacement()
    {
        if (!_userVisible || !_lookAllows || !_viewport.TryGetClientBounds(out var bounds))
        {
            if (_hasShown && IsVisible)
                Hide();
            return;
        }

        if (!_hasShown)
        {
            _hasShown = true;
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
            HwndTopmost,
            bounds.Left,
            bounds.Top,
            bounds.Width,
            bounds.Height,
            SwpNoActivate | SwpShowWindow);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNcHitTest)
        {
            var packed = lParam.ToInt64();
            var screen = new Point(
                unchecked((short)(packed & 0xffff)),
                unchecked((short)((packed >> 16) & 0xffff)));
            handled = true;
            return IsOverChrome(screen)
                ? new IntPtr(HtClient)
                : new IntPtr(HtTransparent);
        }

        if (msg == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        return IntPtr.Zero;
    }

    private bool IsOverChrome(Point screenPoint)
    {
        if (!Chrome.IsVisible || Chrome.ActualWidth <= 0 || Chrome.ActualHeight <= 0)
            return false;

        try
        {
            var local = Chrome.PointFromScreen(screenPoint);
            return local.X >= 0 && local.Y >= 0
                   && local.X <= Chrome.ActualWidth
                   && local.Y <= Chrome.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingScale)
            return;
        Surface.SetDisplayScale(e.NewValue);
        ScaleChanged?.Invoke(e.NewValue);
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncingOpacity)
            return;
        Surface.SetDisplayOpacity(e.NewValue);
        OpacityChanged?.Invoke(e.NewValue);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern IntPtr GetWindowLong32(IntPtr hWnd, int nIndex);

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(hWnd, nIndex) : GetWindowLong32(hWnd, nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern IntPtr SetWindowLong32(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong) =>
        IntPtr.Size == 8
            ? SetWindowLongPtr64(hWnd, nIndex, dwNewLong)
            : SetWindowLong32(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd,
        IntPtr hWndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        uint uFlags);
}
