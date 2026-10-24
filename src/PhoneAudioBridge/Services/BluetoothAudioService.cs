using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace PhoneAudioBridge.Services;

public enum BridgeState
{
    /// <summary>没有连接，也没有在尝试。</summary>
    Idle,

    /// <summary>正在启动连接（含自动重连等待）。</summary>
    Connecting,

    /// <summary>已建立音频通道。</summary>
    Connected,

    /// <summary>正在断开。</summary>
    Disconnecting,

    /// <summary>出错了，需要人工介入。</summary>
    Error
}

public sealed record AudioDevice(string Id, string Name);

/// <summary>
/// 封装 Windows.Media.Audio.AudioPlaybackConnection：让本机作为 A2DP Sink 接收手机推来的音频。
///
/// 与商店版接收器相比，这里做了三件事：
/// 1. 用 OpenAsync 返回的 AudioPlaybackConnectionOpenResult 区分超时 / 被系统拒绝 / 未知失败，给出可操作提示；
/// 2. 断线后用指数退避自动重连；
/// 3. 每次重连前彻底释放旧连接对象，避免残留的 COM 状态让后续连接直接失败。
/// </summary>
public sealed class BluetoothAudioService : IDisposable
{
    private readonly object _gate = new();

    private AudioPlaybackConnection? _conn;
    private bool _userWantsOn;
    private bool _connecting;
    private int _retryCount;
    private CancellationTokenSource? _retryCts;
    private bool _disposed;

    public bool AutoReconnectEnabled { get; set; } = true;

    public BridgeState State { get; private set; } = BridgeState.Idle;

    public AudioDevice? ActiveDevice { get; private set; }

    public string StatusText { get; private set; } = "未连接";

    /// <summary>状态变化时触发，可能在后台线程，UI 需自行切回主线程。</summary>
    public event EventHandler? Changed;

    // ---------------------------------------------------------------- 设备枚举

