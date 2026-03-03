using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using EhangNAS_Sync.Models;
using static EhangNAS_Sync.Native.CldApi;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 首次启动全量同步：递归遍历云端目录树，
/// 用 CfCreatePlaceholders 批量创建所有占位符。
/// 确保用户打开同步文件夹时能立即看到完整的文件列表。
/// 
/// 完成后写入标记文件，下次启动时跳过全量同步。
/// 如果中途中断（关机/崩溃），标记未写入，下次启动会自动继续。
/// </summary>
public static class InitialSyncService
{
    private static readonly string StateDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YihangDrive");

    /// <summary>
    /// 检查指定同步目录的全量同步是否已完成。
    /// </summary>
    public static bool IsCompleted(string syncFolder)
    {
        return File.Exists(GetMarkerPath(syncFolder));
    }

    /// <summary>
    /// 执行全量同步。递归遍历云端目录树，为不存在的文件/目录创建占位符。
    /// 成功完成后写入标记文件。
    /// </summary>
    public static async Task SyncAsync(SyncApiService api, string syncFolder)
    {
        FileLogger.Log("InitialSync: 开始全量同步...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int totalFiles = 0;
        int totalDirs = 0;
        int totalCreated = 0;
        int totalReplaced = 0;
        int totalUploaded = 0;

        try
        {
            // 从根目录开始递归
            var (files, dirs, created, replaced, uploaded) = await SyncDirectoryRecursive(api, syncFolder, "");
            totalFiles = files;
            totalDirs = dirs;
            totalCreated = created;
            totalReplaced = replaced;
            totalUploaded = uploaded;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"InitialSync: 异常 - {ex.Message}");
        }

        sw.Stop();
        FileLogger.Log($"InitialSync: 完成 - 扫描 {totalDirs} 个目录, {totalFiles} 个文件, 新建 {totalCreated} 个占位符, 替换 {totalReplaced} 个(服务器更新), 上传 {totalUploaded} 个(本地更新), 耗时 {sw.ElapsedMilliseconds}ms");
        SyncStatusManager.Instance.AddLog("🔄", $"全量同步完成: {totalDirs}个目录, {totalFiles}个文件, 新建{totalCreated}, 替换{totalReplaced}, 上传{totalUploaded}");

        // 写入完成标记（只有成功走到这里才写入，中途异常/关机不会写入）
        WriteCompletionMarker(syncFolder);
    }

    /// <summary>
    /// 获取标记文件路径，绑定到具体同步目录（不同目录独立跟踪）。
    /// </summary>
    private static string GetMarkerPath(string syncFolder)
    {
        // 用确定性哈希做文件名（string.GetHashCode() 在 .NET 8 每次进程启动值不同）
        var hash = DeterministicHash(syncFolder.ToLowerInvariant());
        return Path.Combine(StateDir, $"initial_sync_done_{hash:X8}.marker");
    }

    /// <summary>
    /// 确定性哈希，跨进程重启结果一致（替代 string.GetHashCode 的随机化行为）。
    /// </summary>
    private static uint DeterministicHash(string s)
    {
        uint hash = 2166136261u; // FNV-1a offset basis
        foreach (char c in s)
        {
            hash ^= c;
            hash *= 16777619u; // FNV-1a prime
        }
        return hash;
    }

