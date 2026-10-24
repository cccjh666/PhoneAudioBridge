using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhoneAudioBridge;

/// <summary>让无边框窗口在 Windows 11 上获得系统圆角（低版本系统上静默跳过）。</summary>
internal static class Win11Visuals
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmWindowCornerRound = 2;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void TryApplyRoundedCorners(Window window)
    {
        try
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            int preference = DwmWindowCornerRound;
            DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // 系统不支持就算了
        }
    }
}
