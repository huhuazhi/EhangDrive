using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using EhangNAS_Sync.Models;
using static EhangNAS_Sync.Native.CldApi;
using Microsoft.Win32.SafeHandles;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 同步事件指令
/// </summary>
public record SyncEvent(SyncEventType Type, string FullPath, string RelativePath,
    string? OldFullPath = null, string? OldRelativePath = null);

public enum SyncEventType
{
    CreateDirectory,
    RenameItem,
    CreateFile,
    ModifyFile,
    DeleteItem,
}

/// <summary>
/// 同步引擎：生产者往里扔事件，内部消费者线程自行处理。
/// 主线程不阻塞。
/// </summary>
public sealed class SyncEngine : IDisposable
{
    private readonly SyncApiService _api;
    private readonly string _syncFolder;
    private readonly Channel<SyncEvent> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _consumerTask;

    // 防止 CfConvertToPlaceholder 触发的属性变化引起反馈循环
    private readonly ConcurrentDictionary<string, long> _recentlySynced = new(StringComparer.OrdinalIgnoreCase);

    // 正在上传中的文件（防止并发重复上传）
    private readonly ConcurrentDictionary<string, bool> _uploadingFiles = new(StringComparer.OrdinalIgnoreCase);

    // Changed 去重：同一文件 2 秒内不重复上传
    private readonly ConcurrentDictionary<string, long> _lastChangedTicks = new(StringComparer.OrdinalIgnoreCase);

    // 上传成功后记录文件的 mtime，用于防止 CfConvertToPlaceholder 触发的 Changed 事件引起重复上传
    private readonly ConcurrentDictionary<string, long> _lastUploadedMtime = new(StringComparer.OrdinalIgnoreCase);

    // 延迟删除队列（防止 move = delete + create 导致误删服务端文件）
    // key=relativePath, value=(fullPath, enqueueTicks)
    private readonly ConcurrentDictionary<string, (string fullPath, long ticks)> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    // 队列中待处理的事件数（Channel.Reader.Count 在 SingleReader=true 时不可用，所以手动计数）
    private int _pendingEventCount;

    // 并发上传控制：最多同时上传 15 个文件
    private readonly SemaphoreSlim _uploadSemaphore = new(15, 15);

    // 需要在所有文件传完后统一刷新 IN_SYNC 的目录（避免并发竞争）
    private readonly ConcurrentDictionary<string, byte> _dirtyDirectories = new(StringComparer.OrdinalIgnoreCase);
    public bool HasDirtyDirectories => !_dirtyDirectories.IsEmpty;

