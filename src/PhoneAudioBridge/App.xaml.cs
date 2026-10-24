using System.Threading;
using System.Windows;
using PhoneAudioBridge.Services;

namespace PhoneAudioBridge;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppSettings _settings = null!;
    private BluetoothAudioService _bluetooth = null!;
    private VolumeService _volume = null!;
    private TrayIconHost? _tray;
    private MainWindow? _window;
    private bool _hideTipShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // ---- 单实例：第二次启动只提示，不再开一个托盘图标 ----
        _singleInstance = new Mutex(true, @"Local\PhoneAudioBridge.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("手机音频桥已经在运行了，请在系统托盘里打开它。", "手机音频桥",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("界面线程未处理异常", args.Exception);
            args.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("后台线程未处理异常", args.ExceptionObject as Exception);

        _settings = AppSettings.Load();
        Log.Info("======== 手机音频桥启动 ========");

        _bluetooth = new BluetoothAudioService { AutoReconnectEnabled = _settings.AutoReconnect };
        _volume = new VolumeService();
        _tray = new TrayIconHost(_settings);
        _window = new MainWindow(_settings, _bluetooth, _volume);

        _window.HideToTrayRequested += HideToTray;
        _tray.ShowRequested += ShowPanel;
        _tray.ExitRequested += ExitApp;
        _tray.ToggleRequested += ToggleConnection;
        _bluetooth.Changed += OnBluetoothChanged;

        bool startHidden = e.Args.Any(a => string.Equals(a, "--tray", StringComparison.OrdinalIgnoreCase));
        if (!startHidden)
            ShowPanel();

        // 无论是否显示窗口，都执行设备扫描 + 自动连接
        _ = _window.StartAsync();
    }

    private void OnBluetoothChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _tray?.UpdateForState(_bluetooth.State, _bluetooth.ActiveDevice?.Name);

            if (_bluetooth.State == BridgeState.Error)
                _tray?.ShowTip("手机音频桥", _bluetooth.StatusText, System.Windows.Forms.ToolTipIcon.Warning);
        }));
    }

    private void ShowPanel()
    {
        if (_window is null) return;

        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;

        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    private void HideToTray()
    {
        if (_window is null) return;

        _window.PersistState();
        _window.Hide();

        if (!_hideTipShown && _tray is not null)
        {
            _hideTipShown = true;
            _tray.ShowTip("手机音频桥仍在后台运行",
                "程序已经收进托盘。要真正退出，请右键托盘图标选择「退出」。");
        }
    }

    private async void ToggleConnection()
    {
        try
        {
            if (_bluetooth.State is BridgeState.Connected or BridgeState.Connecting)
            {
                _bluetooth.Disconnect();
                return;
            }

            if (_window is not null)
                await _window.ConnectBestAsync();
        }
        catch (Exception ex)
        {
            Log.Error("托盘切换连接失败", ex);
        }
    }

    private void ExitApp()
    {
        Log.Info("用户选择退出程序");
        _window?.PersistState();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _window?.PersistState();
            _tray?.Dispose();
            _bluetooth?.Dispose();
            _volume?.Dispose();
            _singleInstance?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("退出清理时出错", ex);
        }

        Log.Info("======== 已退出 ========");
        base.OnExit(e);
    }
}
