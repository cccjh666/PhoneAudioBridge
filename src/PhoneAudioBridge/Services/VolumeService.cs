using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace PhoneAudioBridge.Services;

/// <summary>
/// 直接操作“当前默认播放设备”的端点音量。
/// 不走 Windows 音量合成器的界面，避免出现拖动无效、回弹、数值不同步的问题。
/// </summary>
public sealed class VolumeService : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private bool _subscribed;
    private bool _applying;
    private DateTime _lastNotifyLog = DateTime.MinValue;

    /// <summary>音量被外部改变（例如键盘音量键）时触发。</summary>
    public event EventHandler? Changed;

    public VolumeService()
    {
        RefreshDevice();
    }

    public string DeviceName => _device?.FriendlyName ?? "未找到播放设备";

    public bool HasDevice => _device is not null;

    /// <summary>当前音量百分比 0-100。</summary>
    public int VolumePercent
    {
        get
        {
            try
            {
                if (_device is null) return 0;
                float scalar = _device.AudioEndpointVolume.MasterVolumeLevelScalar;
                return (int)Math.Round(Math.Clamp(scalar, 0f, 1f) * 100f);
            }
            catch
            {
                return 0;
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            try { return _device?.AudioEndpointVolume.Mute ?? false; }
            catch { return false; }
        }
    }

    /// <summary>
    /// 当前输出设备的实时峰值电平（0-1）。
    /// 用来判断“已连接但手机没有送音频过来”这种状态。
    /// </summary>
    public float PeakValue
    {
        get
        {
            try { return _device?.AudioMeterInformation.MasterPeakValue ?? 0f; }
            catch { return 0f; }
        }
    }

    /// <summary>重新获取默认播放设备（用户可能切换了耳机/扬声器）。</summary>
    public void RefreshDevice()
    {
        try
        {
            Unsubscribe();

            MMDevice? fresh = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (fresh is null) return;

            // 默认设备没变就复用
            if (_device is not null && _device.ID == fresh.ID)
            {
                fresh.Dispose();
                Subscribe();
                return;
            }

            _device?.Dispose();
            _device = fresh;
            Subscribe();
            Log.Info("当前输出设备：" + DeviceName);
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn("获取默认播放设备失败：" + ex.Message);
        }
    }

    public void SetVolume(int percent)
    {
        try
        {
            if (_device is null) return;
            float scalar = Math.Clamp(percent, 0, 100) / 100f;
            _applying = true;
            try
            {
                _device.AudioEndpointVolume.MasterVolumeLevelScalar = scalar;
                // 主动调大音量时顺手取消静音，避免“拖了滑块还是没声”
                if (percent > 0 && _device.AudioEndpointVolume.Mute)
                    _device.AudioEndpointVolume.Mute = false;
            }
            finally
            {
                _applying = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("设置音量失败：" + ex.Message);
        }
    }

    public void SetMute(bool mute)
    {
        try
        {
            if (_device is null) return;
            _applying = true;
            try { _device.AudioEndpointVolume.Mute = mute; }
            finally { _applying = false; }
        }
        catch (Exception ex)
        {
            Log.Warn("设置静音失败：" + ex.Message);
        }
    }

    private void Subscribe()
    {
        if (_device is null || _subscribed) return;
        try
        {
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
            _subscribed = true;
        }
        catch (Exception ex)
        {
            Log.Warn("订阅音量变化失败：" + ex.Message);
        }
    }

    private void Unsubscribe()
    {
        if (_device is null || !_subscribed) return;
        try
        {
            _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification;
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _subscribed = false;
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_applying) return;

        // 走到这里说明音量是被外部改掉的：键盘音量键，或者手机通过绝对音量命令改的
        if ((DateTime.Now - _lastNotifyLog).TotalSeconds >= 1)
        {
            _lastNotifyLog = DateTime.Now;
            int percent = (int)Math.Round(Math.Clamp(data.MasterVolume, 0f, 1f) * 100f);
            Log.Info($"端点音量被外部改变 → {percent}%{(data.Muted ? "（静音）" : string.Empty)}");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        Unsubscribe();
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
