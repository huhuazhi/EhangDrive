using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using EhangNAS_Sync.Models;
using EhangNAS_Sync.Native;
using static EhangNAS_Sync.Native.CldApi;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 管理 Cloud Filter 同步根连接的生命周期。
/// 进程存活期间保持连接，Windows 才不会报"云文件提供程序未运行"。
/// </summary>
public sealed class SyncProviderConnection : IDisposable
{
    private long _connectionKey;
    private bool _connected;

    // 静态字段，供回调函数访问
    private static SyncApiService? _api;
    private static string? _syncFolder;
    private static SyncEngine? _syncEngine;

    // 必须持有委托引用，防止 GC 回收导致回调崩溃
    private CF_CALLBACK? _fetchPlaceholdersCallback;
    private CF_CALLBACK? _fetchDataCallback;
    private CF_CALLBACK? _notifyDehydrateCallback;

    /// <summary>
    /// 脱水冷却：记录每个路径最后一次脱水时间，防止重复脱水导致的无限循环。
    /// 目录 UNPINNED 状态是永久的，每次子文件变化都会触发父目录 Changed 事件，
    /// 若不加冷却，会导致反复脱水→水合→脱水的无限循环。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _dehydrateCooldown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 连接到已注册的同步根目录
    /// </summary>
    /// <summary>
    /// 延迟设置 SyncEngine（Connect 时可能尚未创建）
    /// </summary>
    public static void SetSyncEngine(SyncEngine engine)
    {
        _syncEngine = engine;
    }

    public void Connect(string syncFolderPath, SyncApiService api)
    {
        if (_connected) return;

        _api = api;
        _syncFolder = syncFolderPath;

        // 创建回调委托并持有引用
        _fetchPlaceholdersCallback = OnFetchPlaceholders;
        _fetchDataCallback = OnFetchData;
        _notifyDehydrateCallback = OnNotifyDehydrate;

        var callbackTable = new CF_CALLBACK_REGISTRATION[]
        {
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                Callback = Marshal.GetFunctionPointerForDelegate(_fetchPlaceholdersCallback)
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = Marshal.GetFunctionPointerForDelegate(_fetchDataCallback)
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE,
                Callback = Marshal.GetFunctionPointerForDelegate(_notifyDehydrateCallback)
            },
            CF_CALLBACK_REGISTRATION.CF_CALLBACK_REGISTRATION_END
        };

        FileLogger.Log($"CfConnectSyncRoot 调用中... path={syncFolderPath}");
        int hr = CfConnectSyncRoot(
            syncFolderPath,
            callbackTable,
            IntPtr.Zero,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            out _connectionKey);

