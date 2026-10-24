using System.IO;
using System.Text;

namespace PhoneAudioBridge.Services;

/// <summary>极简文件日志，按天分文件，方便排查“连上了却没声音”这类问题。</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static string LogFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhoneAudioBridge", "logs");

    public static string CurrentFile => Path.Combine(LogFolder, $"app-{DateTime.Now:yyyyMMdd}.log");

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
    {
        StringBuilder sb = new(message);
        if (ex is not null)
        {
            sb.Append(" :: ").Append(ex.GetType().Name).Append(": ").Append(ex.Message);
            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
                sb.Append(Environment.NewLine).Append(ex.StackTrace);
        }

        Write("ERROR", sb.ToString());
    }

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogFolder);
                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
                File.AppendAllText(CurrentFile, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不能影响主流程
        }
    }
}
