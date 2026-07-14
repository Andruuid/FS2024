using System.Windows;
using System.Windows.Interop;
using ChallengeLab.App.ViewModels;
using ChallengeLab.SimConnect;

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

        _vm.RequestConnect += ConnectToSim;
        _vm.RequestShowHud += ShowHud;
        _vm.RequestActivateMain += () =>
        {
            Activate();
            WindowState = WindowState.Normal;
        };

        Loaded += OnLoaded;
        Closed += OnClosed;
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
            _hud = new CompanionHudWindow(_vm)
            {
                Owner = this
            };
            _hud.Closed += (_, _) => _hud = null;
        }

        if (!_hud.IsVisible)
            _hud.Show();
        _hud.Activate();
    }
}
