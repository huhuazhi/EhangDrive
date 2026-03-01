using System.IO;
using System.Text;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 简单的文件日志，写入 %LocalAppData%\YihangDrive\debug.log
/// 使用带 BOM 的 UTF-8 编码，避免中文乱码。
/// </summary>
public static class FileLogger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YihangDrive", "debug.log");

    private static readonly object Lock = new();

    /// <summary>UTF-8 带 BOM，确保任何工具/编辑器都能正确识别中文</summary>
    private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n",
                    Utf8Bom);
            }
        }
        catch { }
    }
}
