using System.IO;

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
            InternalBufferSize = 64 * 1024, // 64KB 缓冲区，防止事件丢失
        };

        _watcher.Created += OnCreated;
        _watcher.Renamed += OnRenamed;
        _watcher.Changed += OnChanged;
        _watcher.Deleted += OnDeleted;
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
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // 过滤反馈事件（CfConvertToPlaceholder / CfSetInSyncState / FETCH_DATA 触发的属性变化）
        if (_engine.IsRecentlySynced(e.FullPath))
        {
            FileLogger.Log($"FileWatcher.Changed 跳过(RecentlySynced): {e.FullPath}");
            return;
        }

        // ModList 脱水操作引起的 Changed 不要重新上传
        if (_engine.IsModListSuppressed(e.FullPath))
        {
            FileLogger.Log($"FileWatcher.Changed 跳过(ModList抑制): {e.FullPath}");
            return;
        }

        // 优先检查是否是"释放空间"触发的属性变化（PinState → UNPINNED）
        if (SyncProviderConnection.TryHandleDehydrateRequest(e.FullPath))
            return;

        // 只处理文件修改（目录的 Changed 忽略）
        if (!File.Exists(e.FullPath) || Directory.Exists(e.FullPath)) return;

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

    public void Dispose()
    {
        _watcher.Dispose();
    }
}
