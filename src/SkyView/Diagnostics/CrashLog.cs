using System;
using System.IO;
using System.Text;

namespace SkyView.Diagnostics;

/// <summary>
/// 崩溃日志。WinUI 启动期异常通常表现为 0xC000027B（stowed exception），
/// 事件日志里看不到托管堆栈，所以把未处理异常落到 %LOCALAPPDATA%\SkyView\crash.log。
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SkyView",
        "crash.log");

    public static void Write(string source, Exception? ex)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}");
            if (ex is not null)
            {
                sb.AppendLine(ex.GetType().FullName);
                sb.AppendLine(ex.Message);
                sb.AppendLine(ex.ToString());
            }
            sb.AppendLine(new string('-', 60));

            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, sb.ToString());
            }
        }
        catch
        {
            // 日志失败不能再次抛出，否则会递归崩溃
        }
    }
}
