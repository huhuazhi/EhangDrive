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

        // 只处理文件夹（当前阶段）
        if (Directory.Exists(e.FullPath))
        {
            var relativePath = Path.GetRelativePath(_syncFolder, e.FullPath)
                                   .Replace('\\', '/');

            FileLogger.Log($"  → 入队 CreateDirectory: {relativePath}");
            _engine.Enqueue(new SyncEvent(
                SyncEventType.CreateDirectory,
                e.FullPath,
                relativePath));
        }
        else
        {
            FileLogger.Log($"  → 不是目录，跳过");
        }
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
