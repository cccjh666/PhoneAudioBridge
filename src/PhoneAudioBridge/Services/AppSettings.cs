using System.IO;
using System.Text.Json;

namespace PhoneAudioBridge.Services;

/// <summary>应用设置，落盘到 %LOCALAPPDATA%\PhoneAudioBridge\settings.json</summary>
public sealed class AppSettings
{
    /// <summary>连接意外中断后自动尝试恢复。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>程序启动时自动连接上次使用的设备。</summary>
    public bool AutoConnectOnStart { get; set; } = true;

    /// <summary>开机自启（写 HKCU Run 项）。</summary>
    public bool RunAtStartup { get; set; } = false;

    /// <summary>连接成功后把输出音量恢复到上次记录的值。</summary>
    public bool RestoreVolume { get; set; } = true;

    /// <summary>上次连接的设备 Id。</summary>
    public string? LastDeviceId { get; set; }

    /// <summary>上次使用的音量（0-100），-1 表示未记录。</summary>
    public int LastVolume { get; set; } = -1;

    /// <summary>关闭窗口时最小化到托盘而不是退出。</summary>
    public bool CloseToTray { get; set; } = true;

    // ---------- 持久化 ----------

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string FolderPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneAudioBridge");

    public static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("读取设置失败，已使用默认值：" + ex.Message);
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Warn("保存设置失败：" + ex.Message);
        }
    }
}
