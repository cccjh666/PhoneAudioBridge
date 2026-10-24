using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneAudioBridge.Services;

namespace PhoneAudioBridge;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly BluetoothAudioService _bluetooth;
    private readonly VolumeService _volume;
    private readonly ObservableCollection<AudioDevice> _devices = new();

    private bool _suppressVolumeEvent;
    private bool _busy;

    // ---- 音频流监测：用来识别“连着但手机没送声音”的情况 ----
    private readonly DispatcherTimer _meterTimer;
    private DateTime _lastAudioAt = DateTime.Now;
    private bool _hasSeenAudio;
    private bool _silentHintVisible;

    private const float SilencePeakThreshold = 0.002f;
    private static readonly TimeSpan SilenceWarnAfter = TimeSpan.FromSeconds(60);

    /// <summary>请求把窗口收进托盘（由 App 决定是否真的退出）。</summary>
    public event Action? HideToTrayRequested;

    public MainWindow(AppSettings settings, BluetoothAudioService bluetooth, VolumeService volume)
    {
        InitializeComponent();

        _settings = settings;
        _bluetooth = bluetooth;
        _volume = volume;

        DeviceList.ItemsSource = _devices;

        _bluetooth.Changed += OnBluetoothChanged;
        _volume.Changed += OnVolumeChanged;

        AutoReconnectSwitch.IsChecked = _settings.AutoReconnect;
        AutoConnectSwitch.IsChecked = _settings.AutoConnectOnStart;
        StartupSwitch.IsChecked = AutoStart.IsEnabled();
        _bluetooth.AutoReconnectEnabled = _settings.AutoReconnect;

        _meterTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _meterTimer.Tick += OnMeterTick;
        _meterTimer.Start();

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _bluetooth.Changed -= OnBluetoothChanged;
            _volume.Changed -= OnVolumeChanged;
            _meterTimer.Stop();
        };
    }

    // ------------------------------------------------------------ 音频流监测

    private void OnMeterTick(object? sender, EventArgs e)
    {
        if (_bluetooth.State != BridgeState.Connected)
        {
            _hasSeenAudio = false;
            _lastAudioAt = DateTime.Now;
            SetSilentHint(false);
            return;
        }

        if (_volume.PeakValue > SilencePeakThreshold)
        {
            _hasSeenAudio = true;
            _lastAudioAt = DateTime.Now;
            SetSilentHint(false);
            return;
        }

        // 只有在本次连接里确实听到过声音、之后又断了，才提示。
        // 这样即使某些声卡不支持峰值计量、读数恒为 0，也不会误报。
        if (!_hasSeenAudio) return;

        if (DateTime.Now - _lastAudioAt >= SilenceWarnAfter)
            SetSilentHint(true);
    }

    private void SetSilentHint(bool show)
    {
        if (_silentHintVisible == show) return;

        _silentHintVisible = show;
        SilentHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

        if (show)
        {
            Log.Info(_hasSeenAudio
                ? "已连接但音频流中断超过 60 秒（可能手机进入了通话）"
                : "已连接但 60 秒内始终没有音频流");
        }
    }

    // ------------------------------------------------------------ 启动

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Win11Visuals.TryApplyRoundedCorners(this);
        RenderState();
        RefreshVolumeUi();
    }

    /// <summary>由 App 在启动时调用：扫描设备并按设置自动连接上次的设备。</summary>
    public async Task StartAsync()
    {
        RenderState();
        RefreshVolumeUi();

        await RefreshDevicesAsync();

        if (_settings.AutoConnectOnStart && !string.IsNullOrWhiteSpace(_settings.LastDeviceId))
        {
            AudioDevice? last = _devices.FirstOrDefault(d => d.Id == _settings.LastDeviceId);
            if (last is not null)
            {
                await ConnectAsync(last);
                return;
            }
        }

        RenderState();
    }

    /// <summary>供托盘菜单使用：连接上次设备，没有记录就连第一个。</summary>
    public async Task ConnectBestAsync()
    {
        if (_devices.Count == 0)
            await RefreshDevicesAsync();

        AudioDevice? target = _devices.FirstOrDefault(d => d.Id == _settings.LastDeviceId)
                              ?? _devices.FirstOrDefault();

        if (target is null)
        {
            EmptyTip.Visibility = Visibility.Visible;
            return;
        }

        await ConnectAsync(target);
    }

    // ------------------------------------------------------------ 设备

    private async Task RefreshDevicesAsync()
    {
        try
        {
            DeviceHint.Text = "扫描中…";
            IReadOnlyList<AudioDevice> found = await _bluetooth.DiscoverAsync();

            _devices.Clear();
            foreach (AudioDevice d in found) _devices.Add(d);

            EmptyTip.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DeviceHint.Text = _devices.Count == 0 ? "" : $"{_devices.Count} 台";
        }
        catch (Exception ex)
        {
            DeviceHint.Text = "扫描失败";
            EmptyTip.Visibility = Visibility.Visible;
            Log.Error("刷新设备列表失败", ex);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await RefreshDevicesAsync();
    }

    private async void DeviceConnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AudioDevice device })
            await ConnectAsync(device);
    }

    private async Task ConnectAsync(AudioDevice device)
    {
        if (_busy) return;
        _busy = true;

        try
        {
            // 切换默认设备后音量端点会变，连接前重新绑定一次
            _volume.RefreshDevice();

            bool ok = await _bluetooth.ConnectAsync(device);
            if (ok)
            {
                _settings.LastDeviceId = device.Id;

                if (_settings.RestoreVolume && _settings.LastVolume >= 0)
                {
                    _volume.RefreshDevice();
                    _volume.SetVolume(_settings.LastVolume);
                }

                _settings.Save();
                RefreshVolumeUi();
            }
        }
        catch (Exception ex)
        {
            Log.Error("连接流程异常", ex);
        }
        finally
        {
            _busy = false;
            RenderState();
        }
    }

    // ------------------------------------------------------------ 主操作按钮

    private async void ActionButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_bluetooth.State)
        {
            case BridgeState.Connected:
            case BridgeState.Connecting:
                _bluetooth.Disconnect();
                RenderState();
                break;

            case BridgeState.Idle:
            case BridgeState.Error:
                await ConnectBestAsync();
                break;

            default:
                break;
        }
    }

    // ------------------------------------------------------------ 状态渲染

    private void OnBluetoothChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnBluetoothChanged(sender, e)));
            return;
        }

        RenderState();
    }

    private void RenderState()
    {
        (Brush dot, string title, string subtitle, string action, bool actionEnabled) presentation =
            _bluetooth.State switch
            {
                BridgeState.Connected => (
                    (Brush)FindResource("OkBrush"),
                    "已连接",
                    _bluetooth.ActiveDevice?.Name ?? "手机音频正在这台电脑上播放",
                    "断开",
                    true),

                BridgeState.Connecting => (
                    (Brush)FindResource("WarnBrush"),
                    "正在连接",
                    _bluetooth.StatusText,
                    "取消",
                    true),

                BridgeState.Disconnecting => (
                    (Brush)FindResource("IdleBrush"),
                    "正在断开",
                    _bluetooth.StatusText,
                    "请稍候",
                    false),

                BridgeState.Error => (
                    (Brush)FindResource("ErrBrush"),
                    "需要处理",
                    _bluetooth.StatusText,
                    "重试",
                    true),

                _ => (
                    (Brush)FindResource("IdleBrush"),
                    "未连接",
                    "选择下面列出的手机，把它的声音接到这台电脑播放。",
                    "连接",
                    true)
            };

        StateDot.Fill = presentation.dot;
        StateTitle.Text = presentation.title;
        StateSubtitle.Text = presentation.subtitle;
        ActionButton.Content = presentation.action;
        ActionButton.IsEnabled = presentation.actionEnabled;

        if (_bluetooth.State != BridgeState.Connected)
            SetSilentHint(false);
    }

    // ------------------------------------------------------------ 音量

    private void OnVolumeChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => OnVolumeChanged(sender, e)));
            return;
        }

        RefreshVolumeUi();
    }

    private void RefreshVolumeUi()
    {
        _volume.RefreshDevice();

        _suppressVolumeEvent = true;
        try
        {
            int percent = _volume.VolumePercent;
            VolumeSlider.Value = percent;
            VolumeText.Text = percent + "%";

            bool muted = _volume.IsMuted;
            MuteGlyph.Text = muted ? "\uE74F" : "\uE767";
            MuteButton.ToolTip = muted ? "取消静音" : "静音";
            MuteGlyph.Foreground = muted
                ? (Brush)FindResource("ErrBrush")
                : (Brush)FindResource("MutedBrush");

            OutputDeviceText.Text = "输出到：" + _volume.DeviceName;
        }
        catch (Exception ex)
        {
            Log.Warn("刷新音量界面失败：" + ex.Message);
        }
        finally
        {
            _suppressVolumeEvent = false;
        }
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        int value = (int)Math.Round(e.NewValue);
        VolumeText.Text = value + "%";

        if (_suppressVolumeEvent) return;

        _volume.SetVolume(value);
        _settings.LastVolume = value;
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _volume.SetMute(!_volume.IsMuted);
        RefreshVolumeUi();
    }

    // ------------------------------------------------------------ 设置项

    private void AutoReconnect_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoReconnect = AutoReconnectSwitch.IsChecked == true;
        _bluetooth.AutoReconnectEnabled = _settings.AutoReconnect;
        _settings.Save();
    }

    private void AutoConnect_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoConnectOnStart = AutoConnectSwitch.IsChecked == true;
        _settings.Save();
    }

    private void Startup_Click(object sender, RoutedEventArgs e)
    {
        bool want = StartupSwitch.IsChecked == true;
        bool ok = AutoStart.Set(want);

        if (!ok)
        {
            StartupSwitch.IsChecked = !want;
            MessageBox.Show(this, "写入开机启动项失败，请查看日志。", "手机音频桥",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _settings.RunAtStartup = want;
        _settings.Save();
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Log.LogFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Log.LogFolder + "\"")
            {
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Log.Error("打开日志目录失败", ex);
        }
    }

    // ------------------------------------------------------------ 窗口行为

    private void Minimize_Click(object sender, RoutedEventArgs e) => HideToTrayRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e) => HideToTrayRequested?.Invoke();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 托盘应用：关闭窗口只收进托盘，真正退出走托盘菜单
        if (_settings.CloseToTray)
        {
            e.Cancel = true;
            HideToTrayRequested?.Invoke();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>退出前把当前音量记下来，下次连上自动恢复。</summary>
    public void PersistState()
    {
        try
        {
            _settings.LastVolume = _volume.VolumePercent;
            _settings.Save();
        }
        catch
        {
            // 忽略
        }
    }
}