    /// <summary>
    /// 写入完成标记文件。
    /// </summary>
    private static void WriteCompletionMarker(string syncFolder)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            File.WriteAllText(GetMarkerPath(syncFolder), 
                $"completed={DateTime.UtcNow:O}\nfolder={syncFolder}");
            FileLogger.Log("InitialSync: 已写入完成标记");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"InitialSync: 写入标记失败 - {ex.Message}");
        }
    }

    /// <summary>
    /// 删除完成标记（换目录或登出时调用）。
    /// </summary>
    public static void ClearMarker(string syncFolder)
    {
        try
        {
            var path = GetMarkerPath(syncFolder);
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    /// <summary>
    /// 递归同步一个目录：列出云端内容，创建缺失的占位符，然后递归子目录。
    /// 对本地已存在的文件，比较时间戳：服务器更新则替换为占位符，本地更新则上传到服务器。
    /// 返回 (文件数, 目录数, 新建占位符数, 替换数, 上传数)
    /// </summary>
    private static async Task<(int files, int dirs, int created, int replaced, int uploaded)> SyncDirectoryRecursive(
        SyncApiService api, string syncFolder, string relativePath)
    {
        // 列出云端该目录下的内容
        List<TreeItem> items;
        try
        {
            items = await api.ListDirectoryAsync(relativePath);
        }
        catch (Exception ex)
        {
            FileLogger.Log($"InitialSync: 列目录失败 \"{relativePath}\" - {ex.Message}");
            return (0, 0, 0, 0, 0);
        }

        if (items.Count == 0) return (0, 1, 0, 0, 0);

        // UI 日志：显示正在扫描的目录和条目数
        int dirItemCount = items.Count(i => i.IsDir);
        int fileItemCount = items.Count(i => !i.IsDir);
        string displayPath = string.IsNullOrEmpty(relativePath) ? "/" : relativePath;
        SyncStatusManager.Instance.AddLog("📂", $"{displayPath}  ({dirItemCount}个目录, {fileItemCount}个文件)");

        // 本地目录完整路径
        string localDir = string.IsNullOrEmpty(relativePath)
            ? syncFolder
            : Path.Combine(syncFolder, relativePath.Replace('/', '\\'));

        // 确保本地目录存在
        Directory.CreateDirectory(localDir);

        // 筛选出需要处理的条目
        var toCreate = new List<TreeItem>();                         // 本地不存在，需创建占位符
        var toUpload = new List<(string localPath, string relPath)>(); // 本地更新，需上传
        var subDirs = new List<(string name, string relPath)>();
        int replaced = 0;

        foreach (var item in items)
        {
            string childRelPath = string.IsNullOrEmpty(relativePath)
                ? item.Name
                : relativePath + "/" + item.Name;
            string childLocalPath = Path.Combine(localDir, item.Name);

            if (item.IsDir)
            {
                subDirs.Add((item.Name, childRelPath));

                // 目录：如果本地不存在，需要创建占位符
                if (!Directory.Exists(childLocalPath))
                {
                    toCreate.Add(item);
                }
            }
            else
            {
                // 文件
                if (!File.Exists(childLocalPath))
                {
                    // 本地不存在 → 创建占位符
                    toCreate.Add(item);
                }
                else
                {
                    // 本地已存在 → 比较修改时间，哪个新用哪个
                    try
                    {
                        var localMtime = new DateTimeOffset(File.GetLastWriteTimeUtc(childLocalPath));
                        var serverMtime = DateTimeOffset.FromUnixTimeSeconds(item.Mtime);

                        if (serverMtime > localMtime.AddSeconds(2))
                        {
                            // 服务器更新 → 删除本地旧文件，重新创建占位符
                            try
                            {
                                // 去掉只读属性以确保可删除
                                var attrs = File.GetAttributes(childLocalPath);
                                if (attrs.HasFlag(System.IO.FileAttributes.ReadOnly))
                                    File.SetAttributes(childLocalPath, attrs & ~System.IO.FileAttributes.ReadOnly);
                                File.Delete(childLocalPath);
                                toCreate.Add(item);
                                replaced++;
                                FileLogger.Log($"InitialSync: 服务器更新，替换本地文件: {childRelPath} (本地={localMtime:u} 服务器={serverMtime:u})");
                                SyncStatusManager.Instance.AddLog("🔄", $"服务器更新: {childRelPath}");
                            }
                            catch (Exception ex)
                            {
                                FileLogger.Log($"InitialSync: 删除旧文件失败 {childRelPath}: {ex.Message}");
                            }
                        }
                        else if (localMtime > serverMtime.AddSeconds(2))
                        {
                            // 本地更新 → 需要上传到服务器
                            toUpload.Add((childLocalPath, childRelPath));
                            FileLogger.Log($"InitialSync: 本地更新，需上传: {childRelPath} (本地={localMtime:u} 服务器={serverMtime:u})");
                            SyncStatusManager.Instance.AddLog("⬆️", $"本地更新: {childRelPath}");
                        }
                        // else: 时间戳接近（差值≤2秒），视为相同，跳过
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"InitialSync: 比较文件时间失败 {childRelPath}: {ex.Message}");
                    }
                }
            }
        }

        int created = 0;
        if (toCreate.Count > 0)
        {
            created = CreatePlaceholders(localDir, relativePath, toCreate);
            SyncStatusManager.Instance.AddLog("✨", $"新建 {created} 个占位符: {displayPath}");
        }

        // 上传本地更新的文件到服务器
        int uploaded = 0;
        foreach (var (localPath, relPath) in toUpload)
        {
            try
            {
                bool ok = await api.UploadFileAsync(relPath, localPath);
                if (ok)
                {
                    uploaded++;
                    FileLogger.Log($"InitialSync: 已上传本地更新文件: {relPath}");
                    SyncStatusManager.Instance.AddLog("✅", $"已上传: {relPath}");
                }
                else
                {
                    FileLogger.Log($"InitialSync: 上传失败(HTTP错误): {relPath}");
                }
            }
            catch (Exception ex)
            {
                FileLogger.Log($"InitialSync: 上传异常 {relPath}: {ex.Message}");
            }
        }

        int fileCount = items.Count(i => !i.IsDir);
        int dirCount = 1; // 当前目录

        // 递归处理子目录
        foreach (var (name, relPath) in subDirs)
        {
            var (subFiles, subDirs2, subCreated, subReplaced, subUploaded) = await SyncDirectoryRecursive(api, syncFolder, relPath);
            fileCount += subFiles;
            dirCount += subDirs2;
            created += subCreated;
            replaced += subReplaced;
            uploaded += subUploaded;
        }

        // 确保当前目录为 IN_SYNC 状态
        if (toCreate.Count > 0 && !string.IsNullOrEmpty(relativePath))
        {
            SetItemInSync(localDir);
        }

        return (fileCount, dirCount, created, replaced, uploaded);
    }

    /// <summary>
    /// 使用 CfCreatePlaceholders 批量创建占位符
    /// </summary>
    private static int CreatePlaceholders(string localDir, string parentRelativePath, List<TreeItem> items)
    {
        var placeholders = new CF_PLACEHOLDER_CREATE_INFO[items.Count];

        for (int i = 0; i < items.Count; i++)
        {
            var item = items[i];

            string relPath = string.IsNullOrEmpty(parentRelativePath)
                ? item.Name
                : parentRelativePath + "/" + item.Name;
            byte[] identityBytes = Encoding.UTF8.GetBytes(relPath);

            long fileTime = item.Mtime > 0
                ? DateTimeOffset.FromUnixTimeSeconds(item.Mtime).ToFileTime()
                : DateTimeOffset.UtcNow.ToFileTime();

            placeholders[i] = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = Marshal.StringToHGlobalUni(item.Name),
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
        }

        try
        {
            int hr = CfCreatePlaceholders(
                localDir,
                placeholders,
                (uint)placeholders.Length,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                out uint entriesProcessed);

            if (hr < 0)
            {
                FileLogger.Log($"InitialSync: CfCreatePlaceholders 失败 0x{(uint)hr:X8} dir=\"{parentRelativePath}\"");
                return 0;
            }

            FileLogger.Log($"InitialSync: 创建 {entriesProcessed} 个占位符 in \"{parentRelativePath}\"");
            return (int)entriesProcessed;
        }
        finally
        {
            // 释放非托管内存
            foreach (var p in placeholders)
            {
                if (p.RelativeFileName != IntPtr.Zero) Marshal.FreeHGlobal(p.RelativeFileName);
                if (p.FileIdentity != IntPtr.Zero) Marshal.FreeHGlobal(p.FileIdentity);
            }
        }
    }

    private static IntPtr CopyToHGlobal(byte[] data)
    {
        IntPtr ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return ptr;
    }

    /// <summary>
    /// 将文件/目录标记为 IN_SYNC（绿勾）
    /// </summary>
    private static void SetItemInSync(string path)
    {
        try
        {
            bool isDir = Directory.Exists(path);
            uint flags = isDir ? 0x02000000u : 0x00000080u;

            IntPtr handle = CreateFileW(
                path,
                0x00000182,
                0x00000007,
                IntPtr.Zero,
                3,
                flags,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

            try
            {
                CfSetInSyncState(
                    handle,
                    CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                    CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                    IntPtr.Zero);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch { }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);
}