    // ModList 操作抑制：ModList 删除/脱水本地文件时，抑制 FileWatcher 产生的 Delete/Changed 事件
    // key=fullPath, value=ticks
    private readonly ConcurrentDictionary<string, long> _modListSuppressed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 标记一个文件正在被 ModList 操作（删除/脱水），让 FileWatcher 忽略后续事件
    /// </summary>
    public void SuppressForModList(string fullPath)
    {
        _modListSuppressed[fullPath] = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// 检查路径是否被 ModList 抑制（5秒内）
    /// </summary>
    public bool IsModListSuppressed(string fullPath)
    {
        if (_modListSuppressed.TryGetValue(fullPath, out var ticks))
        {
            if ((DateTime.UtcNow.Ticks - ticks) < TimeSpan.FromSeconds(5).Ticks)
                return true;
            _modListSuppressed.TryRemove(fullPath, out _);
        }
        return false;
    }

    /// <summary>
    /// 检查路径是否在最近被同步处理过（用于过滤反馈事件）
    /// </summary>
    public bool IsRecentlySynced(string fullPath)
    {
        if (_recentlySynced.TryGetValue(fullPath, out var ticks))
            return (DateTime.UtcNow.Ticks - ticks) < TimeSpan.FromSeconds(2).Ticks;
        return false;
    }

    /// <summary>
    /// 立即标记路径为"最近已同步"，用于在 FileWatcher 线程上抑制后续 Changed 事件
    /// </summary>
    public void MarkRecentlySynced(string fullPath)
    {
        _recentlySynced[fullPath] = DateTime.UtcNow.Ticks;
    }

    public SyncEngine(SyncApiService api, string syncFolder)
    {
        _api = api;
        _syncFolder = syncFolder;

        // 无界队列，生产者永远不阻塞
        _channel = Channel.CreateUnbounded<SyncEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        // 启动消费者
        _consumerTask = Task.Run(() => ConsumeLoop(_cts.Token));
    }

    /// <summary>
    /// 队列中待处理 + 正在上传中的文件数
    /// </summary>
    public int PendingCount => _pendingEventCount + _uploadingFiles.Count;

    /// <summary>
    /// 事件处理完成后减计数，如果全部完成则刷新脏目录
    /// </summary>
    private void DecrementAndTryFlush()
    {
        Interlocked.Decrement(ref _pendingEventCount);
        if (PendingCount == 0 && HasDirtyDirectories)
        {
            FlushDirtyDirectories();
        }
    }

    /// <summary>
    /// 生产者入口：丢一个同步事件进来，立即返回
    /// </summary>
    public void Enqueue(SyncEvent evt)
    {
        _channel.Writer.TryWrite(evt);
        Interlocked.Increment(ref _pendingEventCount);
    }

    /// <summary>
    /// 消费者循环
    /// </summary>
    private async Task ConsumeLoop(CancellationToken ct)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
        {
            // 注意：不在此处 Decrement！
            // Task.Run 派发的事件（CreateFile/ModifyFile/CreateDirectory）在各自 finally 中减计数，
            // 避免事件还在 Task.Delay 等待中时 PendingCount 就已为 0 的计数盲区。
            try
            {
                await ProcessEvent(evt);
            }
            catch (Exception ex)
            {
                SyncStatusManager.Instance.AddLog("❌", $"处理异常: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 处理单个事件
    /// </summary>
    private async Task ProcessEvent(SyncEvent evt)
    {
        switch (evt.Type)
        {
            case SyncEventType.CreateDirectory:
                // 目录创建也不阻塞消费者，与文件上传并行处理
                _ = Task.Run(async () =>
                {
                    try { await HandleCreateDirectory(evt); }
                    catch (Exception ex) { SyncStatusManager.Instance.AddLog("❌", $"处理异常: {ex.Message}"); }
                    finally { DecrementAndTryFlush(); }
                });
                break;
            case SyncEventType.RenameItem:
                Interlocked.Decrement(ref _pendingEventCount);
                await HandleRename(evt);
                break;
            case SyncEventType.CreateFile:
                // 并发处理：不阻塞消费者，由信号量控制并发数
                _ = Task.Run(async () =>
                {
                    try { await HandleCreateFile(evt); }
                    catch (Exception ex) { SyncStatusManager.Instance.AddLog("❌", $"处理异常: {ex.Message}"); }
                    finally { DecrementAndTryFlush(); }
                });
                break;
            case SyncEventType.ModifyFile:
                // 并发处理
                _ = Task.Run(async () =>
                {
                    try { await HandleModifyFile(evt); }
                    catch (Exception ex) { SyncStatusManager.Instance.AddLog("❌", $"处理异常: {ex.Message}"); }
                    finally { DecrementAndTryFlush(); }
                });
                break;
            case SyncEventType.DeleteItem:
                Interlocked.Decrement(ref _pendingEventCount);
                await HandleDelete(evt);
                break;
        }
    }

    /// <summary>
    /// 处理新建文件夹：延迟等待重命名 → 检测move → 调 mkdir API → 转为placeholder
    /// </summary>
    private async Task HandleCreateDirectory(SyncEvent evt)
    {
        FileLogger.Log($"HandleCreateDirectory: {evt.RelativePath} ({evt.FullPath})");

        // 短暂延迟：用户创建"新建文件夹"后通常会立即重命名
        // 拷贝场景不需要等太久，300ms 足够检测重命名
        await Task.Delay(300);

        if (!Directory.Exists(evt.FullPath))
        {
            FileLogger.Log($"  目录已不存在(可能已重命名)，跳过: {evt.FullPath}");
            return;
        }

        // ── move 检测：HandleDelete 已非阻塞地把旧路径放进 _pendingDeletes ──
        var dirName = Path.GetFileName(evt.RelativePath);
        if (TryCancelPendingDelete(dirName, out var oldRelativePath))
        {
            FileLogger.Log($"  → 消费者侧检测到文件夹移动: {oldRelativePath} → {evt.RelativePath}");
            await HandleRename(new SyncEvent(
                SyncEventType.RenameItem,
                evt.FullPath, evt.RelativePath,
                null, oldRelativePath));
            return;
        }

        SyncStatusManager.Instance.AddLog("🔵", $"同步文件夹: {evt.RelativePath}");
        var ok = await _api.MkdirAsync(evt.RelativePath);
        FileLogger.Log($"  MkdirAsync → {ok}");

        if (ok)
        {
            // 目录不立即转 placeholder（避免白费功夫被子文件清掉）
            // 加入脏目录列表，由 FlushDirtyDirectories 统一转 placeholder + IN_SYNC
            // 空目录转完虽然是白云，但至少比蓝圈正常（表示已是云端文件）
            _dirtyDirectories.TryAdd(evt.FullPath, 0);
            SyncStatusManager.Instance.AddLog("✅", $"文件夹已同步: {evt.RelativePath}");
        }
        else
        {
            SyncStatusManager.Instance.AddLog("❌", $"同步文件夹失败: {evt.RelativePath}");
        }
    }

    /// <summary>
    /// 处理重命名：服务端重命名 → 转为placeholder
    /// </summary>
    private async Task HandleRename(SyncEvent evt)
    {
        FileLogger.Log($"HandleRename: {evt.OldRelativePath} → {evt.RelativePath}");


        // 立即抑制新路径的 Changed 事件（Windows rename 会同时触发 Changed）
        _recentlySynced[evt.FullPath] = DateTime.UtcNow.Ticks;

        bool ok = false;
        bool isDir = Directory.Exists(evt.FullPath);

        // 判断是同目录改名还是跨目录移动
        var oldParent = Path.GetDirectoryName(evt.OldRelativePath ?? "")?.Replace('\\', '/') ?? "";
        var newParent = Path.GetDirectoryName(evt.RelativePath)?.Replace('\\', '/') ?? "";
        bool isMove = !string.Equals(oldParent, newParent, StringComparison.OrdinalIgnoreCase);
        var actionName = isMove ? "移动" : "重命名";
        SyncStatusManager.Instance.AddLog("🟢", $"{actionName}: {evt.OldRelativePath} → {evt.RelativePath}");

        try
        {
            if (isMove)
            {
                ok = await _api.MoveAsync(evt.OldRelativePath!, evt.RelativePath);
                FileLogger.Log($"  MoveAsync → {ok}");
            }
            else
            {
                ok = await _api.RenameAsync(evt.OldRelativePath!, evt.RelativePath, isDir);
                FileLogger.Log($"  RenameAsync → {ok}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  {(isMove ? "MoveAsync" : "RenameAsync")} 异常: {ex.Message}");
        }

        if (!ok)
        {
            // Fallback: 服务端可能还没有旧路径(Created被延迟跳过了)，直接创建新路径
            FileLogger.Log($"  {actionName}失败，尝试直接创建/上传: {evt.RelativePath}");
            if (Directory.Exists(evt.FullPath))
            {
                ok = await _api.MkdirAsync(evt.RelativePath);
            }
            else if (File.Exists(evt.FullPath))
            {
                // 文件 rename 失败（旧路径从未上传过），直接上传
                // forceUpload=true: 即使是 placeholder 也强制上传（移动的 placeholder）
                await UploadFileWithRetry(evt.FullPath, evt.RelativePath, forceUpload: true);
                return; // UploadFileWithRetry 内部已调用 ConvertToPlaceholderAndSync
            }
        }

        if (ok)
        {
            ConvertToPlaceholderAndSync(evt.FullPath, evt.RelativePath);
            // 目录重命名后需要刷新 IN_SYNC 状态（否则一直是蓝圈）
            if (isDir)
            {
                _dirtyDirectories.TryAdd(evt.FullPath, 0);
                FlushDirtyDirectories();
            }
            SyncStatusManager.Instance.AddLog("✅", $"已{actionName}: {evt.RelativePath}");
        }
        else
        {
            SyncStatusManager.Instance.AddLog("❌", $"{actionName}失败: {evt.RelativePath}");
        }
    }

    /// <summary>
    /// 处理新文件创建：先检测 move → 等待写入完成 → 上传 → 转为placeholder
    /// </summary>
    private async Task HandleCreateFile(SyncEvent evt)
    {
        FileLogger.Log($"HandleCreateFile: {evt.RelativePath} ({evt.FullPath})");

        // 短暂延迟，等文件写入完成
        await Task.Delay(200);

        if (!File.Exists(evt.FullPath))
        {
            FileLogger.Log($"  文件已不存在(可能已重命名)，跳过: {evt.FullPath}");
            return;
        }

        bool isPlaceholder = false;
        try
        {
            var fi = new FileInfo(evt.FullPath);
            isPlaceholder = fi.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint);
        }
        catch { return; }

        // ── move 检测（必须在 placeholder 跳过之前！placeholder 文件也可能是 move 的产物）──
        var fileName = Path.GetFileName(evt.RelativePath);
        if (TryCancelPendingDelete(fileName, out var oldRelativePath))
        {
            FileLogger.Log($"  → 消费者侧检测到文件移动: {oldRelativePath} → {evt.RelativePath}");
            await HandleRename(new SyncEvent(
                SyncEventType.RenameItem,
                evt.FullPath, evt.RelativePath,
                null, oldRelativePath));
            return;
        }

        // 非 move 场景的 placeholder 文件 → 跳过
        if (isPlaceholder)
        {
            FileLogger.Log($"  跳过 placeholder 文件(非移动): {evt.RelativePath}");
            return;
        }

        await UploadFileWithRetry(evt.FullPath, evt.RelativePath);
    }

    /// <summary>
    /// 处理文件修改：重新上传
    /// </summary>
    private async Task HandleModifyFile(SyncEvent evt)
    {
        FileLogger.Log($"HandleModifyFile: {evt.RelativePath}");

        // 消费者侧二次检查：移动操作(HandleRename)会设置 _recentlySynced，
        // 但 Changed 事件在 FileWatcher 回调时尚未被标记，已溜进队列。
        // 此时 HandleRename 已执行完毕，这里再检查一次即可拦住。
        if (IsRecentlySynced(evt.FullPath))
        {
            FileLogger.Log($"  消费者侧跳过(RecentlySynced): {evt.RelativePath}");
            return;
        }

        // 只处理文件
        if (!File.Exists(evt.FullPath) || Directory.Exists(evt.FullPath)) return;

        // Debounce: 2 秒内同一文件不重复上传
        var now = DateTime.UtcNow.Ticks;
        if (_lastChangedTicks.TryGetValue(evt.RelativePath, out var lastTicks))
        {
            if ((now - lastTicks) < TimeSpan.FromSeconds(2).Ticks)
            {
                FileLogger.Log($"  Debounce 跳过: {evt.RelativePath}");
                return;
            }
        }
        _lastChangedTicks[evt.RelativePath] = now;

        // 等文件写完
        await Task.Delay(1000);
        if (!File.Exists(evt.FullPath)) return;

        // 延迟后二次检查：并发的 HandleRename/HandleCreateFile 可能在这 1 秒内
        // 已经完成了 move/rename 并设置了 _recentlySynced，此时不应再上传。
        if (IsRecentlySynced(evt.FullPath))
        {
            FileLogger.Log($"  延迟后跳过(RecentlySynced): {evt.RelativePath}");
            return;
        }

        try
        {
            var fi = new FileInfo(evt.FullPath);
            // Offline = cloud-only 白云文件（未 hydrate），不需要上传
            if (fi.Attributes.HasFlag(System.IO.FileAttributes.Offline))
            {
                FileLogger.Log($"  跳过 Offline 文件(白云): {evt.RelativePath}");
                return;
            }
            // 注意：不再用 ReparsePoint 判断是否跳过！
            // Cloud Filter placeholder 被用户修改后 ReparsePoint 仍然保留，
            // 但内容已经变了，必须重新上传。
            // 反馈事件已在 OnChanged 中被 IsRecentlySynced(2秒) 过滤。

            // 但如果文件是 placeholder 且 mtime 未变（说明内容没有被修改，
            // Changed 事件只是 CfConvertToPlaceholder/脱水等属性变化引起的），可以跳过
            if (fi.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint) &&
                _lastUploadedMtime.TryGetValue(evt.RelativePath, out var lastMtime) &&
                fi.LastWriteTimeUtc.Ticks == lastMtime)
            {
                FileLogger.Log($"  跳过(placeholder mtime未变): {evt.RelativePath}");
                return;
            }
        }
        catch { return; }

        FileLogger.Log($"  准备上传修改: {evt.RelativePath}");
        await UploadFileWithRetry(evt.FullPath, evt.RelativePath, forceUpload: true);
    }

    /// <summary>
    /// 处理删除：立即入延迟队列并启动后台定时器，不阻塞消费者。
    /// 这样后续的 Create 事件可以立即被处理，配合 move 检测。
    /// </summary>
    private Task HandleDelete(SyncEvent evt)
    {
        FileLogger.Log($"HandleDelete: {evt.RelativePath}");

        // 入延迟队列（立即返回，不阻塞消费者）
        _pendingDeletes[evt.RelativePath] = (evt.FullPath, DateTime.UtcNow.Ticks);
        SyncStatusManager.Instance.AddLog("🗑", $"检测到删除(排队): {evt.RelativePath}");

        // 后台延迟 3 秒再检查：如果这期间出现同名 Created 事件（move 场景），则取消删除
        _ = Task.Run(async () =>
        {
            await Task.Delay(3000);

            if (!_pendingDeletes.TryRemove(evt.RelativePath, out var pending))
            {
                FileLogger.Log($"  延迟删除已被取消(可能是移动操作): {evt.RelativePath}");
                return;
            }

            // ── 祖先折叠：如果任何父目录也在待删队列，跳过子路径 ──
            // 用户删除一个目录时，FileSystemWatcher 会为每个子文件/子目录单独产生 Deleted 事件。
            // 只需对最顶层的目录发一次 DeleteAsync，子路径全部跳过。
            {
                var parts = evt.RelativePath.Split('/');
                for (int i = parts.Length - 1; i > 0; i--)
                {
                    var ancestor = string.Join('/', parts, 0, i);
                    if (_pendingDeletes.ContainsKey(ancestor))
                    {
                        FileLogger.Log($"  跳过删除(父目录待删): {evt.RelativePath}");
                        return;
                    }
                }
            }

            // 二次检查：本地是否又出现了
            if (File.Exists(pending.fullPath) || Directory.Exists(pending.fullPath))
            {
                // 空的 placeholder 目录（ReparsePoint）不算"恢复"，
                // 大目录树删除时 Windows 会留下空的 Cloud Filter 占位符目录壳。
                var isEmptyPlaceholderDir = false;
                if (Directory.Exists(pending.fullPath))
                {
                    try
                    {
                        var di = new DirectoryInfo(pending.fullPath);
                        isEmptyPlaceholderDir = di.Attributes.HasFlag(FileAttributes.ReparsePoint)
                                                && !di.EnumerateFileSystemInfos().Any();
                    }
                    catch { /* 忽略 */ }
                }

                if (!isEmptyPlaceholderDir)
                {
                    FileLogger.Log($"  取消删除(路径已恢复): {evt.RelativePath}");
                    SyncStatusManager.Instance.AddLog("🔙", $"取消删除(已恢复): {evt.RelativePath}");
                    return;
                }
                FileLogger.Log($"  路径仍存在但是空的占位符目录，继续删除: {evt.RelativePath}");
            }

            // 执行服务端删除
            SyncStatusManager.Instance.AddLog("🗑", $"删除服务端: {evt.RelativePath}");
            var ok = await _api.DeleteAsync(evt.RelativePath);
            FileLogger.Log($"  DeleteAsync → {ok}");

            if (ok)
            {
                SyncStatusManager.Instance.AddLog("✅", $"服务端已删除: {evt.RelativePath}");

                // ── 清理空的占位符父目录 ──
                // 删除大目录树时 FileSystemWatcher 缓冲区可能溢出，
                // 导致中间层目录的 Deleted 事件丢失，遗留空的 placeholder 目录。
                // 每次成功删除子项后，向上逐级检查并清理空的占位符父目录。
                await CleanupEmptyParentDirsAsync(pending.fullPath);
            }
            else
                SyncStatusManager.Instance.AddLog("❌", $"服务端删除失败: {evt.RelativePath}");
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// 向上逐级清理空的占位符父目录。
    /// 当 FileSystemWatcher 缓冲区溢出时，中间层目录的 Deleted 事件可能丢失，
    /// 导致空的 placeholder 目录残留。此方法在子项删除成功后自动清理。
    /// </summary>
    private async Task CleanupEmptyParentDirsAsync(string childFullPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(childFullPath);
            while (!string.IsNullOrEmpty(dir)
                   && !string.Equals(dir, _syncFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(dir)) break;

                var di = new DirectoryInfo(dir);

                // 只清理空的占位符目录（ReparsePoint = Cloud Filter placeholder）
                if (!di.Attributes.HasFlag(FileAttributes.ReparsePoint)) break;

                // 先清理该目录下所有空的 placeholder 子目录（兄弟节点可能遗留）
                foreach (var sub in di.EnumerateDirectories())
                {
                    try
                    {
                        if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)
                            && !sub.EnumerateFileSystemInfos().Any())
                        {
                            var subRel = Path.GetRelativePath(_syncFolder, sub.FullName).Replace('\\', '/');
                            FileLogger.Log($"  清理空占位符兄弟目录: {subRel}");
                            // 尝试删服务端（不阻塞本地清理）
                            await _api.DeleteAsync(subRel);
                            // 本地确认是空 placeholder，直接删
                            try { sub.Delete(); }
                            catch (Exception ex)
                            {
                                FileLogger.Log($"  删除本地兄弟目录失败: {subRel} - {ex.Message}");
                            }
                        }
                    }
                    catch { /* 忽略单个子目录失败 */ }
                }

                if (di.EnumerateFileSystemInfos().Any()) break; // 仍然非空，停止

                var relativePath = Path.GetRelativePath(_syncFolder, dir).Replace('\\', '/');
                FileLogger.Log($"  清理空占位符父目录: {relativePath}");

                // 先尝试删服务端（即使失败也继续删本地空 placeholder）
                var ok = await _api.DeleteAsync(relativePath);
                if (!ok)
                    FileLogger.Log($"  服务端删除空目录失败(将继续清理本地): {relativePath}");

                // 删本地（本地已确认是空的 ReparsePoint 占位符目录）
                try { Directory.Delete(dir); }
                catch (Exception ex)
                {
                    FileLogger.Log($"  删除本地空目录失败: {relativePath} - {ex.Message}");
                    break;
                }
                SyncStatusManager.Instance.AddLog("✅", $"清理空目录: {relativePath}");

                // 继续向上检查
                dir = Path.GetDirectoryName(dir);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  CleanupEmptyParentDirs 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查是否有同名的 pending delete（用于检测 move = delete + create）
    /// </summary>
    public bool TryCancelPendingDelete(string fileName, out string? oldRelativePath)
    {
        foreach (var kv in _pendingDeletes)
        {
            if (string.Equals(Path.GetFileName(kv.Key), fileName, StringComparison.OrdinalIgnoreCase))
            {
                if (_pendingDeletes.TryRemove(kv.Key, out _))
                {
                    oldRelativePath = kv.Key;
                    FileLogger.Log($"  取消 pending delete (检测到移动): {kv.Key}");
                    return true;
                }
            }
        }
        oldRelativePath = null;
        return false;
    }

    /// <summary>
    /// 重试单个失败的上传任务（从 UI 调用）
    /// </summary>
    public async Task RetryUploadAsync(TransferItem item)
    {
        if (item.Status != TransferStatus.Failed || item.Direction != TransferDirection.Upload)
            return;

        SyncStatusManager.Instance.RemoveTransfer(item);
        await Task.Run(() => UploadFileWithRetry(item.FullPath, item.FileName));
    }

    /// <summary>
    /// 重试所有失败的上传任务
    /// </summary>
    public async Task RetryAllFailedAsync()
    {
        var failedItems = SyncStatusManager.Instance.Transfers
            .Where(t => t.Status == TransferStatus.Failed && t.Direction == TransferDirection.Upload)
            .ToList();

        foreach (var item in failedItems)
            SyncStatusManager.Instance.RemoveTransfer(item);

        foreach (var item in failedItems)
            _ = Task.Run(() => UploadFileWithRetry(item.FullPath, item.FileName));
    }

    /// <summary>
    /// 等待文件可读后上传，最多重试 5 次
    /// </summary>
    /// <param name="forceUpload">为 true 时跳过 placeholder 检查（rename fallback 场景）</param>
    private async Task UploadFileWithRetry(string fullPath, string relativePath, bool forceUpload = false)
    {
        // 去重：已在上传中则跳过
        if (!_uploadingFiles.TryAdd(relativePath, true))
        {
            FileLogger.Log($"  UploadFileWithRetry 跳过(已在上传): {relativePath}");
            return;
        }

        // 在 UI 传输列表中显示（等待状态）
        var transferItem = new TransferItem
        {
            FileName = relativePath,
            FullPath = fullPath,
            Direction = TransferDirection.Upload,
            Status = TransferStatus.Waiting,
        };
        SyncStatusManager.Instance.AddTransfer(transferItem);
        bool transferEnded = false; // 跟踪是否已通过 RemoveTransfer 调用了 EndBusy

        // 获取上传槽位（最多 5 个并发上传）
        await _uploadSemaphore.WaitAsync();
        transferItem.Status = TransferStatus.Transferring;
        var uploadStart = DateTime.UtcNow;

        try
        {
            SyncStatusManager.Instance.AddLog("↑", $"上传: {relativePath}");

            // 分离两类重试：
            // - 文件被占用（IOException）：文件正在拷贝中，最多等 3 分钟，不计入上传重试次数
            // - 上传失败（服务器错误/网络问题）：最多重试 5 次
            int uploadAttempt = 0;
            var retryStart = DateTime.UtcNow;

            while (true)
            {
                // 文件占用超时：最多等 3 分钟
                if ((DateTime.UtcNow - retryStart).TotalMinutes > 3)
                {
                    FileLogger.Log($"  等待文件可读超时(3分钟)，放弃: {relativePath}");
                    break;
                }

                // 上传失败超过 5 次则放弃
                if (uploadAttempt >= 5)
                    break;

                // 再次检查文件是否存在
                if (!File.Exists(fullPath))
                {
                    FileLogger.Log($"  文件已消失，跳过: {relativePath}");
                    SyncStatusManager.Instance.RemoveTransfer(transferItem);
                    transferEnded = true;
                    return;
                }

                // 跳过已变成 placeholder 的文件（forceUpload 时允许上传 placeholder）
                if (!forceUpload)
                {
                    try
                    {
                        var fi = new FileInfo(fullPath);
                        if (fi.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint))
                        {
                            FileLogger.Log($"  已是 placeholder，跳过: {relativePath}");
                            SyncStatusManager.Instance.RemoveTransfer(transferItem);
                            transferEnded = true;
                            return;
                        }
                    }
                    catch { }
                }

                try
                {
                    long fileSize = 0;
                    try { fileSize = new FileInfo(fullPath).Length; } catch { }

                    var ok = await _api.UploadFileAsync(relativePath, fullPath, (uploaded, total) =>
                    {
                        double pct = total > 0 ? uploaded * 100.0 / total : 0;
                        transferItem.Progress = pct;
                        transferItem.Speed = FormatTransferSpeed(uploaded, total, uploadStart);
                    });

                    if (ok)
                    {
                        FileLogger.Log($"  上传成功: {relativePath}");
                        // 上传成功后立即设置 debounce，防止队列中积压的 ModifyFile 引发链式重复上传
                        _lastChangedTicks[relativePath] = DateTime.UtcNow.Ticks;
                        // 记录文件 mtime，用于在后续 HandleModifyFile 中判断文件是否真正被修改
                        try { _lastUploadedMtime[relativePath] = new FileInfo(fullPath).LastWriteTimeUtc.Ticks; } catch { }
                        ConvertToPlaceholderAndSync(fullPath, relativePath);
                        transferItem.Progress = 100;
                        transferItem.Status = TransferStatus.Completed;
                        transferItem.Speed = FormatFileSize(fileSize);
                        SyncStatusManager.Instance.AddLog("✅", $"已上传: {relativePath}");
                        return;
                    }
                    else
                    {
                        uploadAttempt++;
                        if (uploadAttempt < 5)
                        {
                            var delay = uploadAttempt * 3;
                            FileLogger.Log($"  上传失败，{delay}秒后重试({uploadAttempt}/5): {relativePath}");
                            await Task.Delay(delay * 1000);
                        }
                    }
                }
                catch (IOException)
                {
                    // 文件被占用（正在拷贝中），不计入上传重试次数，每 3 秒重试
                    FileLogger.Log($"  文件被占用(可能正在拷贝)，3秒后重试: {relativePath}");
                    await Task.Delay(3000);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"  上传异常: {ex.Message}");
                    break;
                }
            }

            // 上传失败
            FileLogger.Log($"  上传最终失败: {relativePath}");
            transferItem.Status = TransferStatus.Failed;
            SyncStatusManager.Instance.AddLog("❌", $"上传失败: {relativePath}");
        }
        finally
        {
            _uploadSemaphore.Release();
            _uploadingFiles.TryRemove(relativePath, out _);
            if (!transferEnded) TrayIconService.Current?.EndBusy();
        }
    }

    /// <summary>
    /// 跳过不需要同步的路径（当前不过滤任何文件）
    /// </summary>
    public static bool ShouldSkipPath(string relativePath)
    {
        return false;
    }

    /// <summary>
    /// 格式化传输速度和剩余时间
    /// </summary>
    private static string FormatTransferSpeed(long transferred, long total, DateTime startTime)
    {
        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
        if (elapsed < 0.5 || transferred <= 0) return FormatFileSize(total);

        double speed = transferred / elapsed;
        long remaining = total - transferred;
        double eta = speed > 0 ? remaining / speed : 0;

        string speedStr;
        if (speed >= 1024 * 1024) speedStr = $"{speed / 1024 / 1024:F1} MB/s";
        else if (speed >= 1024) speedStr = $"{speed / 1024:F0} KB/s";
        else speedStr = $"{speed:F0} B/s";

        if (eta < 1) return speedStr;
        if (eta < 60) return $"{speedStr} · 剩余 {eta:F0}s";
        if (eta < 3600) return $"{speedStr} · 剩余 {(int)(eta / 60)}:{(int)(eta % 60):D2}";
        return $"{speedStr} · 剩余 {(int)(eta / 3600)}:{(int)(eta % 3600 / 60):D2}:{(int)(eta % 60):D2}";
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes}B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1}KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024:F1}MB";
        return $"{bytes / 1024.0 / 1024 / 1024:F1}GB";
    }

    /// <summary>
    /// 将用户创建的文件/文件夹转为 Cloud Filter placeholder 并标记为已同步。
    /// 这会让资源管理器显示绿勾/白云图标（而不是蓝圈）。
    /// </summary>
    private void ConvertToPlaceholderAndSync(string fullPath, string relativePath)
    {
        FileLogger.Log($"ConvertToPlaceholderAndSync: {relativePath}");
        _recentlySynced[fullPath] = DateTime.UtcNow.Ticks;

        bool isDir = Directory.Exists(fullPath);

        using var handle = OpenFileForCldApi(fullPath);
        if (handle == null)
        {
            FileLogger.Log($"  无法打开文件句柄: {fullPath}");
            return;
        }

        try
        {
            // FileIdentity = UTF-8 编码的相对路径
            byte[] identity = Encoding.UTF8.GetBytes(relativePath);
            IntPtr identityPtr = Marshal.AllocHGlobal(identity.Length);
            Marshal.Copy(identity, 0, identityPtr, identity.Length);

            // 转换为 placeholder
            // 注意：不加 ENABLE_ON_DEMAND_POPULATION！
            // 此方法处理的是用户本地创建/拷贝的目录，内容已在磁盘上，
            // 加了会让 Windows 认为"需要从云端按需填充"→ 显示白云图标。
            // 云端目录的 on-demand population 由 FETCH_PLACEHOLDERS 回调处理。
            var convertFlags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC
                             | CF_CONVERT_FLAGS.CF_CONVERT_FLAG_FORCE_CONVERT_TO_CLOUD_FILE;

            int hr = CfConvertToPlaceholder(
                handle.DangerousGetHandle(),
                identityPtr, (uint)identity.Length,
                convertFlags,
                IntPtr.Zero, IntPtr.Zero);

            Marshal.FreeHGlobal(identityPtr);

            // 0x8007017C = 已经是 placeholder，可以忽略；只要后续 SetInSyncState 成功即可
            if (hr < 0 && hr != unchecked((int)0x8007017C))
                FileLogger.Log($"  CfConvertToPlaceholder 失败: 0x{hr:X8}");
            else
                FileLogger.Log($"  CfConvertToPlaceholder → 0x{hr:X8}");

            // 文件和目录都设置 IN_SYNC：
            // - 文件：在此处直接设置
            // - 目录：也在此处设置（修复重命名后目录无绿勾的问题）
            //   父目录的 IN_SYNC 由 FlushDirtyDirectories 统一处理
            hr = CfSetInSyncState(
                handle.DangerousGetHandle(),
                CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                IntPtr.Zero);
            FileLogger.Log($"  CfSetInSyncState → 0x{hr:X8}");

            // 通知 Explorer 刷新文件图标覆盖（解决蓝圈不自动变绿勾的问题）
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, fullPath);

            // 标记所有父目录为 RecentlySynced，
            // 防止 CfConvertToPlaceholder 触发的父目录 Changed 事件对 UNPINNED 目录误脱水
            MarkParentRecentlySynced(fullPath);

            // 不论文件还是目录，转 placeholder 都会清除父目录的 IN_SYNC（变白云）
            // 记录脏父目录，等所有操作完成后由 FlushDirtyDirectories 统一批量设回 IN_SYNC
            MarkParentDirectoriesDirty(fullPath);
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  ConvertToPlaceholderAndSync 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 标记所有父目录为 RecentlySynced，防止内部操作（CfConvertToPlaceholder / SHChangeNotify）
    /// 触发的父目录 Changed 事件对 UNPINNED 目录执行误脱水
    /// </summary>
    private void MarkParentRecentlySynced(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(dir) &&
               dir.StartsWith(_syncFolder, StringComparison.OrdinalIgnoreCase) &&
               dir.Length > _syncFolder.Length)
        {
            _recentlySynced[dir] = DateTime.UtcNow.Ticks;
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>
    /// 记录需要刷新 IN_SYNC 的父目录（向上遍历到同步根目录）
    /// </summary>
    private void MarkParentDirectoriesDirty(string fullPath)
    {
        var dir = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(dir) &&
               dir.StartsWith(_syncFolder, StringComparison.OrdinalIgnoreCase) &&
               dir.Length > _syncFolder.Length)
        {
            _dirtyDirectories.TryAdd(dir, 0);
            dir = Path.GetDirectoryName(dir);
        }
    }

    /// <summary>
    /// 批量刷新所有脏目录的 IN_SYNC 状态，从最深处开始向上设置
    /// </summary>
    public void FlushDirtyDirectories()
    {
        if (_dirtyDirectories.IsEmpty) return;

        // 取出并清空
        var dirs = _dirtyDirectories.Keys.ToList();
        _dirtyDirectories.Clear();

        // 按路径长度从长到短排序（最深的目录先处理）
        dirs.Sort((a, b) => b.Length.CompareTo(a.Length));

        FileLogger.Log($"FlushDirtyDirectories: 刷新 {dirs.Count} 个目录的 IN_SYNC 状态");

        // 预先标记所有待刷新的目录为 RecentlySynced，
        // 防止 SHChangeNotify 触发 Changed → TryHandleDehydrateRequest → 对 UNPINNED 目录误脱水
        foreach (var dir in dirs)
            _recentlySynced[dir] = DateTime.UtcNow.Ticks;

        foreach (var dir in dirs)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;

                using var handle = OpenFileForCldApi(dir);
                if (handle == null) continue;

                // 目录可能还不是 placeholder（CreateDirectory 时跳过了转换），先转换
                var relativePath = dir.Substring(_syncFolder.Length + 1).Replace('\\', '/');
                byte[] identity = Encoding.UTF8.GetBytes(relativePath);
                IntPtr identityPtr = Marshal.AllocHGlobal(identity.Length);
                Marshal.Copy(identity, 0, identityPtr, identity.Length);

                int hr = CfConvertToPlaceholder(
                    handle.DangerousGetHandle(),
                    identityPtr, (uint)identity.Length,
                    CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC
                    | CF_CONVERT_FLAGS.CF_CONVERT_FLAG_FORCE_CONVERT_TO_CLOUD_FILE,
                    IntPtr.Zero, IntPtr.Zero);

                Marshal.FreeHGlobal(identityPtr);

                // 无论 CfConvertToPlaceholder 返回什么，都显式设 IN_SYNC
                // 成功(0): MARK_IN_SYNC 已设，再设一次无害
                // 已是 placeholder(0x8007017C): 必须单独设
                // 其他错误: 尝试设 IN_SYNC 也无害
                CfSetInSyncState(
                    handle.DangerousGetHandle(),
                    CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                    CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                    IntPtr.Zero);

                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, dir);

                // 刷新后再次标记，确保 SHChangeNotify 引起的延迟 Changed 事件也被抑制
                _recentlySynced[dir] = DateTime.UtcNow.Ticks;
            }
            catch { }
        }
    }

    /// <summary>
    /// FETCH_DATA 下载完成后调用：设置文件 IN_SYNC + 刷新资源管理器图标 + 刷新父目录状态
    /// 解决水合完成后蓝色进度条/父目录蓝圈不变绿勾的问题
    /// </summary>
    public void SetInSyncAfterHydration(string fullPath)
    {
        try
        {
            // 记录 mtime，防止水合后超长延迟的 Changed 事件触发误上传
            // （_lastUploadedMtime 仅在 UploadFileWithRetry 成功后记录，
            //   下载/水合的文件没有记录，HandleModifyFile 的 mtime 检查会漏过）
            try
            {
                var fi = new FileInfo(fullPath);
                var relPath = Path.GetRelativePath(_syncFolder, fullPath).Replace('\\', '/');
                _lastUploadedMtime[relPath] = fi.LastWriteTimeUtc.Ticks;
                FileLogger.Log($"  \u8bb0\u5f55\u6c34\u5408mtime: {relPath} = {fi.LastWriteTimeUtc:o}");
            }
            catch { }

            using var handle = OpenFileForCldApi(fullPath);
            if (handle == null) return;

            int hr = CfSetInSyncState(
                handle.DangerousGetHandle(),
                CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                IntPtr.Zero);
            FileLogger.Log($"  SetInSyncAfterHydration: 0x{hr:X8}");

            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, fullPath);

            // 刷新父目录状态（蓝圈 → 绿勾）
            MarkParentDirectoriesDirty(fullPath);
            FlushDirtyDirectories();
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  SetInSyncAfterHydration 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 以适合 Cloud Filter API 的方式打开文件句柄
    /// 对只读文件会临时去掉只读属性以获取写权限
    /// </summary>
    private static SafeFileHandle? OpenFileForCldApi(string path)
    {
        try
        {
            bool removedReadOnly = false;
            var attrs = File.GetAttributes(path);
            if (attrs.HasFlag(System.IO.FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attrs & ~System.IO.FileAttributes.ReadOnly);
                removedReadOnly = true;
            }

            // FILE_FLAG_BACKUP_SEMANTICS (0x02000000) 用于打开目录
            var handle = CreateFileW(
                path,
                0x00000182, // FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | FILE_WRITE_DATA
                0x00000007, // FILE_SHARE_READ | WRITE | DELETE
                IntPtr.Zero,
                3, // OPEN_EXISTING
                0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                if (removedReadOnly) File.SetAttributes(path, attrs);
                return null;
            }

            // 句柄打开成功后恢复只读属性
            if (removedReadOnly) File.SetAttributes(path, attrs);

            return new SafeFileHandle(handle, ownsHandle: true);
        }
        catch
        {
            return null;
        }
    }

    // SHChangeNotify 常量
    private const uint SHCNE_UPDATEITEM = 0x00002000;
    private const uint SHCNE_UPDATEDIR  = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(
        uint wEventId, uint uFlags,
        [MarshalAs(UnmanagedType.LPWStr)] string? dwItem1,
        IntPtr dwItem2 = default);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    public void Dispose()
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        try { _consumerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _uploadSemaphore.Dispose();
    }
}
