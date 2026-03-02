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
                    File.Delete(localPath);
                    FileLogger.Log($"ModList: 远程删除 → 已删本地 placeholder: {item.Path}");
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
                Directory.Delete(localPath, recursive: true);
                FileLogger.Log($"ModList: 远程删除目录 → 已删本地目录: {item.Path}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"ModList: 删除本地目录失败: {item.Path} - {ex.Message}");
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理远程文件更新：
    ///   - 本地不存在 → 忽略（下次浏览目录时 FETCH_PLACEHOLDERS 自动处理）
    ///   - 本地是白云(dehydrated) → 忽略（打开时 FETCH_DATA 自动拉最新）
    ///   - 本地已 hydrated → 比较 mtime，云端新则脱水，本地新则上传
    /// </summary>
    private async Task HandleRemoteUpdate(ModItem item, string localPath)
    {
        if (!File.Exists(localPath)) return;

        var fi = new FileInfo(localPath);

        // 不是 placeholder → 不管（普通文件由 FileWatcher 处理）
        if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) return;

        // 已脱水(Offline) → 不管，下次打开自然拉最新
        if (fi.Attributes.HasFlag(FileAttributes.Offline)) return;

        // 文件是 hydrated 的 placeholder（绿勾），需要检查 mtime
        long localMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(localPath)).ToUnixTimeSeconds();
        long cloudMtime = item.Mtime;

        if (cloudMtime > localMtime)
        {
            // 云端更新 → 更新元数据并脱水本地文件
            FileLogger.Log($"ModList: 云端更新，脱水本地: {item.Path} (cloud={cloudMtime}, local={localMtime})");
            UpdateAndDehydrateFile(localPath, item.Mtime, item.Size);
            SyncStatusManager.Instance.AddLog("☁️", $"云端已更新: {item.Path}");
        }
        else if (localMtime > cloudMtime)
        {
            // 本地更新 → 上传到云端
            // 先检查是否正在被 SyncEngine 处理
            if (_syncEngine.IsRecentlySynced(localPath)) return;

            FileLogger.Log($"ModList: 本地更新，上传: {item.Path} (cloud={cloudMtime}, local={localMtime})");
            var ok = await _api.UploadFileAsync(item.Path, localPath);
            if (ok)
            {
                FileLogger.Log($"ModList: 上传成功: {item.Path}");
            }
        }
        // mtime 一致 → 不需要任何操作
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
