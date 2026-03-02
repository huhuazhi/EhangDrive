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

    // 延迟删除队列（防止 move = delete + create 导致误删服务端文件）
    // key=relativePath, value=(fullPath, enqueueTicks)
    private readonly ConcurrentDictionary<string, (string fullPath, long ticks)> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);

    // 并发上传控制：最多同时上传 5 个文件
    private readonly SemaphoreSlim _uploadSemaphore = new(5, 5);

    /// <summary>
    /// 检查路径是否在最近被同步处理过（用于过滤反馈事件）
    /// </summary>
    public bool IsRecentlySynced(string fullPath)
    {
        if (_recentlySynced.TryGetValue(fullPath, out var ticks))
            return (DateTime.UtcNow.Ticks - ticks) < TimeSpan.FromSeconds(2).Ticks;
        return false;
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
    /// 生产者入口：丢一个同步事件进来，立即返回
    /// </summary>
    public void Enqueue(SyncEvent evt)
    {
        _channel.Writer.TryWrite(evt);
    }

    /// <summary>
    /// 消费者循环
    /// </summary>
    private async Task ConsumeLoop(CancellationToken ct)
    {
        await foreach (var evt in _channel.Reader.ReadAllAsync(ct))
        {
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
                await HandleCreateDirectory(evt);
                break;
            case SyncEventType.RenameItem:
                await HandleRename(evt);
                break;
            case SyncEventType.CreateFile:
                // 并发处理：不阻塞消费者，由信号量控制并发数
                _ = Task.Run(async () =>
                {
                    try { await HandleCreateFile(evt); }
                    catch (Exception ex) { SyncStatusManager.Instance.AddLog("❌", $"处理异常: {ex.Message}"); }
                });
                break;
            case SyncEventType.ModifyFile:
                // 并发处理
                _ = Task.Run(async () =>
                {
                    try { await HandleModifyFile(evt); }
                    catch (Exception ex) { SyncStatusManager.Instance.AddLog("❌", $"处理异常: {ex.Message}"); }
                });
                break;
            case SyncEventType.DeleteItem:
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
        // 如果重命名了，原路径就不存在了，由 RenameItem 事件处理
        await Task.Delay(1500);

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
            ConvertToPlaceholderAndSync(evt.FullPath, evt.RelativePath);
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
        SyncStatusManager.Instance.AddLog("🔵", $"重命名: {evt.OldRelativePath} → {evt.RelativePath}");

        bool ok = false;
        try
        {
            ok = await _api.RenameAsync(evt.OldRelativePath!, evt.RelativePath);
            FileLogger.Log($"  RenameAsync → {ok}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  RenameAsync 异常: {ex.Message}");
        }

        if (!ok)
        {
            // Fallback: 服务端可能还没有旧路径(Created被延迟跳过了)，直接创建新路径
            FileLogger.Log($"  重命名失败，尝试直接创建/上传: {evt.RelativePath}");
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
            SyncStatusManager.Instance.AddLog("✅", $"已重命名: {evt.RelativePath}");
        }
        else
        {
            SyncStatusManager.Instance.AddLog("❌", $"重命名失败: {evt.RelativePath}");
        }
    }

    /// <summary>
    /// 处理新文件创建：先检测 move → 等待写入完成 → 上传 → 转为placeholder
    /// </summary>
    private async Task HandleCreateFile(SyncEvent evt)
    {
        FileLogger.Log($"HandleCreateFile: {evt.RelativePath} ({evt.FullPath})");

        // 短暂延迟，等文件写入完成
        await Task.Delay(500);

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

            // 二次检查：本地是否又出现了
            if (File.Exists(pending.fullPath) || Directory.Exists(pending.fullPath))
            {
                FileLogger.Log($"  取消删除(路径已恢复): {evt.RelativePath}");
                SyncStatusManager.Instance.AddLog("🔙", $"取消删除(已恢复): {evt.RelativePath}");
                return;
            }

            // 执行服务端删除
            SyncStatusManager.Instance.AddLog("🗑", $"删除服务端: {evt.RelativePath}");
            var ok = await _api.DeleteAsync(evt.RelativePath);
            FileLogger.Log($"  DeleteAsync → {ok}");

            if (ok)
                SyncStatusManager.Instance.AddLog("✅", $"服务端已删除: {evt.RelativePath}");
            else
                SyncStatusManager.Instance.AddLog("❌", $"服务端删除失败: {evt.RelativePath}");
        });

        return Task.CompletedTask;
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
            Direction = TransferDirection.Upload,
            Status = TransferStatus.Waiting,
        };
        SyncStatusManager.Instance.AddTransfer(transferItem);

        // 获取上传槽位（最多 5 个并发上传）
        await _uploadSemaphore.WaitAsync();
        transferItem.Status = TransferStatus.Transferring;

        try
        {
            SyncStatusManager.Instance.AddLog("↑", $"上传: {relativePath}");

            for (int attempt = 1; attempt <= 5; attempt++)
            {
                // 再次检查文件是否存在
                if (!File.Exists(fullPath))
                {
                    FileLogger.Log($"  文件已消失，跳过: {relativePath}");
                    SyncStatusManager.Instance.RemoveTransfer(transferItem);
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
                        transferItem.Speed = FormatSpeed(uploaded, total);
                    });

                    if (ok)
                    {
                        FileLogger.Log($"  上传成功: {relativePath}");
                        // 上传成功后立即设置 debounce，防止队列中积压的 ModifyFile 引发链式重复上传
                        _lastChangedTicks[relativePath] = DateTime.UtcNow.Ticks;
                        ConvertToPlaceholderAndSync(fullPath, relativePath);
                        transferItem.Progress = 100;
                        transferItem.Status = TransferStatus.Completed;
                        SyncStatusManager.Instance.AddLog("✅", $"已上传: {relativePath}");
                        return;
                    }
                    else if (attempt < 5)
                    {
                        FileLogger.Log($"  上传失败，{attempt * 3}秒后重试({attempt}/5): {relativePath}");
                        await Task.Delay(attempt * 3000);
                    }
                }
                catch (IOException) when (attempt < 5)
                {
                    FileLogger.Log($"  文件被占用，{attempt * 3}秒后重试({attempt}/5): {relativePath}");
                    await Task.Delay(attempt * 3000);
                }
                catch (Exception ex)
                {
                    FileLogger.Log($"  上传异常: {ex.Message}");
                    break;
                }
            }

            // 上传失败
            transferItem.Status = TransferStatus.Failed;
            SyncStatusManager.Instance.AddLog("❌", $"上传失败: {relativePath}");
        }
        finally
        {
            _uploadSemaphore.Release();
            _uploadingFiles.TryRemove(relativePath, out _);
        }
    }

    /// <summary>
    /// 跳过不需要同步的路径（当前不过滤任何文件）
    /// </summary>
    public static bool ShouldSkipPath(string relativePath)
    {
        return false;
    }

    private static string FormatSpeed(long uploaded, long total)
    {
        if (total <= 0) return "";
        return FormatFileSize(total);
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

            // 设置同步状态
            hr = CfSetInSyncState(
                handle.DangerousGetHandle(),
                CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                IntPtr.Zero);

            FileLogger.Log($"  CfSetInSyncState → 0x{hr:X8}");

            // 通知 Explorer 刷新文件图标覆盖（解决蓝圈不自动变绿勾的问题）
            SHChangeNotify(SHCNE_UPDATEITEM, SHCNF_PATHW, fullPath);

            // 同时通知父目录刷新聚合同步状态
            var parentDir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(parentDir))
                SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, parentDir);
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  ConvertToPlaceholderAndSync 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 以适合 Cloud Filter API 的方式打开文件句柄
    /// </summary>
    private static SafeFileHandle? OpenFileForCldApi(string path)
    {
        try
        {
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
                return null;

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
    /// 启动时扫描同步目录下所有文件/文件夹，将已有的 placeholder 重新标记为 IN_SYNC。
    /// 解决 APP 重启后绿勾变蓝圈的问题。
    /// </summary>
    public void RestoreInSyncState()
    {
        FileLogger.Log("RestoreInSyncState: 开始扫描...");
        int count = 0;
        try
        {
            foreach (var entry in new DirectoryInfo(_syncFolder).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                try
                {
                    // 只处理 placeholder 文件（带 ReparsePoint 属性）
                    if (!entry.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint))
                        continue;

                    using var handle = OpenFileForCldApi(entry.FullName);
                    if (handle == null) continue;

                    int hr = CfSetInSyncState(
                        handle.DangerousGetHandle(),
                        CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                        CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                        IntPtr.Zero);

                    if (hr >= 0) count++;
                }
                catch { /* 跳过无法访问的文件 */ }
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"RestoreInSyncState 异常: {ex.Message}");
        }
        FileLogger.Log($"RestoreInSyncState: 完成，已恢复 {count} 个文件的同步状态");

        // 通知 Explorer 刷新整个同步目录
        SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, _syncFolder);
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
