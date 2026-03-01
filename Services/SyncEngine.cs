using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using EhangNAS_Sync.Models;
using static EhangNAS_Sync.Native.CldApi;
using Microsoft.Win32.SafeHandles;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 同步事件指令
/// </summary>
public record SyncEvent(SyncEventType Type, string FullPath, string RelativePath);

public enum SyncEventType
{
    CreateDirectory,
    // 后续扩展：CreateFile, ModifyFile, Delete, Rename ...
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
        }
    }

    /// <summary>
    /// 处理新建文件夹：调 mkdir API → 设绿勾
    /// </summary>
    private async Task HandleCreateDirectory(SyncEvent evt)
    {
        FileLogger.Log($"HandleCreateDirectory: {evt.RelativePath} ({evt.FullPath})");
        SyncStatusManager.Instance.AddLog("🔵", $"开始同步文件夹: {evt.RelativePath}");

        var ok = await _api.MkdirAsync(evt.RelativePath);
        FileLogger.Log($"  MkdirAsync → {ok}");

        if (ok)
        {
            SetInSync(evt.FullPath);
            SyncStatusManager.Instance.AddLog("✅", $"文件夹已同步: {evt.RelativePath}");
        }
        else
        {
            SyncStatusManager.Instance.AddLog("❌", $"同步文件夹失败: {evt.RelativePath}");
        }
    }

    /// <summary>
    /// 将用户创建的文件/文件夹标记为已同步
    /// 注意：CfConvertToPlaceholder 在 CF 回调线程池环境中会死锁，
    /// 因此暂时跳过 placeholder 转换，仅做服务端同步。
    /// TODO: 后续在独立线程上下文中处理 placeholder 转换
    /// </summary>
    private static void SetInSync(string fullPath)
    {
        FileLogger.Log($"SetInSync: {fullPath} (跳过placeholder转换)");
        SyncStatusManager.Instance.AddLog("🟢", $"已同步: {Path.GetFileName(fullPath)}");
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
                0x00000100, // FILE_WRITE_ATTRIBUTES (CfConvertToPlaceholder需要写权限)
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
