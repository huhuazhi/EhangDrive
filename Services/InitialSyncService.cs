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
/// </summary>
public static class InitialSyncService
{
    /// <summary>
    /// 执行全量同步。递归遍历云端目录树，为不存在的文件/目录创建占位符。
    /// </summary>
    public static async Task SyncAsync(SyncApiService api, string syncFolder)
    {
        FileLogger.Log("InitialSync: 开始全量同步...");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        int totalFiles = 0;
        int totalDirs = 0;
        int totalCreated = 0;

        try
        {
            // 从根目录开始递归
            var (files, dirs, created) = await SyncDirectoryRecursive(api, syncFolder, "");
            totalFiles = files;
            totalDirs = dirs;
            totalCreated = created;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"InitialSync: 异常 - {ex.Message}");
        }

        sw.Stop();
        FileLogger.Log($"InitialSync: 完成 - 扫描 {totalDirs} 个目录, {totalFiles} 个文件, 新建 {totalCreated} 个占位符, 耗时 {sw.ElapsedMilliseconds}ms");
        SyncStatusManager.Instance.AddLog("🔄", $"全量同步完成: {totalDirs}个目录, {totalFiles}个文件, 新建{totalCreated}个占位符");
    }

    /// <summary>
    /// 递归同步一个目录：列出云端内容，创建缺失的占位符，然后递归子目录。
    /// 返回 (文件数, 目录数, 新建占位符数)
    /// </summary>
    private static async Task<(int files, int dirs, int created)> SyncDirectoryRecursive(
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
            return (0, 0, 0);
        }

        if (items.Count == 0) return (0, 1, 0);

        // 本地目录完整路径
        string localDir = string.IsNullOrEmpty(relativePath)
            ? syncFolder
            : Path.Combine(syncFolder, relativePath.Replace('/', '\\'));

        // 确保本地目录存在
        Directory.CreateDirectory(localDir);

        // 筛选出本地还不存在的条目（需要创建占位符的）
        var toCreate = new List<TreeItem>();
        var subDirs = new List<(string name, string relPath)>();

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
                // 文件：如果本地不存在，需要创建占位符
                if (!File.Exists(childLocalPath))
                {
                    toCreate.Add(item);
                }
            }
        }

        int created = 0;
        if (toCreate.Count > 0)
        {
            created = CreatePlaceholders(localDir, relativePath, toCreate);
        }

        int fileCount = items.Count(i => !i.IsDir);
        int dirCount = 1; // 当前目录

        // 递归处理子目录
        foreach (var (name, relPath) in subDirs)
        {
            var (subFiles, subDirs2, subCreated) = await SyncDirectoryRecursive(api, syncFolder, relPath);
            fileCount += subFiles;
            dirCount += subDirs2;
            created += subCreated;
        }

        // 确保当前目录为 IN_SYNC 状态
        if (toCreate.Count > 0 && !string.IsNullOrEmpty(relativePath))
        {
            SetItemInSync(localDir);
        }

        return (fileCount, dirCount, created);
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
