using System.Globalization;
using System.IO;
using System.Text;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 简单的文件日志，写入 %LocalAppData%\YihangDrive\debug.log
/// 使用带 BOM 的 UTF-8 编码，避免中文乱码。
/// 启动时自动清理 7 天前的旧日志。
/// </summary>
public static class FileLogger
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YihangDrive");

    private static readonly string LogPath = Path.Combine(LogDir, "debug.log");

    private static readonly object Lock = new();

    /// <summary>UTF-8 带 BOM，确保任何工具/编辑器都能正确识别中文</summary>
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>
    /// 启动时调用：删除 7 天前的日志行，保留最近 7 天的内容。
    /// </summary>
    public static void CleanupOldLogs()
    {
        try
        {
            if (!File.Exists(LogPath)) return;

            // 如果整个文件最后修改时间超过 7 天，直接删除
            var lastWrite = File.GetLastWriteTime(LogPath);
            if ((DateTime.Now - lastWrite).TotalDays > 7)
            {
                File.Delete(LogPath);
                return;
            }

            var cutoff = DateTime.Now.AddDays(-7);
            var lines = File.ReadAllLines(LogPath, Utf8Bom);
            var kept = new List<string>();

            foreach (var line in lines)
            {
                // 新格式: [2025-06-15 10:30:45.123] message
                // 旧格式: [10:30:45.123] message（无日期，保留）
                if (line.Length >= 25 && line[0] == '[' &&
                    DateTime.TryParseExact(line.Substring(1, 23),
                        "yyyy-MM-dd HH:mm:ss.fff",
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.None,
                        out var lineDate))
                {
                    if (lineDate >= cutoff)
                        kept.Add(line);
                }
                else
                {
                    // 旧格式或无法解析的行，保留
                    kept.Add(line);
                }
            }

            // 只有确实裁剪了内容才重写文件
            if (kept.Count < lines.Length)
            {
                File.WriteAllLines(LogPath, kept, Utf8Bom);
            }
        }
        catch { }
    }

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(LogDir);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}\r\n",
                    Utf8Bom);
            }
        }
        catch { }
    }
}
