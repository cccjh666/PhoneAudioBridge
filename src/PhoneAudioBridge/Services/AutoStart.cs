using Microsoft.Win32;

namespace PhoneAudioBridge.Services;

/// <summary>通过 HKCU Run 项实现开机自启（不需要管理员权限）。</summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PhoneAudioBridge";

    public static string? ExecutablePath => Environment.ProcessPath;

    public static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            string? value = key?.GetValue(ValueName) as string;
            return !string.IsNullOrWhiteSpace(value);
        }
        catch (Exception ex)
        {
            Log.Warn("读取开机自启项失败：" + ex.Message);
            return false;
        }
    }

    public static bool Set(bool enable)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true)
                                    ?? throw new InvalidOperationException("无法打开启动项注册表键。");

            if (enable)
            {
                string? exe = ExecutablePath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    Log.Warn("无法定位程序路径，开机自启未设置。");
                    return false;
                }

                key.SetValue(ValueName, "\"" + exe + "\" --tray");
                Log.Info("已设置开机自启：" + exe);
            }
            else
            {
                key.DeleteValue(ValueName, false);
                Log.Info("已取消开机自启");
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机自启失败", ex);
            return false;
        }
    }
}
