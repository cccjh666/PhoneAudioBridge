using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using PhoneAudioBridge.Services;

namespace PhoneAudioBridge;

/// <summary>系统托盘图标 + 右键菜单。图标颜色直接反映连接状态。</summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly AppSettings _settings;
    private readonly NotifyIcon _notify;
    private readonly ToolStripMenuItem _toggleItem;
    private Icon? _icon;
    private bool _disposed;

    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action? ToggleRequested;

    public TrayIconHost(AppSettings settings)
    {
        _settings = settings;

        ContextMenuStrip menu = new()
        {
            ShowImageMargin = false,
            Font = new Font("Microsoft YaHei UI", 9F)
        };

        ToolStripMenuItem showItem = new("打开面板") { Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold) };
        showItem.Click += (_, _) => ShowRequested?.Invoke();

        _toggleItem = new ToolStripMenuItem("连接手机");
        _toggleItem.Click += (_, _) => ToggleRequested?.Invoke();

        ToolStripMenuItem startupItem = new("开机自动启动")
        {
            CheckOnClick = true,
            Checked = AutoStart.IsEnabled()
        };
        startupItem.Click += (_, _) =>
        {
            bool ok = AutoStart.Set(startupItem.Checked);
            if (!ok)
            {
                startupItem.Checked = !startupItem.Checked;
                MessageBox.Show("写入开机启动项失败，请查看日志。", "手机音频桥",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _settings.RunAtStartup = startupItem.Checked;
            _settings.Save();
        };

        ToolStripMenuItem logItem = new("打开日志");
        logItem.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(Log.LogFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                    "\"" + Log.LogFolder + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("打开日志目录失败", ex);
            }
        };

        ToolStripMenuItem exitItem = new("退出");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(showItem);
        menu.Items.Add(_toggleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add(logItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notify = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true,
            Text = "手机音频桥"
        };
        _notify.DoubleClick += (_, _) => ShowRequested?.Invoke();

        UpdateForState(BridgeState.Idle, null);
    }

    public void UpdateForState(BridgeState state, string? deviceName)
    {
        if (_disposed) return;

        (Color color, string text) = state switch
        {
            BridgeState.Connected => (Color.FromArgb(0x12, 0xA1, 0x50), "已连接 · " + (deviceName ?? "手机")),
            BridgeState.Connecting => (Color.FromArgb(0xE8, 0xA3, 0x3D), "正在连接…"),
            BridgeState.Disconnecting => (Color.FromArgb(0x8E, 0x95, 0x9E), "正在断开…"),
            BridgeState.Error => (Color.FromArgb(0xE5, 0x48, 0x4D), "连接出错"),
            _ => (Color.FromArgb(0x8E, 0x95, 0x9E), "未连接")
        };

        ApplyIcon(color);

        _toggleItem.Text = state is BridgeState.Connected or BridgeState.Connecting
            ? "断开连接"
            : "连接手机";

        string tip = "手机音频桥 · " + text;
        _notify.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    }

    public void ShowTip(string title, string body, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (_disposed) return;

        try
        {
            _notify.BalloonTipTitle = title;
            _notify.BalloonTipText = body;
            _notify.BalloonTipIcon = icon;
            _notify.ShowBalloonTip(3000);
        }
        catch
        {
            // 气泡提示失败无所谓
        }
    }

    private void ApplyIcon(Color background)
    {
        Icon next = BuildIcon(background);
        Icon? previous = _icon;
        _icon = next;
        _notify.Icon = next;
        previous?.Dispose();
    }

    /// <summary>画一个带蓝牙符号的圆点图标，避免额外依赖 .ico 资源。</summary>
    private static Icon BuildIcon(Color background)
    {
        using Bitmap bitmap = new(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using (SolidBrush brush = new(background))
                g.FillEllipse(brush, 1.5f, 1.5f, 29f, 29f);

            using Pen pen = new(Color.White, 2.1f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
                LineJoin = LineJoin.Round
            };

            g.DrawLine(pen, 16f, 7.5f, 16f, 24.5f);
            g.DrawLines(pen, new[] { new PointF(16f, 7.5f), new PointF(22f, 12f), new PointF(16f, 16f) });
            g.DrawLines(pen, new[] { new PointF(16f, 16f), new PointF(22f, 20f), new PointF(16f, 24.5f) });
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notify.Visible = false;
        _notify.ContextMenuStrip?.Dispose();
        _notify.Dispose();
        _icon?.Dispose();
    }
}
