using System.IO;
using System.Runtime.InteropServices;
using EhangNAS_Sync.Models;
using static EhangNAS_Sync.Native.CldApi;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 定时轮询服务端 modlist 接口，检测云端文件变更。
/// 对比本地已 hydrated 的文件：
///   - 云端更新(action=update, cloudMtime > localMtime) → 脱水本地文件，下次打开自动拉最新
///   - 本地更新(action=update, localMtime > cloudMtime) → 异步上传本地文件
///   - 云端删除(action=delete) → 删除本地 placeholder
/// </summary>
public sealed class ModListPollingService : IDisposable
{
    private readonly SyncApiService _api;
    private readonly string _syncFolder;
    private readonly SyncEngine _syncEngine;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pollingTask;

    /// <summary>
    /// 轮询间隔（毫秒）
    /// </summary>
    private const int PollIntervalMs = 5000;

    public ModListPollingService(SyncApiService api, string syncFolder, SyncEngine syncEngine)
    {
        _api = api;
        _syncFolder = syncFolder;
        _syncEngine = syncEngine;

        _pollingTask = Task.Run(() => PollLoop(_cts.Token));
        FileLogger.Log("ModListPollingService 已启动");
    }

    private async Task PollLoop(CancellationToken ct)
    {
        // 启动后等待几秒，让初始同步注册完成
        try { await Task.Delay(3000, ct); } catch { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollOnce();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"ModListPolling 异常: {ex.Message}");
            }

