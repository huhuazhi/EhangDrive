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

    // Pin 后定时刷新：跟踪已调度的目录，防止重复调度
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pinRefreshTasks = new(StringComparer.OrdinalIgnoreCase);

    // 全量扫描防并发：0=空闲, 1=扫描排队或进行中
    private int _scanPending;

    // 延迟批量目录清理：收集所有待清理的父目录路径，批量处理避免并发竞争
    private readonly ConcurrentDictionary<string, byte> _pendingCleanupDirs = new(StringComparer.OrdinalIgnoreCase);
    private int _cleanupRunning; // 0=空闲, 1=运行中

    // ── 删除目录树爆发抑制 ──
    // 已确认为正在删除的目录树前缀集合，key=fullPath前缀
    private readonly ConcurrentDictionary<string, long> _suppressedTrees = new(StringComparer.OrdinalIgnoreCase);
    private const int SUPPRESS_DURATION_S = 60; // 抑制持续时间

    /// <summary>
    /// 检查指定的相对路径（或其任意祖先目录）是否在待删除队列中。
    /// </summary>
    public bool HasPendingDelete(string relativePath)
    {
        if (_pendingDeletes.ContainsKey(relativePath))
            return true;
        // 祖先目录也在待删除队列中 → 子路径也算待删除
        var parts = relativePath.Split('/');
        for (int i = parts.Length - 1; i > 0; i--)
        {
            var ancestor = string.Join('/', parts, 0, i);
            if (_pendingDeletes.ContainsKey(ancestor))
                return true;
        }
        return false;
    }

    /// <summary>
    /// 检查路径是否位于被抑制的目录树下。
    /// FileWatcher 的所有事件入口和 FETCH_DATA 回调使用此方法过滤。
    /// </summary>
    public bool IsInSuppressedTree(string fullPath)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        foreach (var kv in _suppressedTrees)
        {
            if ((nowTicks - kv.Value) > TimeSpan.FromSeconds(SUPPRESS_DURATION_S).Ticks)
            {
                _suppressedTrees.TryRemove(kv.Key, out _);
                continue;
            }
            if (fullPath.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// HandleDelete 中 Deleted 事件到达时，主动将该目录树标记为 suppressed。
    /// </summary>
    public void MarkTreeSuppressed(string fullPath)
    {
        // 只标记目录（文件删除不需要标记树）
        var dir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrEmpty(dir)) return;

        // 如果 dir 已经在 suppressedTrees 中就不需要再标
        if (IsInSuppressedTree(dir)) return;

        // 检测同一目录下是否有多个 pending delete（说明是目录树删除）
        int count = 0;
        foreach (var kv in _pendingDeletes)
        {
            var pd = Path.GetDirectoryName(Path.Combine(_syncFolder, kv.Key.Replace('/', '\\')));
            if (!string.IsNullOrEmpty(pd) && pd.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
            {
                count++;
                if (count >= 3)
                {
                    _suppressedTrees[dir] = DateTime.UtcNow.Ticks;
                    var rel = Path.GetRelativePath(_syncFolder, dir).Replace('\\', '/');
                    FileLogger.Log($"[DeleteDetect] Deleted 事件确认目录树删除，抑制: {rel}");
                    break;
                }
            }
        }
    }

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

    // ── 0x8007016B 元数据损坏修复 ──
    private const int HR_CLOUD_FILE_METADATA_CORRUPT = unchecked((int)0x8007016B);

    /// <summary>
    /// 检查 IOException 是否为 Cloud File 元数据损坏 (0x8007016B)
    /// </summary>
    public static bool IsMetadataCorrupt(Exception ex)
        => ex is IOException io && io.HResult == HR_CLOUD_FILE_METADATA_CORRUPT;

    /// <summary>
    /// 强制删除元数据损坏的 Cloud Filter placeholder。
    /// 通过管理员权限临时 detach cldflt mini-filter 来绕过元数据校验。
    /// </summary>
    public static bool ForceDeleteCorruptPlaceholder(string fullPath)
    {
        try
        {
            var volume = Path.GetPathRoot(fullPath)?.TrimEnd('\\') ?? "C:";
            var isDir = Directory.Exists(fullPath);

            // 构造 PowerShell 命令：detach → 删除 → attach
            var deleteCmd = isDir
                ? $"Remove-Item -LiteralPath '{fullPath}' -Recurse -Force -ErrorAction Stop"
                : $"Remove-Item -LiteralPath '{fullPath}' -Force -ErrorAction Stop";

            var psScript = string.Join("; ",
                $"fltmc detach cldflt {volume} 2>$null",
                "Start-Sleep -Milliseconds 200",
                deleteCmd,
                $"fltmc attach cldflt {volume} 2>$null");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{psScript}\"",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(15000);

            var deleted = !File.Exists(fullPath) && !Directory.Exists(fullPath);
            FileLogger.Log($"ForceDeleteCorrupt: {(deleted ? "成功" : "失败")} - {fullPath}");
            return deleted;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // 用户拒绝 UAC 提升
            FileLogger.Log($"ForceDeleteCorrupt: 用户拒绝提升权限 - {fullPath}");
            return false;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"ForceDeleteCorrupt: 异常 - {fullPath}: {ex.Message}");
            return false;
        }
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

    /// <summary>
    /// 启动时扫描同步目录下所有 placeholder 文件，记录 mtime。
    /// 这样 HandleModifyFile 可以通过比较 mtime 跳过非内容修改的 Changed 事件（如属性/PinState变化）。
    /// </summary>
    public void PopulateInitialMtimes()
    {
        try
        {
            int count = 0;
            foreach (var file in Directory.GetFiles(_syncFolder, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    var relPath = Path.GetRelativePath(_syncFolder, file).Replace('\\', '/');
                    _lastUploadedMtime[relPath] = fi.LastWriteTimeUtc.Ticks;
                    count++;
                }
                catch { }
            }
            FileLogger.Log($"PopulateInitialMtimes: 已记录 {count} 个文件的 mtime");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"PopulateInitialMtimes 异常: {ex.Message}");
        }
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
            // 安全网：FETCH_DATA 水合后 SetInSyncAfterHydration 设置了 IN_SYNC，
            // 但驱动异步完成可能再次清除。此处确保恢复，防止蓝圈。
            SyncProviderConnection.SetItemInSyncPublic(evt.FullPath);
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
            SyncProviderConnection.SetItemInSyncPublic(evt.FullPath);
            return;
        }

        try
        {
            var fi = new FileInfo(evt.FullPath);
            // Offline = cloud-only 白云文件（未 hydrate），不需要上传
            // PinState 异步传播会清除 IN_SYNC（蓝圈），需要完整恢复（含 SHChangeNotify + 父目录刷新）
            if (fi.Attributes.HasFlag(System.IO.FileAttributes.Offline))
            {
                FileLogger.Log($"  跳过 Offline 文件(白云): {evt.RelativePath}");
                SetInSyncAfterHydration(evt.FullPath);
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
                // WPS/Office 以读写模式打开文件后关闭（不保存），Cloud Filter 驱动会清除
                // IN_SYNC 标志（蓝圈），但文件内容实际未变。此处需要重新设置 IN_SYNC。
                SetInSyncAfterHydration(evt.FullPath);
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

                // ── 注册空目录清理（延迟批量执行，避免并发竞争）──
                ScheduleDirectoryCleanup(pending.fullPath);
            }
            else
                SyncStatusManager.Instance.AddLog("❌", $"服务端删除失败: {evt.RelativePath}");
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// 注册一个已删除子项的父目录链到待清理集合，并启动延迟批量清理。
    /// 删除大目录树时，数百个 HandleDelete 并发完成，每个都注册父目录，
    /// 但只有一个 RunDeferredCleanupAsync 实例实际执行清理，避免并发竞争。
    /// </summary>
    private void ScheduleDirectoryCleanup(string childFullPath)
    {
        var dir = Path.GetDirectoryName(childFullPath);
        while (!string.IsNullOrEmpty(dir)
               && !string.Equals(dir, _syncFolder, StringComparison.OrdinalIgnoreCase))
        {
            _pendingCleanupDirs[dir] = 0;
            dir = Path.GetDirectoryName(dir);
        }

        // 只允许一个清理任务运行
        if (Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) == 0)
        {
            _ = Task.Run(RunDeferredCleanupAsync);
        }
    }

    /// <summary>
    /// 延迟批量清理空的占位符目录。
    /// 等待删除风暴平息后，从最深目录开始逐级向上清理。
    /// 单线程执行，避免并发竞争和重复 API 调用。
    /// </summary>
    private async Task RunDeferredCleanupAsync()
    {
        try
        {
            // 等待删除风暴平息（HandleDelete 有 3 秒延迟 + 服务端请求时间）
            await Task.Delay(5000);

            // 可能持续有新路径注册，循环处理直到稳定
            while (true)
            {
                // 快照并清空待清理集合
                var dirs = _pendingCleanupDirs.Keys.ToList();
                foreach (var d in dirs) _pendingCleanupDirs.TryRemove(d, out _);

                if (dirs.Count == 0) break;

                // 按路径长度降序（最深目录优先），实现自底向上清理
                dirs.Sort((a, b) => b.Length.CompareTo(a.Length));

                FileLogger.Log($"[DeferredCleanup] 开始批量清理，待处理目录数: {dirs.Count}");

                foreach (var dir in dirs)
                {
                    try
                    {
                        if (!Directory.Exists(dir)) continue;
                        var di = new DirectoryInfo(dir);

                        // 只清理占位符目录（ReparsePoint = Cloud Filter placeholder）
                        if (!di.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                        // 先清理该目录下所有空的 placeholder 子目录（FSW 溢出可能漏掉的兄弟节点）
                        // 获取服务端目录内容，判断子目录是否仍在服务端（仍存在=合法目录，不清理）
                        var relativePath = Path.GetRelativePath(_syncFolder, dir).Replace('\\', '/');
                        HashSet<string> serverNames;
                        try
                        {
                            var serverItems = await _api.ListDirectoryAsync(relativePath);
                            serverNames = new HashSet<string>(serverItems.Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
                        }
                        catch { serverNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase); }

                        foreach (var sub in di.EnumerateDirectories())
                        {
                            try
                            {
                                if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint)
                                    && !sub.EnumerateFileSystemInfos().Any())
                                {
                                    // 服务端仍存在的目录是合法目录，不清理
                                    if (serverNames.Contains(sub.Name)) continue;

                                    var subRel = Path.GetRelativePath(_syncFolder, sub.FullName).Replace('\\', '/');
                                    FileLogger.Log($"  [DeferredCleanup] 清理空兄弟目录: {subRel}");
                                    // 标记抑制，防止 FileWatcher.Deleted 触发 HandleDelete → MarkTreeSuppressed
                                    SuppressForModList(sub.FullName);
                                    try { sub.Delete(); } catch { }
                                }
                            }
                            catch { }
                        }

                        // 刷新：兄弟清理后重新检查是否为空
                        di.Refresh();
                        if (!Directory.Exists(dir)) continue;
                        if (di.EnumerateFileSystemInfos().Any()) continue;

                        // 检查该目录在服务端是否仍然存在 → 存在则跳过（合法目录，不应因子文件删除而被清理）
                        var dirMeta = await _api.GetFileMetaAsync(relativePath);
                        if (dirMeta != null)
                        {
                            FileLogger.Log($"  [DeferredCleanup] 跳过清理(服务端仍存在): {relativePath}");
                            continue;
                        }

                        FileLogger.Log($"  [DeferredCleanup] 清理空占位符目录: {relativePath}");

                        // 标记抑制，防止 FileWatcher.Deleted 触发 HandleDelete → MarkTreeSuppressed
                        SuppressForModList(dir);

                        // 标记父目录为 RecentlySynced，防止删除触发 Changed → UNPINNED 误脱水
                        var parentDir = Path.GetDirectoryName(dir);
                        if (!string.IsNullOrEmpty(parentDir) && parentDir.Length > _syncFolder.Length)
                            _recentlySynced[parentDir] = DateTime.UtcNow.Ticks;

                        // 本地删除，带重试（并发的脱水/水合操作可能仍持有句柄）
                        for (int retry = 0; retry < 5; retry++)
                        {
                            try
                            {
                                Directory.Delete(dir);
                                SyncStatusManager.Instance.AddLog("✅", $"清理空目录: {relativePath}");
                                break;
                            }
                            catch (UnauthorizedAccessException) when (retry < 4)
                            {
                                FileLogger.Log($"  [DeferredCleanup] 删除失败(重试 {retry + 1}/5): {relativePath}");
                                await Task.Delay(2000);
                            }
                            catch (Exception ex)
                            {
                                FileLogger.Log($"  [DeferredCleanup] 删除失败: {relativePath} - {ex.Message}");
                                if (IsMetadataCorrupt(ex))
                                    ForceDeleteCorruptPlaceholder(dir);
                                break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        FileLogger.Log($"  [DeferredCleanup] 处理目录异常: {dir} - {ex.Message}");
                        if (IsMetadataCorrupt(ex))
                            ForceDeleteCorruptPlaceholder(dir);
                    }
                }

                // 等待一会儿看看是否有新路径注册（清理过程中可能触发新事件）
                await Task.Delay(3000);
            }

            FileLogger.Log("[DeferredCleanup] 批量清理完成");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"[DeferredCleanup] 异常: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupRunning, 0);

            // 如果在清理期间又有新路径注册，再启动一轮
            if (!_pendingCleanupDirs.IsEmpty
                && Interlocked.CompareExchange(ref _cleanupRunning, 1, 0) == 0)
            {
                _ = Task.Run(RunDeferredCleanupAsync);
            }
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
    public void MarkDirectoryDirty(string fullPath)
    {
        _dirtyDirectories.TryAdd(fullPath, 0);
    }

    public void MarkParentDirectoriesDirty(string fullPath)
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
    /// Pin 目录后调度定时刷新 IN_SYNC 状态。
    /// Windows Cloud Filter 驱动会在 Pin 后 1-2 分钟异步传播 PinState 到所有子项，
    /// 此过程在 mini-filter 层清除 IN_SYNC，不会触发 FileSystemWatcher 事件，
    /// 因此必须主动定时刷新来恢复。
    /// </summary>
    public void SchedulePinRefresh(string directoryPath)
    {
        // 取消之前对同一目录的刷新任务
        if (_pinRefreshTasks.TryRemove(directoryPath, out var oldCts))
        {
            oldCts.Cancel();
            oldCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _pinRefreshTasks[directoryPath] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                // PinState 传播通常在 1-2 分钟内完成，在 15s/45s/90s/150s 各刷新一次
                var delaysMs = new[] { 15_000, 30_000, 45_000, 60_000 };
                foreach (var delay in delaysMs)
                {
                    await Task.Delay(delay, cts.Token);
                    RefreshDirectoryTreeInSync(directoryPath);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                FileLogger.Log($"SchedulePinRefresh 异常: {ex.Message}");
            }
            finally
            {
                _pinRefreshTasks.TryRemove(directoryPath, out _);
                cts.Dispose();
            }
        });
    }

    /// <summary>
    /// 主动刷新目录树中所有项的 IN_SYNC 状态 + 通知 Explorer 刷新图标。
    /// 用于对抗 Cloud Filter 驱动异步清除 IN_SYNC 的场景。
    /// </summary>
    private void RefreshDirectoryTreeInSync(string directoryPath)
    {
        try
        {
            if (!Directory.Exists(directoryPath)) return;

            FileLogger.Log($"Pin 定时刷新 IN_SYNC: {directoryPath}");

            // 刷新所有文件的 IN_SYNC + 通知 Explorer
            foreach (var file in Directory.GetFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var fi = new FileInfo(file);
                    if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    SyncProviderConnection.SetItemInSyncPublic(file);
                    SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, file);
                    MarkRecentlySynced(file);
                }
                catch { }
            }

            // 刷新所有目录的 IN_SYNC（通过 FlushDirtyDirectories 机制）
            MarkDirectoryDirty(directoryPath);
            foreach (var subDir in Directory.GetDirectories(directoryPath, "*", SearchOption.AllDirectories))
                MarkDirectoryDirty(subDir);
            MarkParentDirectoriesDirty(directoryPath);
            FlushDirtyDirectories();
        }
        catch (Exception ex)
        {
            FileLogger.Log($"RefreshDirectoryTreeInSync 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 记录文件的 mtime 到 _lastUploadedMtime，防止后续 Changed 事件触发误上传。
    /// 用于已水合文件 Pin/Unpin 属性变化场景。
    /// </summary>
    public void RecordFileMtime(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            var relPath = Path.GetRelativePath(_syncFolder, fullPath).Replace('\\', '/');
            _lastUploadedMtime[relPath] = fi.LastWriteTimeUtc.Ticks;
        }
        catch { }
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

    /// <summary>
    /// FileSystemWatcher 缓冲区溢出时调用：延迟后全量扫描，补偿丢失的事件。
    /// 扫描只对"非 placeholder"的文件/目录入队，已同步的 placeholder 自动跳过。
    /// </summary>
    public void EnqueueFullScan()
    {
        // 防止多次连续溢出触发多次并发扫描
        if (Interlocked.CompareExchange(ref _scanPending, 1, 0) != 0)
        {
            FileLogger.Log("全量扫描: 已有扫描在排队/进行中，跳过");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                // 等待 5 秒，让正在进行的解压/创建操作以及当前队列尽量处理完
                FileLogger.Log("全量扫描: 等待 5 秒后开始...");
                await Task.Delay(5000);

                FileLogger.Log("全量扫描: 开始扫描未同步的文件/目录...");
                int fileCount = 0, dirCount = 0;
                var syncDi = new DirectoryInfo(_syncFolder);

                foreach (var entry in syncDi.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0, // 不跳过任何属性
                }))
                {
                    // 跳过已是 placeholder 的项（Cloud Filter 已管理）
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                        continue;

                    var relativePath = Path.GetRelativePath(_syncFolder, entry.FullName)
                                           .Replace('\\', '/');

                    if (ShouldSkipPath(relativePath)) continue;

                    if (entry is DirectoryInfo)
                    {
                        dirCount++;
                        Enqueue(new SyncEvent(SyncEventType.CreateDirectory, entry.FullName, relativePath));
                    }
                    else if (entry is FileInfo)
                    {
                        fileCount++;
                        Enqueue(new SyncEvent(SyncEventType.CreateFile, entry.FullName, relativePath));
                    }
                }

                FileLogger.Log($"全量扫描完成: 发现 {fileCount} 个未同步文件, {dirCount} 个未同步目录");
                if (fileCount + dirCount > 0)
                    SyncStatusManager.Instance.AddLog("🔍", $"缓冲区溢出补偿: 发现 {fileCount} 个文件 + {dirCount} 个目录");

                // ── 第二阶段：扫描 placeholder 文件的 PinState 不匹配 ──
                // 缓冲区溢出时 Pin/Unpin 的 Changed 事件可能丢失，
                // 需要扫描修复 PINNED+脱水（应水合）和 UNPINNED+水合（应脱水）的文件。
                FileLogger.Log("全量扫描: 开始 PinState 不匹配修复...");
                int pinFixed = 0, unpinFixed = 0;
                foreach (var entry in syncDi.EnumerateFileSystemInfos("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = 0,
                }))
                {
                    if (entry is not FileInfo pfi) continue;
                    if (!pfi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                    try
                    {
                        bool isOffline = pfi.Attributes.HasFlag(FileAttributes.Offline);
                        uint pinState = SyncProviderConnection.GetFilePinState(pfi.FullName);

                        if (pinState == 1 && isOffline) // PINNED 但未水合
                        {
                            if (SyncProviderConnection.TryHandlePinRequest(pfi.FullName))
                                pinFixed++;
                        }
                        else if (pinState == 2 && !isOffline) // UNPINNED 但未脱水
                        {
                            if (SyncProviderConnection.TryHandleDehydrateRequest(pfi.FullName))
                                unpinFixed++;
                        }
                    }
                    catch { }
                }
                FileLogger.Log($"全量扫描 PinState: 水合 {pinFixed} 个 PINNED 文件, 脱水 {unpinFixed} 个 UNPINNED 文件");
                if (pinFixed + unpinFixed > 0)
                    SyncStatusManager.Instance.AddLog("🔍", $"PinState 修复: 水合 {pinFixed} + 脱水 {unpinFixed}");
            }
            catch (Exception ex)
            {
                FileLogger.Log($"全量扫描异常: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _scanPending, 0);
            }
        });
    }

    public void Dispose()
    {
        _channel.Writer.Complete();
        _cts.Cancel();
        try { _consumerTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts.Dispose();
        _uploadSemaphore.Dispose();
    }
}
