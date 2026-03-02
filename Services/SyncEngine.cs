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

    /// <summary>
    /// 检查路径是否在最近被同步处理过（用于过滤反馈事件）
    /// </summary>
    public bool IsRecentlySynced(string fullPath)
    {
        if (_recentlySynced.TryGetValue(fullPath, out var ticks))
            return (DateTime.UtcNow.Ticks - ticks) < TimeSpan.FromSeconds(10).Ticks;
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
                await HandleCreateFile(evt);
                break;
            case SyncEventType.ModifyFile:
                await HandleModifyFile(evt);
                break;
            case SyncEventType.DeleteItem:
                await HandleDelete(evt);
                break;
        }
    }

    /// <summary>
    /// 处理新建文件夹：延迟等待重命名 → 调 mkdir API → 转为placeholder
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
            FileLogger.Log($"  重命名失败，尝试直接创建: {evt.RelativePath}");
            if (Directory.Exists(evt.FullPath))
                ok = await _api.MkdirAsync(evt.RelativePath);
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
    /// 处理新文件创建：等待写入完成 → 上传 → 转为placeholder
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

        // 跳过 placeholder 文件（ReparsePoint 属性表示已是 Cloud Filter placeholder）
        try
        {
            var fi = new FileInfo(evt.FullPath);
            if (fi.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint))
            {
                FileLogger.Log($"  跳过 placeholder 文件: {evt.RelativePath}");
                return;
            }
        }
        catch { return; }

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

        // 跳过 placeholder / offline 文件（SetFileInSync 引起的属性变化 / 白云 dehydrated）
        try
        {
            var fi = new FileInfo(evt.FullPath);
            if (fi.Attributes.HasFlag(System.IO.FileAttributes.ReparsePoint)) return;
            if (fi.Attributes.HasFlag(System.IO.FileAttributes.Offline)) return;
        }
        catch { return; }

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
        await Task.Delay(500);
        if (!File.Exists(evt.FullPath)) return;

        await UploadFileWithRetry(evt.FullPath, evt.RelativePath);
    }

    /// <summary>
    /// 处理删除：延迟执行，防止 move（delete+create）被误删
    /// </summary>
    private async Task HandleDelete(SyncEvent evt)
    {
        FileLogger.Log($"HandleDelete: {evt.RelativePath}");

        // 入延迟队列
        _pendingDeletes[evt.RelativePath] = (evt.FullPath, DateTime.UtcNow.Ticks);
        SyncStatusManager.Instance.AddLog("🗑", $"检测到删除(排队): {evt.RelativePath}");

        // 延迟 3 秒再检查：如果这期间出现同名 Created 事件（move 场景），则取消删除
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
    private async Task UploadFileWithRetry(string fullPath, string relativePath)
    {
        // 去重：已在上传中则跳过
        if (!_uploadingFiles.TryAdd(relativePath, true))
        {
            FileLogger.Log($"  UploadFileWithRetry 跳过(已在上传): {relativePath}");
            return;
        }

        // 在 UI 传输列表中显示
        var transferItem = new TransferItem
        {
            FileName = relativePath,
            Direction = TransferDirection.Upload,
            Status = TransferStatus.Transferring,
        };
        SyncStatusManager.Instance.AddTransfer(transferItem);

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

                // 跳过已变成 placeholder 的文件
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
                        ConvertToPlaceholderAndSync(fullPath, relativePath);
                        transferItem.Progress = 100;
                        transferItem.Status = TransferStatus.Completed;
                        SyncStatusManager.Instance.AddLog("✅", $"已上传: {relativePath}");

                        // 3 秒后移除传输条目
                        _ = Task.Delay(3000).ContinueWith(_ =>
                            SyncStatusManager.Instance.RemoveTransfer(transferItem));
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
            _ = Task.Delay(5000).ContinueWith(_ =>
                SyncStatusManager.Instance.RemoveTransfer(transferItem));
        }
        finally
        {
            _uploadingFiles.TryRemove(relativePath, out _);
        }
    }

    /// <summary>
    /// 跳过不需要同步的路径（.git, node_modules, desktop.ini 等）
    /// </summary>
    public static bool ShouldSkipPath(string relativePath)
    {
        var parts = relativePath.Replace('\\', '/').Split('/');
        foreach (var p in parts)
        {
            if (p is ".git" or ".svn" or ".hg" or "node_modules")
                return true;
        }
        var name = parts[^1];
        return name is ".DS_Store" or "desktop.ini" or "Thumbs.db";
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
            var convertFlags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC
                             | CF_CONVERT_FLAGS.CF_CONVERT_FLAG_FORCE_CONVERT_TO_CLOUD_FILE;
            if (isDir)
                convertFlags |= CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION;

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
    }
}