    /// <summary>列出本机可以充当音频接收端的已配对设备。</summary>
    public async Task<IReadOnlyList<AudioDevice>> DiscoverAsync()
    {
        List<AudioDevice> list = new();
        try
        {
            string selector = AudioPlaybackConnection.GetDeviceSelector();
            DeviceInformationCollection found = await DeviceInformation.FindAllAsync(selector);

            foreach (DeviceInformation info in found)
            {
                string name = string.IsNullOrWhiteSpace(info.Name) ? "未命名设备" : info.Name.Trim();
                list.Add(new AudioDevice(info.Id, name));
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举音频接收设备失败", ex);
            throw;
        }

        Log.Info($"发现 {list.Count} 个可接收设备：{string.Join(" / ", list.ConvertAll(d => d.Name))}");
        return list;
    }

    // ---------------------------------------------------------------- 连接

    public async Task<bool> ConnectAsync(AudioDevice device)
    {
        if (_disposed) return false;

        _userWantsOn = true;
        _retryCount = 0;
        CancelPendingRetry();

        return await ConnectCoreAsync(device);
    }

    private async Task<bool> ConnectCoreAsync(AudioDevice device)
    {
        if (_disposed) return false;

        _connecting = true;
        try
        {
            CloseConnectionQuietly();

            ActiveDevice = device;
            SetState(BridgeState.Connecting, $"正在连接 {device.Name} …");

            AudioPlaybackConnection? conn = AudioPlaybackConnection.TryCreateFromId(device.Id);
            if (conn is null)
            {
                SetState(BridgeState.Error,
                    "系统无法为该设备建立音频通道。请先在「设置 → 蓝牙和其他设备」里确认它已与这台电脑配对。");
                Log.Warn("TryCreateFromId 返回 null：" + device.Id);
                return false;
            }

            lock (_gate) { _conn = conn; }
            conn.StateChanged += OnConnectionStateChanged;

            // Start 让系统接受来自手机的连接；Open 真正把音频通道拉起来。
            await conn.StartAsync();
            AudioPlaybackConnectionOpenResult result = await conn.OpenAsync();

            Log.Info("OpenAsync 结果：" + result.Status);

            switch (result.Status)
            {
                case AudioPlaybackConnectionOpenResultStatus.Success:
                    _retryCount = 0;
                    SetState(BridgeState.Connected, $"已连接 · {device.Name}");
                    return true;

                case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut:
                    SetState(BridgeState.Error,
                        "手机没有在限时内响应。请确认手机蓝牙已打开且在附近，然后在手机上重新播放一次。");
                    return false;

                case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem:
                    SetState(BridgeState.Error,
                        "系统拒绝了这次连接。通常是这台手机正被其它电脑或旧连接占用，先在手机蓝牙设置里断开旧连接再试。");
                    return false;

                default:
                    string reason = result.ExtendedError?.Message ?? "未知原因";
                    SetState(BridgeState.Error, "连接失败：" + reason);
                    return false;
            }
        }
        catch (Exception ex)
        {
            Log.Error("连接失败", ex);
            SetState(BridgeState.Error, "连接出错：" + FriendlyError(ex));
            return false;
        }
        finally
        {
            _connecting = false;
        }
    }

    // ---------------------------------------------------------------- 断开

    /// <summary>用户主动断开：不会触发自动重连。</summary>
    public void Disconnect()
    {
        _userWantsOn = false;
        CancelPendingRetry();

        SetState(BridgeState.Disconnecting, "正在断开 …");
        CloseConnectionQuietly();
        Log.Info("用户主动断开连接");
        SetState(BridgeState.Idle, "已断开");
    }

    // ---------------------------------------------------------------- 状态与自动重连

    private void OnConnectionStateChanged(AudioPlaybackConnection sender, object args)
    {
        AudioPlaybackConnectionState native;
        try { native = sender.State; }
        catch (Exception ex)
        {
            Log.Warn("读取连接状态失败：" + ex.Message);
            return;
        }

        Log.Info("连接状态变化：" + (native == AudioPlaybackConnectionState.Opened ? "Opened" : "Closed"));

        string deviceName = ActiveDevice?.Name ?? "设备";

        if (native == AudioPlaybackConnectionState.Opened)
        {
            _retryCount = 0;
            SetState(BridgeState.Connected, $"已连接 · {deviceName}");
            return;
        }

        // Closed
        if (_connecting)
        {
            // 连接过程中通道闪断，交给 OpenAsync 的返回值判定，不在事件里抢答
            return;
        }

        if (_userWantsOn && AutoReconnectEnabled)
        {
            Log.Warn("连接意外中断，准备自动重连");
            ScheduleReconnect();
        }
        else if (_userWantsOn)
        {
            SetState(BridgeState.Idle, "连接已断开（自动重连已关闭，可手动重连）");
        }
        else
        {
            SetState(BridgeState.Idle, "已断开");
        }
    }

    private void ScheduleReconnect()
    {
        if (_disposed) return;

        AudioDevice? device = ActiveDevice;
        if (device is null) return;

        _retryCount++;

        int delayMs;

        if (_retryCount <= 6)
        {
            // 快速阶段：1.5s → 3s → 6s → 12s → 24s → 30s，避免高频重试把适配器拖垮
            delayMs = Math.Min(30000, 1500 * (int)Math.Pow(2, _retryCount - 1));
            SetState(BridgeState.Connecting, $"连接中断，{delayMs / 1000.0:0.#} 秒后自动重试（第 {_retryCount}/6 次）…");
        }
        else if (_retryCount <= 40)
        {
            // 慢速阶段：多半是手机正在语音/视频通话，链路被 HFP 抢走。
            // 通话结束后 A2DP 会自己回来，所以这里不放弃，改成低频探测。
            delayMs = 30000;
            SetState(BridgeState.Connecting,
                "仍未恢复，改为每 30 秒探测一次。如果手机正在语音或视频通话，通话结束后通常会自动恢复。");
        }
        else
        {
            SetState(BridgeState.Error,
                "连续重试约 20 分钟仍未恢复，已停止自动重连。请手动重连，或在手机上关闭再打开蓝牙。");
            return;
        }


        CancellationTokenSource cts = new();
        _retryCts = cts;
        CancellationToken token = cts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested || _disposed || !_userWantsOn) return;

            AudioDevice? target = ActiveDevice;
            if (target is null) return;

            Log.Info($"自动重连第 {_retryCount} 次：{target.Name}");
            await ConnectCoreAsync(target);
        });
    }

    private void CancelPendingRetry()
    {
        try
        {
            _retryCts?.Cancel();
            _retryCts?.Dispose();
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _retryCts = null;
        }
    }

    // ---------------------------------------------------------------- 内部工具

    private void SetState(BridgeState state, string text)
    {
        State = state;
        StatusText = text;
        Log.Info($"状态 → {state}：{text}");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void CloseConnectionQuietly()
    {
        AudioPlaybackConnection? conn;
        lock (_gate)
        {
            conn = _conn;
            _conn = null;
        }

        if (conn is null) return;

        try
        {
            conn.StateChanged -= OnConnectionStateChanged;
            // IClosable 在 .NET 投影里就是 Dispose()
            conn.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("释放旧连接时出错：" + ex.Message);
        }
    }

    private static string FriendlyError(Exception ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code switch
        {
            0x05 => "拒绝访问。请检查蓝牙适配器是否被其它程序独占，或尝试重启蓝牙服务。",
            0x1F => "设备当前不可用，请确认手机蓝牙已打开并在这台电脑附近。",
            0x39 => "设备忙，可能已被其它程序连接。",
            _ => ex.Message
        };
    }

    public void Dispose()
    {
        _disposed = true;
        _userWantsOn = false;
        CancelPendingRetry();
        CloseConnectionQuietly();
    }
}
