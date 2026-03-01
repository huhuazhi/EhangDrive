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
