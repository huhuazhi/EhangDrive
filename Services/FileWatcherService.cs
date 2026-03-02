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
                         | NotifyFilters.Size,
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
            // 跳过 placeholder 文件
            try
            {
                var fi = new FileInfo(e.FullPath);
                if (fi.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint))
                {
                    FileLogger.Log($"  → 跳过 placeholder 文件");
                    return;
                }
            }
            catch { return; }

            // 检查是否有同名 pending delete（文件 move = delete + create）
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

            FileLogger.Log($"  → 入队 CreateFile: {relativePath}");
            _engine.Enqueue(new SyncEvent(
                SyncEventType.CreateFile,
                e.FullPath,
                relativePath));
        }
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        // FileLogger.Log($"FileWatcher.Changed: {e.FullPath}");

        // 过滤反馈事件
        if (_engine.IsRecentlySynced(e.FullPath)) return;

        // 只处理文件修改（目录的 Changed 忽略）
        if (!File.Exists(e.FullPath) || Directory.Exists(e.FullPath)) return;

        var relativePath = Path.GetRelativePath(_syncFolder, e.FullPath)
                               .Replace('\\', '/');

        if (SyncEngine.ShouldSkipPath(relativePath)) return;

        _engine.Enqueue(new SyncEvent(
            SyncEventType.ModifyFile,
            e.FullPath,
            relativePath));
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        FileLogger.Log($"FileWatcher.Deleted: {e.FullPath}");

        // 过滤反馈事件
        if (_engine.IsRecentlySynced(e.FullPath)) return;

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
