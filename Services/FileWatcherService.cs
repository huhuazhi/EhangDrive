using System.IO;
using EhangNAS_Sync.Models;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 监听同步目录的文件系统变化，将事件丢进 SyncEngine
/// </summary>
public sealed class FileWatcherService : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly SyncEngine _engine;
    private readonly string _syncFolder;

    public FileWatcherService(string syncFolder, SyncEngine engine)
    {
        _syncFolder = syncFolder;
        _engine = engine;

        _watcher = new FileSystemWatcher(syncFolder)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.DirectoryName
                         | NotifyFilters.FileName
                         | NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.Attributes,
            InternalBufferSize = 256 * 1024, // 256KB 缓冲区，防止大目录树删除时事件丢失
        };

        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
        _watcher.Error += OnError;
    }

    public void Start()
    {
        _watcher.EnableRaisingEvents = true;
    }

    public void Stop()
    {
        _watcher.EnableRaisingEvents = false;
    }

    private void OnCreated(object sender, FileSystemEventArgs e)
    {
        // 被抑制的删除目录树下不处理任何 Created 事件
        if (_engine.IsInSuppressedTree(e.FullPath)) return;

        FileLogger.Log($"FileWatcher.Created: {e.FullPath}");

        // 过滤 CfConvertToPlaceholder 触发的反馈事件
        if (_engine.IsRecentlySynced(e.FullPath)) return;

        var relativePath = Path.GetRelativePath(_syncFolder, e.FullPath)
                               .Replace('\\', '/');

        // 跳过不需要同步的路径
        if (SyncEngine.ShouldSkipPath(relativePath)) return;

        if (Directory.Exists(e.FullPath))
        {
            // 检查是否有同名 pending delete（move = delete + create）
            var name = Path.GetFileName(e.FullPath);
            if (_engine.TryCancelPendingDelete(name, out var oldRelativePath))
            {
                FileLogger.Log($"  → 检测到移动: {oldRelativePath} → {relativePath}");
                _engine.Enqueue(new SyncEvent(
                    SyncEventType.RenameItem,
                    e.FullPath,
                    relativePath,
                    null,
                    oldRelativePath));
                return;
            }

            FileLogger.Log($"  → 入队 CreateDirectory: {relativePath}");
            _engine.Enqueue(new SyncEvent(
                SyncEventType.CreateDirectory,
                e.FullPath,
                relativePath));

            // 标记父目录为 RecentlySynced，防止子目录/文件创建触发父目录 Changed 时
            // 对 UNPINNED 父目录执行误脱水（如 .7z 在解压时被反复脱水/水合）
            var parentDir = Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrEmpty(parentDir) && parentDir.Length > _syncFolder.Length)
                _engine.MarkRecentlySynced(parentDir);
        }
        else if (File.Exists(e.FullPath))
        {
            // 检查是否有同名 pending delete（文件 move = delete + create）
            // 注意：必须在 placeholder 检查之前！移动的 placeholder 文件也需要处理
            var name = Path.GetFileName(e.FullPath);
            if (_engine.TryCancelPendingDelete(name, out var oldRelativePath))
            {
                FileLogger.Log($"  → 检测到文件移动: {oldRelativePath} → {relativePath}");
                _engine.Enqueue(new SyncEvent(
                    SyncEventType.RenameItem,
                    e.FullPath,
                    relativePath,
                    null,
                    oldRelativePath));
                return;
            }

            // 跳过 placeholder 文件（非移动场景）
            // 但如果对应的 DeleteItem 还在消费者队列中（尚未处理），
            // 上面的 TryCancelPendingDelete 找不到，需要入队让消费者侧再检测一次
            bool isPlaceholder = false;
            try
            {
                var fi = new FileInfo(e.FullPath);
                isPlaceholder = fi.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint);
            }
            catch { return; }

            if (isPlaceholder)
            {
                // Placeholder 文件出现在 Created 事件中，很可能是 move 的后半段
                // 入队让消费者有机会在 HandleDelete 处理后再做 move 检测
                FileLogger.Log($"  → Placeholder 文件 Created，入队待消费者判断: {relativePath}");
                _engine.Enqueue(new SyncEvent(
                    SyncEventType.CreateFile,
                    e.FullPath,
                    relativePath));
                return;
            }

            FileLogger.Log($"  → 入队 CreateFile: {relativePath}");
            _engine.Enqueue(new SyncEvent(
                SyncEventType.CreateFile,
                e.FullPath,
                relativePath));

            // 标记父目录为 RecentlySynced（同 CreateDirectory 的理由）
            var parentDir2 = Path.GetDirectoryName(e.FullPath);
            if (!string.IsNullOrEmpty(parentDir2) && parentDir2.Length > _syncFolder.Length)
                _engine.MarkRecentlySynced(parentDir2);
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // 被抑制的删除目录树下不处理任何 Changed 事件（包括 DEHYDRATE）
        if (_engine.IsInSuppressedTree(e.FullPath)) return;

        // 先过滤内部操作的反馈事件（CfConvertToPlaceholder / CfSetInSyncState / FETCH_DATA / FlushDirtyDirectories）
        // 必须在 TryHandleDehydrateRequest 之前！否则内部操作触发的 Changed 会对 UNPINNED 目录执行脱水，
        // 导致刚上传的文件被立即脱水（白云 bug）
        if (_engine.IsRecentlySynced(e.FullPath))
        {
            FileLogger.Log($"FileWatcher.Changed 跳过(RecentlySynced): {e.FullPath}");
            return;
        }

        // 检查"释放空间"（PinState → UNPINNED）
        // 用户右键"释放空间"后 Windows 设置 PinState=UNPINNED，这里检测并主动脱水
        if (SyncProviderConnection.TryHandleDehydrateRequest(e.FullPath))
            return;

        // ModList 脱水操作引起的 Changed 不要重新上传
        if (_engine.IsModListSuppressed(e.FullPath))
        {
            FileLogger.Log($"FileWatcher.Changed 跳过(ModList抑制): {e.FullPath}");
            return;
        }

        // 检查是否是"始终保留在此设备上"触发的属性变化（PinState → PINNED）
        if (SyncProviderConnection.TryHandlePinRequest(e.FullPath))
            return;

        // 目录的 Changed 事件通常是属性变化（如 Windows 搜索设置 NotContentIndexed），
        // 这类属性变化会清除 Cloud Filter 的 IN_SYNC 状态导致显示白云。
        // 检测到 placeholder 目录变化时，重新设置 IN_SYNC。
        if (Directory.Exists(e.FullPath))
        {
            try
            {
                var di = new DirectoryInfo(e.FullPath);
                if (di.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint))
                {
                    SyncProviderConnection.SetItemInSyncPublic(e.FullPath);
                }
            }
            catch { }
            return;
        }

        // 只处理文件修改
        if (!File.Exists(e.FullPath)) return;

        var relativePath = Path.GetRelativePath(_syncFolder, e.FullPath)
                               .Replace('\\', '/');

        if (SyncEngine.ShouldSkipPath(relativePath)) return;

        FileLogger.Log($"FileWatcher.Changed: {e.FullPath}");
        _engine.Enqueue(new SyncEvent(
            SyncEventType.ModifyFile,
            e.FullPath,
            relativePath));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        FileLogger.Log($"FileWatcher.Deleted: {e.FullPath}");

        // Deleted 事件到达时，尝试标记该目录树为抑制状态
        // 这样后续的 Changed/Created 事件会被跳过
        _engine.MarkTreeSuppressed(e.FullPath);

        // ModList 删除本地文件时不要级联删除服务端
        if (_engine.IsModListSuppressed(e.FullPath))
        {
            FileLogger.Log($"  跳过(ModList抑制): {e.FullPath}");
            return;
        }

        var relativePath = Path.GetRelativePath(_syncFolder, e.FullPath)
                               .Replace('\\', '/');

        if (SyncEngine.ShouldSkipPath(relativePath)) return;

        FileLogger.Log($"  → 入队 DeleteItem: {relativePath}");
        _engine.Enqueue(new SyncEvent(
            SyncEventType.DeleteItem,
            e.FullPath,
            relativePath));

        // 标记父目录为 RecentlySynced，防止子项删除触发父目录 Changed 事件时
        // 对 UNPINNED 父目录执行误脱水（如删除子目录导致 .7z 被脱水再水合）
        var parentDir = Path.GetDirectoryName(e.FullPath);
        if (!string.IsNullOrEmpty(parentDir) && parentDir.Length > _syncFolder.Length)
            _engine.MarkRecentlySynced(parentDir);
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        FileLogger.Log($"FileWatcher.Renamed: {e.OldFullPath} → {e.FullPath}");

        // 过滤 CfConvertToPlaceholder 触发的反馈事件
        if (_engine.IsRecentlySynced(e.FullPath)) return;

        // 立即标记新路径，抑制随后到来的 Changed 事件（Windows 重命名会同时触发 Changed）
        _engine.MarkRecentlySynced(e.FullPath);

        var relativePath = Path.GetRelativePath(_syncFolder, e.FullPath)
                               .Replace('\\', '/');
        var oldRelativePath = Path.GetRelativePath(_syncFolder, e.OldFullPath)
                                  .Replace('\\', '/');

        FileLogger.Log($"  → 入队 RenameItem: {oldRelativePath} → {relativePath}");
        _engine.Enqueue(new SyncEvent(
            SyncEventType.RenameItem,
            e.FullPath,
            relativePath,
            e.OldFullPath,
            oldRelativePath));
    }

    /// <summary>
    /// FileSystemWatcher 缓冲区溢出或内部错误。
    /// 缓冲区溢出时部分事件会丢失（解压大量文件时常见），
    /// 触发全量扫描补偿，将未同步的文件/目录重新入队。
    /// </summary>
    private void OnError(object sender, ErrorEventArgs e)
    {
        FileLogger.Log($"FileWatcher.Error: {e.GetException().Message}");

        if (e.GetException() is InternalBufferOverflowException)
        {
            FileLogger.Log("  FileSystemWatcher 缓冲区溢出，启动全量扫描补偿...");
            SyncStatusManager.Instance.AddLog("⚠️", "文件系统事件缓冲区溢出，启动补偿扫描");
            _engine.EnqueueFullScan();
        }
    }

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