        FileLogger.Log($"CfConnectSyncRoot → 0x{hr:X8}, connectionKey={_connectionKey}");
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);

        _connected = true;
        FileLogger.Log("SyncProviderConnection 已连接");
    }

    /// <summary>
    /// FETCH_PLACEHOLDERS 回调 — Windows 枚举目录时调用。
    /// 使用 CfExecute + TRANSFER_PLACEHOLDERS 把占位符传递给系统。
    /// </summary>
    private static void OnFetchPlaceholders(IntPtr callbackInfoPtr, IntPtr callbackParamsPtr)
    {
        FileLogger.Log("FETCH_PLACEHOLDERS 回调触发");
        try
        {
            long connectionKey = CallbackInfoReader.GetConnectionKey(callbackInfoPtr);
            long transferKey = CallbackInfoReader.GetTransferKey(callbackInfoPtr);
            long requestKey = CallbackInfoReader.GetRequestKey(callbackInfoPtr);
            IntPtr correlationVector = CallbackInfoReader.GetCorrelationVector(callbackInfoPtr);
            string normalizedPath = CallbackInfoReader.GetNormalizedPathString(callbackInfoPtr);
            FileLogger.Log($"  connectionKey={connectionKey}, transferKey={transferKey}, path={normalizedPath}");

            // 从完整路径计算服务端相对路径
            string relativePath = GetRelativePath(normalizedPath);
            FileLogger.Log($"  relativePath=\"{relativePath}\"");

            // 调用服务端 API 获取目录内容
            List<TreeItem> items;
            try
            {
                items = Task.Run(() => _api!.ListDirectoryAsync(relativePath)).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  ListDirectoryAsync 异常: {ex.Message}");
                items = new List<TreeItem>();
            }

            FileLogger.Log($"  服务端返回 {items.Count} 个条目");

            // ── 构建非托管 CF_PLACEHOLDER_CREATE_INFO 数组 ──
            int structSize = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>();
            IntPtr nativeArray = items.Count > 0
                ? Marshal.AllocHGlobal(structSize * items.Count)
                : IntPtr.Zero;
            var toFree = new List<IntPtr>();

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];

                    // RelativeFileName: 手动分配非托管 WCHAR 字符串
                    IntPtr namePtr = Marshal.StringToHGlobalUni(item.Name);
                    toFree.Add(namePtr);

                    // FileIdentity: 使用 UTF-8 编码的相对路径
                    string relPath = string.IsNullOrEmpty(relativePath) ? item.Name : relativePath + "/" + item.Name;
                    byte[] identityBytes = Encoding.UTF8.GetBytes(relPath);
                    IntPtr identityPtr = Marshal.AllocHGlobal(identityBytes.Length);
                    Marshal.Copy(identityBytes, 0, identityPtr, identityBytes.Length);
                    toFree.Add(identityPtr);

                    long fileTime = item.Mtime > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(item.Mtime).ToFileTime()
                        : DateTimeOffset.UtcNow.ToFileTime();

                    var entry = new CF_PLACEHOLDER_CREATE_INFO
                    {
                        RelativeFileName = namePtr,
                        FsMetadata = new CF_FS_METADATA
                        {
                            BasicInfo_CreationTime = fileTime,
                            BasicInfo_LastAccessTime = fileTime,
                            BasicInfo_LastWriteTime = fileTime,
                            BasicInfo_ChangeTime = fileTime,
                            BasicInfo_FileAttributes = item.IsDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                            FileSize = item.IsDir ? 0 : item.Size,
                        },
                        FileIdentity = identityPtr,
                        FileIdentityLength = (uint)identityBytes.Length,
                        Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                    };

                    Marshal.StructureToPtr(entry, nativeArray + i * structSize, false);
                    FileLogger.Log($"    [{i}] name=\"{item.Name}\" is_dir={item.IsDir} size={item.Size}");
                }

                // ── CfExecute + TRANSFER_PLACEHOLDERS ──
                var opInfo = new CF_OPERATION_INFO
                {
                    StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                    Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                    ConnectionKey = connectionKey,
                    TransferKey = transferKey,
                    CorrelationVector = correlationVector,
                    SyncStatus = IntPtr.Zero,
                    RequestKey = requestKey,
                };

                var opParams = new CF_OPERATION_PARAMETERS
                {
                    ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS>(),
                    Flags = 0x02,                           // DISABLE_ON_DEMAND_POPULATION
                    CompletionStatus = 0,                   // STATUS_SUCCESS
                    PlaceholderTotalCount = items.Count,
                    PlaceholderArray = nativeArray,
                    PlaceholderCount = (uint)items.Count,
                    EntriesProcessed = 0,
                };

                FileLogger.Log($"  CfExecute 调用: StructSize={opInfo.StructSize}, ParamSize={opParams.ParamSize}, count={items.Count}");
                int hr = CfExecute(in opInfo, ref opParams);
                FileLogger.Log($"  CfExecute(TRANSFER_PLACEHOLDERS) → 0x{hr:X8}, processed={opParams.EntriesProcessed}");

                // ── 创建子占位符会清除父目录的 IN_SYNC 状态（白云），需要重新设回 ──
                if (hr >= 0 && items.Count > 0 && !string.IsNullOrEmpty(relativePath))
                {
                    var dirFullPath = Path.Combine(_syncFolder!, relativePath.Replace('/', '\\'));
                    SetItemInSync(dirFullPath);
                }
            }
            finally
            {
                if (nativeArray != IntPtr.Zero) Marshal.FreeHGlobal(nativeArray);
                foreach (var p in toFree) Marshal.FreeHGlobal(p);
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  FETCH_PLACEHOLDERS 异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 将文件/目录标记为 IN_SYNC（绿勾）— 公开版本，供外部调用
    /// </summary>
    public static void SetItemInSyncPublic(string path) => SetItemInSync(path);

    /// <summary>
    /// 将文件/目录标记为 IN_SYNC（绿勾）
    /// </summary>
    private static void SetItemInSync(string path)
    {
        try
        {
            // 如果是目录，需要 FILE_FLAG_BACKUP_SEMANTICS
            bool isDir = Directory.Exists(path);
            uint flags = isDir ? 0x02000000u : 0x00000080u; // FILE_FLAG_BACKUP_SEMANTICS or FILE_ATTRIBUTE_NORMAL

            IntPtr handle = CreateFileW(
                path,
                0x00000182, // FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES | FILE_WRITE_DATA
                0x00000007, // FILE_SHARE_READ | WRITE | DELETE
                IntPtr.Zero,
                3,          // OPEN_EXISTING
                flags,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;

            try
            {
                CfSetInSyncState(
                    handle,
                    CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                    CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                    IntPtr.Zero);
            }
            finally
            {
                CloseHandle(handle);
            }
        }
        catch { }
    }

    /// <summary>
    /// 检查文件路径是否位于最近被脱水（释放空间）的目录/文件中（5秒内）。
    /// 用于 FETCH_DATA 回调中阻止资源管理器缩略图/预览触发的重新水合。
    /// </summary>
    private static bool IsRecentlyDehydratedPath(string fullPath)
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        var threshold = TimeSpan.TicksPerSecond * 5;

        // 检查文件自身（单文件脱水场景）
        if (_dehydrateCooldown.TryGetValue(fullPath, out var fileTicks) && (nowTicks - fileTicks) < threshold)
            return true;

        // 检查父目录链（目录脱水场景）
        var dir = Path.GetDirectoryName(fullPath);
        while (dir != null && _syncFolder != null && dir.Length >= _syncFolder.Length)
        {
            if (_dehydrateCooldown.TryGetValue(dir, out var dirTicks) && (nowTicks - dirTicks) < threshold)
                return true;
            dir = Path.GetDirectoryName(dir);
        }

        return false;
    }

    /// <summary>
    /// 快速读取文件的 PinState（0=UNSPECIFIED, 1=PINNED, 2=UNPINNED）
    /// 用于 FETCH_DATA 冷却检查和全量扫描 PinState 不匹配修复。
    /// </summary>
    public static uint GetFilePinState(string fullPath)
    {
        IntPtr handle = CreateFileW(
            fullPath,
            0x00000080, // FILE_READ_ATTRIBUTES
            0x00000007, // FILE_SHARE_READ | WRITE | DELETE
            IntPtr.Zero,
            3,          // OPEN_EXISTING
            0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return 0;
        try
        {
            IntPtr buf = Marshal.AllocHGlobal(256);
            try
            {
                int hr = CfGetPlaceholderInfo(handle, 0, buf, 256, out uint retLen);
                if (hr < 0 || retLen < 8) return 0;
                return (uint)Marshal.ReadInt32(buf, 0);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 检测并处理"释放空间"请求。
    /// 用户右键"释放空间"后，Windows 将 PinState 设为 UNPINNED。
    /// 检测到后主动调用 CfDehydratePlaceholder 释放本地数据。
    /// </summary>
    public static bool TryHandleDehydrateRequest(string fullPath)
    {
        try
        {
            bool isDir = Directory.Exists(fullPath);
            bool isFile = !isDir && File.Exists(fullPath);
            if (!isDir && !isFile) return false;

            // 先检查 PinState，确认是 UNPINNED 才处理
            // 必须在冷却检查之前，否则脱水冷却 return true 会阻止 TryHandlePinRequest 处理 Pin 请求
            IntPtr handle = CreateFileW(
                fullPath,
                0x00000180, // FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES
                0x00000007, // FILE_SHARE_READ | WRITE | DELETE
                IntPtr.Zero,
                3,          // OPEN_EXISTING
                0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;

            try
            {
                IntPtr buf = Marshal.AllocHGlobal(256);
                try
                {
                    int hr = CfGetPlaceholderInfo(handle, 0, buf, 256, out uint retLen);
                    if (hr < 0 || retLen < 8) return false;

                    uint pinState = (uint)Marshal.ReadInt32(buf, 0);
                    if (pinState != 2) return false; // 不是 UNPINNED，让其他处理器处理
                }
                finally { Marshal.FreeHGlobal(buf); }

                // PinState 确认为 UNPINNED，现在检查冷却避免重复脱水
                var nowTicks = DateTime.UtcNow.Ticks;
                if (_dehydrateCooldown.TryGetValue(fullPath, out var lastTicks))
                {
                    if ((nowTicks - lastTicks) < TimeSpan.TicksPerSecond * 60)
                    {
                        // 冷却期内，检查是否真的需要脱水
                        bool needsDehydration = false;
                        try
                        {
                            if (isFile)
                            {
                                var fi = new FileInfo(fullPath);
                                needsDehydration = fi.Attributes.HasFlag(FileAttributes.ReparsePoint) && !fi.Attributes.HasFlag(FileAttributes.Offline);
                            }
                            else if (isDir)
                                needsDehydration = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                                    .Any(f => { try { var fi2 = new FileInfo(f); return fi2.Attributes.HasFlag(FileAttributes.ReparsePoint) && !fi2.Attributes.HasFlag(FileAttributes.Offline); } catch { return false; } });
                        }
                        catch { }
                        if (!needsDehydration)
                        {
                            // 驱动可能异步清除 IN_SYNC（蓝圈），趁 Changed 事件到达时恢复
                            SetItemInSync(fullPath);
                            return true; // 文件已脱水，安全跳过
                        }
                        FileLogger.Log($"TryHandleDehydrateRequest 冷却期内但文件需要脱水，继续处理: {fullPath}");
                    }
                }

                if (isDir)
                {
                    // 目录：递归释放所有已 hydrated 文件
                    FileLogger.Log($"DEHYDRATE: UNPINNED 目录检测到，释放空间: {fullPath}");
                    int count = 0;
                    foreach (var file in Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            if (fi.Attributes.HasFlag(FileAttributes.Offline)) continue; // 已 dehydrated

                            var fh = CreateFileW(file,
                                0x00000180, // FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES
                                0x00000007,
                                IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                            if (fh == IntPtr.Zero || fh == new IntPtr(-1)) continue;
                            try
                            {
                                int dhr = CfDehydratePlaceholder(fh, 0, -1, 0, IntPtr.Zero);
                                // 0x80070187 (NOT_IN_SYNC) 或 0x80070188 (DEHYDRATION_DISALLOWED)：
                                // Pin→Unpin 快速切换时，PinState/InSync 传播可能未完成，多次重试
                                if (dhr == unchecked((int)0x80070187) || dhr == unchecked((int)0x80070188))
                                {
                                    for (int retry = 0; retry < 3 && dhr < 0; retry++)
                                    {
                                        CfSetInSyncState(fh,
                                            CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                                            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                                            IntPtr.Zero);
                                        Thread.Sleep(1000);
                                        dhr = CfDehydratePlaceholder(fh, 0, -1, 0, IntPtr.Zero);
                                    }
                                }
                                if (dhr >= 0)
                                {
                                    count++;
                                    // 脱水后恢复 IN_SYNC，防止蓝圈
                                    CfSetInSyncState(fh,
                                        CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                                        CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                                        IntPtr.Zero);
                                }
                                else
                                {
                                    // 重试也失败，至少设置 IN_SYNC 防止蓝圈
                                    CfSetInSyncState(fh,
                                        CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                                        CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                                        IntPtr.Zero);
                                    FileLogger.Log($"  Dehydrate 失败(重试后): 0x{(uint)dhr:X8} {Path.GetFileName(file)}");
                                }
                            }
                            finally { CloseHandle(fh); }
                        }
                        catch { }
                    }
                    FileLogger.Log($"  已释放空间: {fullPath} ({count}个文件)");
                    _dehydrateCooldown[fullPath] = DateTime.UtcNow.Ticks;
                    // 目录及子目录设置 IN_SYNC，防止蓝圈
                    SetItemInSync(fullPath);
                    foreach (var subDir in Directory.GetDirectories(fullPath, "*", SearchOption.AllDirectories))
                    {
                        try { SetItemInSync(subDir); } catch { }
                    }
                    return true;
                }
                else
                {
                    var fi = new FileInfo(fullPath);
                    if (fi.Attributes.HasFlag(FileAttributes.Offline)) return true; // 已 dehydrated
                    if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false; // 不是 placeholder

                    FileLogger.Log($"DEHYDRATE: UNPINNED 文件检测到，释放空间: {fullPath}");
                    int dhr = CfDehydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero);
                    // 0x80070187 (NOT_IN_SYNC) 或 0x80070188 (DEHYDRATION_DISALLOWED)：
                    // Pin→Unpin 快速切换时，PinState/InSync 传播未完成，多次重试
                    if (dhr == unchecked((int)0x80070187) || dhr == unchecked((int)0x80070188))
                    {
                        FileLogger.Log($"  Dehydrate 失败 0x{(uint)dhr:X8}，重试中: {fullPath}");
                        for (int retry = 0; retry < 3 && dhr < 0; retry++)
                        {
                            CfSetInSyncState(handle,
                                CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                                CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                                IntPtr.Zero);
                            Thread.Sleep(1000);
                            dhr = CfDehydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero);
                        }
                    }
                    if (dhr >= 0)
                    {
                        // 脱水后恢复 IN_SYNC，防止蓝圈
                        CfSetInSyncState(handle,
                            CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                            IntPtr.Zero);
                        FileLogger.Log($"  已释放空间: {fullPath}");
                        _dehydrateCooldown[fullPath] = DateTime.UtcNow.Ticks;
                        return true;
                    }
                    else
                    {
                        // 重试也失败，至少设 IN_SYNC 防止蓝圈
                        CfSetInSyncState(handle,
                            CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
                            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
                            IntPtr.Zero);
                        FileLogger.Log($"  Dehydrate 失败(重试后): 0x{(uint)dhr:X8} {fullPath}");
                        return true; // 返回 true 防止后续处理器再处理
                    }
                }
            }
            finally { CloseHandle(handle); }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"TryHandleDehydrateRequest 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 检测并处理"始终保留在此设备上"请求。
    /// 用户右键选择后，Windows 将 PinState 设为 PINNED。
    /// 检测到后主动调用 CfHydratePlaceholder 水合本地占位符。
    /// CfHydratePlaceholder 会内部触发 FETCH_DATA 回调完成实际下载。
    /// </summary>
    public static bool TryHandlePinRequest(string fullPath)
    {
        try
        {
            bool isDir = Directory.Exists(fullPath);
            bool isFile = !isDir && File.Exists(fullPath);
            if (!isDir && !isFile)
            {
                FileLogger.Log($"TryHandlePinRequest 跳过(路径不存在): {fullPath}");
                return false;
            }

            // 先检查 PinState，确认是 PINNED 才处理
            // 必须在冷却检查之前，否则冷却 return true 会阻止 TryHandleDehydrateRequest
            IntPtr handle = CreateFileW(
                fullPath,
                0x00000181, // FILE_READ_DATA | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES
                0x00000007, // FILE_SHARE_READ | WRITE | DELETE
                IntPtr.Zero,
                3,          // OPEN_EXISTING
                0x02000000, // FILE_FLAG_BACKUP_SEMANTICS
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                FileLogger.Log($"TryHandlePinRequest 跳过(句柄打开失败): {fullPath}");
                return false;
            }

            try
            {
                IntPtr buf = Marshal.AllocHGlobal(256);
                try
                {
                    int hr = CfGetPlaceholderInfo(handle, 0, buf, 256, out uint retLen);
                    if (hr < 0 || retLen < 8)
                    {
                        FileLogger.Log($"TryHandlePinRequest 跳过(PlaceholderInfo失败 hr=0x{(uint)hr:X8} retLen={retLen}): {fullPath}");
                        return false;
                    }

                    uint pinState = (uint)Marshal.ReadInt32(buf, 0);
                    if (pinState != 1)
                    {
                        FileLogger.Log($"TryHandlePinRequest 跳过(PinState={pinState}，非PINNED): {fullPath}");
                        return false; // 不是 PINNED，让其他处理器处理
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }

                // PinState 确认为 PINNED，现在检查冷却避免重复水合
                var nowTicks = DateTime.UtcNow.Ticks;
                if (_dehydrateCooldown.TryGetValue("PIN:" + fullPath, out var lastTicks))
                {
                    if ((nowTicks - lastTicks) < TimeSpan.TicksPerSecond * 60)
                    {
                        bool needsHydration = false;
                        try
                        {
                            if (isFile)
                                needsHydration = new FileInfo(fullPath).Attributes.HasFlag(FileAttributes.Offline);
                            else if (isDir)
                                needsHydration = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                                    .Any(f => { try { return new FileInfo(f).Attributes.HasFlag(FileAttributes.Offline) && new FileInfo(f).Attributes.HasFlag(FileAttributes.ReparsePoint); } catch { return false; } });
                        }
                        catch { }
                        if (!needsHydration)
                            return true; // 文件已水合，安全跳过
                        FileLogger.Log($"TryHandlePinRequest 冷却期内但文件需要水合，继续处理: {fullPath}");
                    }
                }

                // 用户 Pin 时清除脱水冷却，允许后续 FETCH_DATA 水合
                _dehydrateCooldown.TryRemove(fullPath, out _);

                if (isDir)
                {
                    // 目录：递归水合所有 dehydrated 文件
                    FileLogger.Log($"HYDRATE: PINNED 目录检测到，始终保留: {fullPath}");
                    int count = 0;
                    foreach (var file in Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(file);
                            if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                            if (!fi.Attributes.HasFlag(FileAttributes.Offline))
                            {
                                // 已 hydrated，记录 mtime 防止后续 Unpin 触发误上传
                                _syncEngine?.RecordFileMtime(file);
                                continue;
                            }

                            var fh = CreateFileW(file,
                                0x00000181, // FILE_READ_DATA | FILE_READ_ATTRIBUTES | FILE_WRITE_ATTRIBUTES
                                0x00000007,
                                IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
                            if (fh == IntPtr.Zero || fh == new IntPtr(-1)) continue;
                            try
                            {
                                // 清除单文件的脱水冷却，让 FETCH_DATA 不被阻止
                                _dehydrateCooldown.TryRemove(file, out _);
                                int hhr = CfHydratePlaceholder(fh, 0, -1, 0, IntPtr.Zero);
                                if (hhr >= 0) count++;
                                else FileLogger.Log($"  Hydrate 失败: 0x{(uint)hhr:X8} {Path.GetFileName(file)}");
                            }
                            finally { CloseHandle(fh); }
                        }
                        catch { }
                    }
                    FileLogger.Log($"  已水合: {fullPath} ({count}个文件)");
                    _dehydrateCooldown["PIN:" + fullPath] = DateTime.UtcNow.Ticks;
                    return true;
                }
                else
                {
                    // 单个文件
                    var fi = new FileInfo(fullPath);
                    if (!fi.Attributes.HasFlag(FileAttributes.Offline))
                    {
                        // 已 hydrated，刷新冷却防止过期后漏检
                        _dehydrateCooldown["PIN:" + fullPath] = DateTime.UtcNow.Ticks;
                        // 记录 mtime，防止后续 Unpin 触发的 Changed 事件导致误上传
                        _syncEngine?.RecordFileMtime(fullPath);
                        // 驱动可能异步清除 IN_SYNC（蓝圈），趁 Changed 事件到达时恢复
                        SetItemInSync(fullPath);
                        FileLogger.Log($"TryHandlePinRequest PINNED+已水合，刷新冷却: {fullPath}");
                        return true;
                    }
                    if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        FileLogger.Log($"TryHandlePinRequest 跳过(非placeholder): {fullPath}");
                        return false;
                    }

                    FileLogger.Log($"HYDRATE: PINNED 文件检测到，始终保留: {fullPath}");
                    int hhr = CfHydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero);
                    if (hhr >= 0)
                    {
                        FileLogger.Log($"  已水合: {fullPath}");
                        _dehydrateCooldown["PIN:" + fullPath] = DateTime.UtcNow.Ticks;
                        return true;
                    }
                    else
                    {
                        FileLogger.Log($"  Hydrate 失败: 0x{(uint)hhr:X8} {fullPath}");
                        return false;
                    }
                }
            }
            finally { CloseHandle(handle); }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"TryHandlePinRequest 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 从 NormalizedPath 提取服务端相对路径。
    /// NormalizedPath 格式: \Users\hhz\EhangSync\Documents (volume-relative, 无盘符)
    /// _syncFolder 格式:   C:\Users\hhz\EhangSync
    /// 返回: "Documents" 或 "" (根目录)
    /// </summary>
    private static string GetRelativePath(string normalizedPath)
    {
        if (_syncFolder == null) return "";

        string path = normalizedPath.TrimEnd('\\');

        // 移除 \\?\ 前缀
        if (path.StartsWith(@"\?\"))
            path = path.Substring(4);

        // syncRoot 去掉盘符前缀，变成 volume-relative 格式
        // C:\Users\hhz\EhangSync → \Users\hhz\EhangSync
        string syncRoot = _syncFolder.TrimEnd('\\');
        if (syncRoot.Length >= 2 && syncRoot[1] == ':')
            syncRoot = syncRoot.Substring(2); // 去掉 "C:"

        // 比较
        if (string.Equals(path, syncRoot, StringComparison.OrdinalIgnoreCase))
            return "";

        if (path.StartsWith(syncRoot + "\\", StringComparison.OrdinalIgnoreCase))
            return path.Substring(syncRoot.Length + 1).Replace('\\', '/');

        return "";
    }

    /// <summary>
    /// FETCH_DATA 回调 — Windows 尝试下载文件内容时调用。
    /// 使用流式分块下载，每块 8MB，逐块通过 CfExecute + TRANSFER_DATA 回传给系统。
    /// 这样任意大小的文件都能处理，内存占用恒定 ~8MB。
    /// 用户取消时通过 CancellationToken 立即中止 HTTP 请求，避免排空阻塞。
    /// </summary>
    private static void OnFetchData(IntPtr callbackInfoPtr, IntPtr callbackParamsPtr)
    {
        FileLogger.Log("FETCH_DATA 回调触发");
        try
        {
            long connectionKey = CallbackInfoReader.GetConnectionKey(callbackInfoPtr);
            long transferKey = CallbackInfoReader.GetTransferKey(callbackInfoPtr);
            long requestKey = CallbackInfoReader.GetRequestKey(callbackInfoPtr);
            long fileSize = CallbackInfoReader.GetFileSize(callbackInfoPtr);
            string normalizedPath = CallbackInfoReader.GetNormalizedPathString(callbackInfoPtr);
            FileLogger.Log($"  path={normalizedPath}, fileSize={fileSize}");

            // 解析 FETCH_DATA 参数（请求的文件范围）
            var fetchParams = Marshal.PtrToStructure<CF_CALLBACK_PARAMETERS_FETCHDATA>(callbackParamsPtr);
            long requiredOffset = fetchParams.FetchData.RequiredFileOffset;
            long requiredLength = fetchParams.FetchData.RequiredLength;
            FileLogger.Log($"  请求范围: offset={requiredOffset}, length={requiredLength}");

            // 从完整路径计算服务端相对路径
            string relativePath = GetRelativePath(normalizedPath);
            FileLogger.Log($"  relativePath=\"{relativePath}\"");

            // 检查是否位于被抑制的删除目录树下 — 文件即将被删，不需要下载
            if (_syncEngine != null && _syncFolder != null)
            {
                var fullPath = Path.Combine(_syncFolder, relativePath.Replace('/', '\\'));
                if (_syncEngine.IsInSuppressedTree(fullPath) && _syncEngine.HasPendingDelete(relativePath))
                {
                    FileLogger.Log($"  FETCH_DATA 跳过(删除爆发抑制): {relativePath}");
                    // 返回错误状态让 Windows 知道不提供数据
                    var cancelOpInfo = new CF_OPERATION_INFO
                    {
                        StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                        Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                        ConnectionKey = connectionKey,
                        TransferKey = transferKey,
                        SyncStatus = IntPtr.Zero,
                        RequestKey = requestKey,
                    };
                    var cancelOpParams = new CF_OPERATION_PARAMETERS_TRANSFERDATA
                    {
                        ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFERDATA>(),
                        CompletionStatus = unchecked((int)0xC000_0120), // STATUS_CANCELLED
                        Buffer = IntPtr.Zero,
                        Offset = 0,
                        Length = 0,
                    };
                    CfExecute(in cancelOpInfo, ref cancelOpParams);
                    return;
                }

                // 检查文件是否在刚被释放空间的目录内（5秒内）
                // 资源管理器/SMB 浏览目录时会为图片等文件生成缩略图/预览，
                // 触发 FETCH_DATA 把刚脱水的文件重新水合，导致释放空间被撤销+蓝圈。
                // 但如果文件已被 PINNED（用户请求"始终保留"），必须允许水合。
                if (IsRecentlyDehydratedPath(fullPath))
                {
                    // 关键修复：检查 PinState，PINNED 文件即使在冷却期内也必须允许水合
                    uint pinState = GetFilePinState(fullPath);
                    if (pinState == 1) // PINNED — 用户明确要求水合
                    {
                        FileLogger.Log($"  FETCH_DATA 最近脱水但文件已PINNED，允许水合: {relativePath}");
                        _dehydrateCooldown.TryRemove(fullPath, out _);
                    }
                    else
                    {
                        FileLogger.Log($"  FETCH_DATA 跳过(最近脱水，防止重新水合): {relativePath}");
                        var cancelOpInfo2 = new CF_OPERATION_INFO
                        {
                            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                            ConnectionKey = connectionKey,
                            TransferKey = transferKey,
                            SyncStatus = IntPtr.Zero,
                            RequestKey = requestKey,
                        };
                        var cancelOpParams2 = new CF_OPERATION_PARAMETERS_TRANSFERDATA
                        {
                            ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFERDATA>(),
                            CompletionStatus = unchecked((int)0xC000_0120), // STATUS_CANCELLED
                            Buffer = IntPtr.Zero,
                            Offset = 0,
                            Length = 0,
                        };
                        CfExecute(in cancelOpInfo2, ref cancelOpParams2);
                        // 确保文件保持 IN_SYNC（脱水后驱动可能清除）
                        SetItemInSync(fullPath);
                        return;
                    }
                }

                // 在写入数据之前就标记抑制，防止 Changed 事件竞态
                // 同时设置 SuppressForModList，因为大文件下载可能超过 MarkRecentlySynced 的 2 秒窗口
                _syncEngine.MarkRecentlySynced(fullPath);
                _syncEngine.SuppressForModList(fullPath);
            }

            // 报告下载开始
            CfReportProviderProgress(connectionKey, transferKey, fileSize, requiredOffset);
            SyncStatusManager.Instance.AddLog("⬇️", $"水合下载: {relativePath} ({FormatSize(requiredLength)})");

            // 在 UI 传输列表中显示下载进度
            var transferItem = new TransferItem
            {
                FileName = relativePath,
                Direction = TransferDirection.Download,
                Status = TransferStatus.Transferring,
            };
            SyncStatusManager.Instance.AddTransfer(transferItem);
            var downloadStart = DateTime.UtcNow;

            try // 确保 EndBusy 与 AddTransfer 的 BeginBusy 配对
            {

            // ── 流式分块下载 + 逐块 TRANSFER_DATA ──
            const int CHUNK_SIZE = 4 * 1024 * 1024; // 4MB per chunk

            // 用 CancellationTokenSource 在取消时立即中止 HTTP 请求
            // 避免 response.Dispose() 排空未读数据导致长时间阻塞
            using var cts = new CancellationTokenSource();
            System.Net.Http.HttpResponseMessage? response = null;
            try
            {
                response = Task.Run(() => _api!.GetDownloadStreamAsync(relativePath, requiredOffset, requiredLength, cts.Token))
                    .GetAwaiter().GetResult();

                using var stream = Task.Run(() => response.Content.ReadAsStreamAsync(cts.Token)).GetAwaiter().GetResult();

                byte[] buffer = new byte[CHUNK_SIZE];
                long currentOffset = requiredOffset;
                long totalRead = 0;

                while (totalRead < requiredLength)
                {
                    // 从流中读满一个 chunk（或到流末尾）
                    int wantBytes = (int)Math.Min(CHUNK_SIZE, requiredLength - totalRead);
                    int bytesInChunk = 0;
                    while (bytesInChunk < wantBytes)
                    {
                        int n = Task.Run(() => stream.ReadAsync(buffer, bytesInChunk, wantBytes - bytesInChunk, cts.Token))
                            .GetAwaiter().GetResult();
                        if (n == 0) break; // 流结束
                        bytesInChunk += n;
                    }

                    if (bytesInChunk == 0) break; // 无更多数据

                    // 通过 CfExecute + TRANSFER_DATA 将这一块传给 Windows
                    var pinnedData = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                    try
                    {
                        var opInfo = new CF_OPERATION_INFO
                        {
                            StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                            ConnectionKey = connectionKey,
                            TransferKey = transferKey,
                            SyncStatus = IntPtr.Zero,
                            RequestKey = requestKey,
                        };

                        var opParams = new CF_OPERATION_PARAMETERS_TRANSFERDATA
                        {
                            ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFERDATA>(),
                            CompletionStatus = 0,  // STATUS_SUCCESS
                            Buffer = pinnedData.AddrOfPinnedObject(),
                            Offset = currentOffset,
                            Length = bytesInChunk,
                        };

                        int hr = CfExecute(in opInfo, ref opParams);
                        if (hr < 0)
                        {
                            FileLogger.Log($"  CfExecute(TRANSFER_DATA) 取消/失败: 0x{(uint)hr:X8} at offset={currentOffset}, 已传输 {totalRead} bytes");
                            transferItem.Status = TransferStatus.Failed;
                            // 立即取消 HTTP 请求，避免 response.Dispose() 排空剩余数据阻塞回调线程
                            cts.Cancel();
                            return;
                        }
                    }
                    finally
                    {
                        pinnedData.Free();
                    }

                    currentOffset += bytesInChunk;
                    totalRead += bytesInChunk;

                    // 更新 UI 进度和速度
                    double pct = requiredLength > 0 ? totalRead * 100.0 / requiredLength : 0;
                    transferItem.Progress = pct;
                    transferItem.Speed = FormatTransferSpeed(totalRead, requiredLength, downloadStart);

                    // 更新系统进度
                    CfReportProviderProgress(connectionKey, transferKey, fileSize, currentOffset);
                }

                FileLogger.Log($"  已下载: {relativePath} ({totalRead} bytes, {(totalRead + CHUNK_SIZE - 1) / CHUNK_SIZE} chunks)");
                transferItem.Progress = 100;
                transferItem.Status = TransferStatus.Completed;
                transferItem.Speed = FormatSize(totalRead);
                SyncStatusManager.Instance.AddLog("✅", $"已水合: {relativePath} ({FormatSize(totalRead)})");

                // 下载完成后立即刷新抑制窗口，防止最后一个 chunk 触发的 Changed 事件竞态
                if (_syncEngine != null && _syncFolder != null)
                {
                    var fullPath = Path.Combine(_syncFolder, relativePath.Replace('/', '\\'));
                    _syncEngine.MarkRecentlySynced(fullPath);
                    _syncEngine.SuppressForModList(fullPath);
                }
            }
            catch (OperationCanceledException)
            {
                FileLogger.Log($"  下载已取消: {relativePath}");
                transferItem.Status = TransferStatus.Failed;
                SyncStatusManager.Instance.AddLog("❌", $"下载已取消: {relativePath}");
                return;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  流式下载失败: {ex.Message}");
                transferItem.Status = TransferStatus.Failed;
                SyncStatusManager.Instance.AddLog("❌", $"下载失败: {relativePath}");
                cts.Cancel(); // 确保 HTTP 请求被中止
                TransferDataFailure(connectionKey, transferKey);
                return;
            }
            finally
            {
                response?.Dispose();
            }

            // 标记为 ModList 抑制 + 设置 IN_SYNC + 刷新父目录状态
            if (_syncEngine != null && _syncFolder != null)
            {
                var fullPath = Path.Combine(_syncFolder, relativePath.Replace('/', '\\'));
                _syncEngine.SuppressForModList(fullPath);
                _syncEngine.SetInSyncAfterHydration(fullPath);

                // 驱动可能在 SetInSyncAfterHydration 之后异步清除 IN_SYNC（导致蓝圈）。
                // 延迟 3 秒后再设置一次，作为安全网。
                var captured = fullPath;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    try { SyncProviderConnection.SetItemInSync(captured); }
                    catch { }
                });
            }

            } // end try (EndBusy)
            finally
            {
                TrayIconService.Current?.EndBusy();
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  FETCH_DATA 异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// NOTIFY_DEHYDRATE 回调 — 用户右键"释放空间"时触发。
    /// ACK 同意后 Windows 会删除本地数据，文件变为白云占位符。
    /// </summary>
    private static void OnNotifyDehydrate(IntPtr callbackInfoPtr, IntPtr callbackParamsPtr)
    {
        try
        {
            long connectionKey = CallbackInfoReader.GetConnectionKey(callbackInfoPtr);
            long transferKey = CallbackInfoReader.GetTransferKey(callbackInfoPtr);
            long requestKey = CallbackInfoReader.GetRequestKey(callbackInfoPtr);
            IntPtr correlationVector = CallbackInfoReader.GetCorrelationVector(callbackInfoPtr);
            string normalizedPath = CallbackInfoReader.GetNormalizedPathString(callbackInfoPtr);
            string relativePath = GetRelativePath(normalizedPath);

            FileLogger.Log($"NOTIFY_DEHYDRATE: {relativePath}");

            // ACK 同意 dehydrate
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DEHYDRATE,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
                CorrelationVector = correlationVector,
                SyncStatus = IntPtr.Zero,
                RequestKey = requestKey,
            };

            var opParams = new CF_OPERATION_PARAMETERS_ACKDEHYDRATE
            {
                ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_ACKDEHYDRATE>(),
                Flags = 0,
                CompletionStatus = 0, // STATUS_SUCCESS
                FileIdentity = IntPtr.Zero,
                FileIdentityLength = 0,
            };

            int hr = CfExecute(in opInfo, ref opParams);
            FileLogger.Log($"  ACK_DEHYDRATE → 0x{hr:X8}");

            if (hr >= 0)
            {
                // ACK 成功后，延迟恢复 IN_SYNC 状态
                // Windows 执行 dehydrate 需要一点时间，之后会发 DEHYDRATE_COMPLETION
                // 这里做延迟恢复作为双保险
                string fullPath = normalizedPath.StartsWith(@"\\?\") ? normalizedPath.Substring(4) : normalizedPath;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(300);
                        SetItemInSync(fullPath);
                        FileLogger.Log($"  dehydrate后恢复IN_SYNC: {relativePath}");
                    }
                    catch { }
                });
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  NOTIFY_DEHYDRATE 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 通知 Windows 文件下载失败（网络不可用）
    /// </summary>
    private static void TransferDataFailure(long connectionKey, long transferKey)
    {
        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)Marshal.SizeOf<CF_OPERATION_INFO>(),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
                SyncStatus = IntPtr.Zero,
            };

            var opParams = new CF_OPERATION_PARAMETERS_TRANSFERDATA
            {
                ParamSize = (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS_TRANSFERDATA>(),
                CompletionStatus = unchecked((int)0xC000CF06),  // STATUS_CLOUD_FILE_NETWORK_UNAVAILABLE
                Buffer = IntPtr.Zero,
                Offset = 0,
                Length = 0,
            };

            CfExecute(in opInfo, ref opParams);
        }
        catch { }
    }

    /// <summary>
    /// 断开同步根连接
    /// </summary>
    public void Disconnect()
    {
        if (!_connected) return;

        CfDisconnectSyncRoot(_connectionKey);
        _connected = false;
    }

    public void Dispose()
    {
        Disconnect();
    }

    /// <summary>
    /// 将字节数格式化为人类可读的大小字符串
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }

    /// <summary>
    /// 格式化传输速度和剩余时间
    /// </summary>
    private static string FormatTransferSpeed(long transferred, long total, DateTime startTime)
    {
        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
        if (elapsed < 0.5 || transferred <= 0) return FormatSize(total);

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
}