            try { await Task.Delay(PollIntervalMs, ct); } catch { return; }
        }
    }

    private async Task PollOnce()
    {
        var items = await _api.GetModListAsync();
        if (items.Count == 0) return;

        foreach (var item in items)
        {
            try
            {
                await ProcessModItem(item);
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  ModList 处理失败: {item.Path} - {ex.Message}");
            }
        }
    }

    private async Task ProcessModItem(ModItem item)
    {
        // 将服务端路径转换为本地完整路径
        string localPath = Path.Combine(_syncFolder, item.Path.Replace('/', '\\'));

        if (item.Action == "delete")
        {
            await HandleRemoteDelete(item, localPath);
            return;
        }

        if (item.Action == "move" && !string.IsNullOrEmpty(item.OldPath))
        {
            await HandleRemoteMove(item, localPath);
            return;
        }

        // action == "update"
        await HandleRemoteUpdate(item, localPath);
    }

    /// <summary>
    /// 处理远程文件删除：如果本地存在该 placeholder 则删除
    /// </summary>
    private Task HandleRemoteDelete(ModItem item, string localPath)
    {
        if (File.Exists(localPath))
        {
            try
            {
                var fi = new FileInfo(localPath);
                // 只删除 placeholder 文件（有 ReparsePoint 属性）
                if (fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    _syncEngine.SuppressForModList(localPath);
                    File.Delete(localPath);
                    FileLogger.Log($"ModList: 远程删除 → 已删本地 placeholder: {item.Path}");
                    SyncStatusManager.Instance.AddLog("🗑️", $"已删除: {item.Path}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"ModList: 删除本地文件失败: {item.Path} - {ex.Message}");
            }
        }
        else if (Directory.Exists(localPath))
        {
            try
            {
                // 抑制目录下所有文件的 FileWatcher 事件
                foreach (var f in Directory.GetFiles(localPath, "*", SearchOption.AllDirectories))
                    _syncEngine.SuppressForModList(f);
                _syncEngine.SuppressForModList(localPath);
                Directory.Delete(localPath, recursive: true);
                FileLogger.Log($"ModList: 远程删除目录 → 已删本地目录: {item.Path}");
                SyncStatusManager.Instance.AddLog("🗑️", $"已删除文件夹: {item.Path}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"ModList: 删除本地目录失败: {item.Path} - {ex.Message}");
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理远程 move/rename：本地执行 File.Move / Directory.Move，保留已下载内容
    /// </summary>
    private Task HandleRemoteMove(ModItem item, string newLocalPath)
    {
        string oldLocalPath = Path.Combine(_syncFolder, item.OldPath.Replace('/', '\\'));

        if (item.IsDir)
        {
            if (Directory.Exists(oldLocalPath))
            {
                try
                {
                    // 抑制旧路径和新路径下所有文件的 FileWatcher 事件
                    foreach (var f in Directory.GetFiles(oldLocalPath, "*", SearchOption.AllDirectories))
                    {
                        _syncEngine.SuppressForModList(f);
                        var rel = Path.GetRelativePath(oldLocalPath, f);
                        var newF = Path.Combine(newLocalPath, rel);
                        _syncEngine.MarkRecentlySynced(newF);
                    }
                    _syncEngine.SuppressForModList(oldLocalPath);
                    _syncEngine.MarkRecentlySynced(newLocalPath);

                    Directory.CreateDirectory(Path.GetDirectoryName(newLocalPath)!);
                    Directory.Move(oldLocalPath, newLocalPath);
                    FileLogger.Log($"ModList: 远程移动目录 → {item.OldPath} → {item.Path}");
                    SyncStatusManager.Instance.AddLog("📁", $"移动文件夹: {item.OldPath} → {item.Path}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"ModList: 移动目录失败: {item.OldPath} → {item.Path} - {ex.Message}");
                }
            }
            else if (!Directory.Exists(newLocalPath))
            {
                CreatePlaceholderForItem(item, newLocalPath);
                FileLogger.Log($"ModList: 远程移动目录(旧不存在) → 创建 placeholder: {item.Path}");
            }
        }
        else
        {
            if (File.Exists(oldLocalPath))
            {
                try
                {
                    _syncEngine.SuppressForModList(oldLocalPath);
                    _syncEngine.MarkRecentlySynced(newLocalPath);

                    Directory.CreateDirectory(Path.GetDirectoryName(newLocalPath)!);
                    File.Move(oldLocalPath, newLocalPath, overwrite: true);
                    FileLogger.Log($"ModList: 远程移动文件 → {item.OldPath} → {item.Path}");
                    SyncStatusManager.Instance.AddLog("📄", $"移动文件: {item.OldPath} → {item.Path}");
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"ModList: 移动文件失败: {item.OldPath} → {item.Path} - {ex.Message}");
                }
            }
            else if (!File.Exists(newLocalPath))
            {
                CreatePlaceholderForItem(item, newLocalPath);
                FileLogger.Log($"ModList: 远程移动文件(旧不存在) → 创建 placeholder: {item.Path}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理远程文件更新：
    ///   - 本地不存在 → 创建占位符（placeholder）
    ///   - 本地是白云(dehydrated) → 忽略（打开时 FETCH_DATA 自动拉最新）
    ///   - 本地已 hydrated：
    ///       mtime 相同 → 自己上传的，跳过
    ///       mtime 不同 → 别人修改的，脱水让用户下次打开获取新版
    /// </summary>
    private Task HandleRemoteUpdate(ModItem item, string localPath)
    {
        // ── 目录处理 ──
        if (item.IsDir)
        {
            if (!Directory.Exists(localPath))
            {
                CreatePlaceholderForItem(item, localPath);
            }
            return Task.CompletedTask;
        }

        // ── 文件处理 ──
        if (!File.Exists(localPath))
        {
            // 本地不存在 → 创建占位符
            CreatePlaceholderForItem(item, localPath);
            return Task.CompletedTask;
        }

        var fi = new FileInfo(localPath);

        // 不是 placeholder → 不管（普通文件由 FileWatcher 处理）
        if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) return Task.CompletedTask;

        // 文件的 mtime
        long localMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath)).ToUnixTimeSeconds();
        long cloudMtime = item.Mtime;

        // 已脱水(Offline)的 placeholder：如果云端版本更新，需要更新元数据（mtime/size）
        // 这样 Explorer 显示正确大小，下次打开 FETCH_DATA 拉最新
        if (fi.Attributes.HasFlag(FileAttributes.Offline))
        {
            if (cloudMtime > localMtime)
            {
                FileLogger.Log($"ModList: 白云文件元数据更新: {item.Path} (cloud={cloudMtime}, local={localMtime}, size={item.Size})");
                _syncEngine.SuppressForModList(localPath);
                UpdateAndDehydrateFile(localPath, item.Mtime, item.Size);
                SyncStatusManager.Instance.AddLog("☁️", $"云端已更新: {item.Path}");
            }
            return Task.CompletedTask;
        }

        // 文件是 hydrated 的 placeholder（绿勾），需要检查 mtime

        if (localMtime == cloudMtime)
        {
            // mtime 相同 → 自己上传的，跳过
            return Task.CompletedTask;
        }

        if (cloudMtime < localMtime)
        {
            // 云端 mtime 比本地更旧 → 本地刚修改过还没被 modlist 反映，跳过
            FileLogger.Log($"ModList: 跳过(本地更新): {item.Path} (cloud={cloudMtime}, local={localMtime})");
            return Task.CompletedTask;
        }

        // cloudMtime > localMtime → 别人修改/上传的，脱水本地文件
        FileLogger.Log($"ModList: 云端变更，脱水本地: {item.Path} (cloud={cloudMtime}, local={localMtime})");
        _syncEngine.SuppressForModList(localPath);
        UpdateAndDehydrateFile(localPath, item.Mtime, item.Size);
        SyncStatusManager.Instance.AddLog("☁️", $"云端已更新: {item.Path}");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 为远程新文件创建本地占位符。
    /// </summary>
    private void CreatePlaceholderForItem(ModItem item, string localPath)
    {
        try
        {
            // 确保父目录存在
            var parentDir = Path.GetDirectoryName(localPath);
            if (parentDir != null) Directory.CreateDirectory(parentDir);

            long fileTime = item.Mtime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(item.Mtime).ToFileTime()
                : DateTimeOffset.UtcNow.ToFileTime();

            byte[] identityBytes = System.Text.Encoding.UTF8.GetBytes(item.Path);

            var placeholder = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = Marshal.StringToHGlobalUni(Path.GetFileName(localPath)),
                FsMetadata = new CF_FS_METADATA
                {
                    BasicInfo_CreationTime = fileTime,
                    BasicInfo_LastAccessTime = fileTime,
                    BasicInfo_LastWriteTime = fileTime,
                    BasicInfo_ChangeTime = fileTime,
                    BasicInfo_FileAttributes = item.IsDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                    FileSize = item.IsDir ? 0 : item.Size,
                },
                FileIdentity = CopyToHGlobal(identityBytes),
                FileIdentityLength = (uint)identityBytes.Length,
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
            };

            try
            {
                int hr = CfCreatePlaceholders(
                    parentDir!,
                    new[] { placeholder },
                    1,
                    CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                    out uint processed);

                if (hr >= 0 && processed > 0)
                {
                    var icon = item.IsDir ? "📁" : "📄";
                    var label = item.IsDir ? "新文件夹" : "新文件";
                    FileLogger.Log($"ModList: 创建{(item.IsDir ? "目录" : "文件")}占位符: {item.Path}");
                    SyncStatusManager.Instance.AddLog(icon, $"{label}: {item.Path}");
                }
                else
                {
                    FileLogger.Log($"ModList: 创建占位符失败 0x{(uint)hr:X8}: {item.Path}");
                }
            }
            finally
            {
                if (placeholder.RelativeFileName != IntPtr.Zero) Marshal.FreeHGlobal(placeholder.RelativeFileName);
                if (placeholder.FileIdentity != IntPtr.Zero) Marshal.FreeHGlobal(placeholder.FileIdentity);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"ModList: 创建占位符异常: {item.Path} - {ex.Message}");
        }
    }

    private static IntPtr CopyToHGlobal(byte[] data)
    {
        IntPtr ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return ptr;
    }

    /// <summary>
    /// 更新占位符元数据（文件大小、修改时间）并脱水。
    /// 使用 CfUpdatePlaceholder 一次性完成，确保下次 hydrate 时 Windows 使用正确的文件大小。
    /// </summary>
    private static void UpdateAndDehydrateFile(string fullPath, long cloudMtimeUnix, long cloudSize)
    {
        IntPtr handle = CreateFileW(
            fullPath,
            0x00000182, // FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | FILE_WRITE_DATA
            0x00000007, // FILE_SHARE_READ | WRITE | DELETE
            IntPtr.Zero,
            3,          // OPEN_EXISTING
            0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            FileLogger.Log($"  UpdateAndDehydrate: 无法打开句柄: {fullPath}");
            return;
        }

        try
        {
            long fileTime = DateTimeOffset.FromUnixTimeSeconds(cloudMtimeUnix).ToFileTime();
            var metadata = new CF_FS_METADATA
            {
                BasicInfo_CreationTime = fileTime,
                BasicInfo_LastAccessTime = fileTime,
                BasicInfo_LastWriteTime = fileTime,
                BasicInfo_ChangeTime = fileTime,
                BasicInfo_FileAttributes = FILE_ATTRIBUTE_NORMAL,
                FileSize = cloudSize,
            };

            int hr = CfUpdatePlaceholder(
                handle,
                in metadata,
                IntPtr.Zero,    // FileIdentity - 保持不变
                0,
                IntPtr.Zero,    // DehydrateRangeArray
                0,              // DehydrateRangeCount
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC,
                IntPtr.Zero,    // UpdateUsn
                IntPtr.Zero);   // Overlapped

            if (hr >= 0)
            {
                FileLogger.Log($"  UpdateAndDehydrate: 已更新元数据并脱水: {fullPath} (size={cloudSize})");
            }
            else
            {
                FileLogger.Log($"  UpdateAndDehydrate: 失败 0x{(uint)hr:X8}: {fullPath}");
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    public void Dispose()
    {
        _cts.Cancel();
        try { _pollingTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        FileLogger.Log("ModListPollingService 已停止");
    }
}
