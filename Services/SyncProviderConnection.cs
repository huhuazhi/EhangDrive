using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
                    // InfoClass = 0 → CF_PLACEHOLDER_INFO_STANDARD
                    // 第一个 DWORD = PinState; UNPINNED = 2
                    int hr = CfGetPlaceholderInfo(handle, 0, buf, 256, out uint retLen);
                    if (hr < 0 || retLen < 8) return false;

                    uint pinState = (uint)Marshal.ReadInt32(buf, 0);
                    if (pinState != 2) return false; // 不是 UNPINNED
                }
                finally { Marshal.FreeHGlobal(buf); }

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
                                if (dhr >= 0) count++;
                                else FileLogger.Log($"  Dehydrate 失败: 0x{(uint)dhr:X8} {Path.GetFileName(file)}");
                            }
                            finally { CloseHandle(fh); }
                        }
                        catch { }
                    }
                    FileLogger.Log($"  已释放空间: {fullPath} ({count}个文件)");
                    return true;
                }
                else
                {
                    // 单个文件
                    var fi = new FileInfo(fullPath);
                    if (fi.Attributes.HasFlag(FileAttributes.Offline)) return true; // 已 dehydrated
                    if (!fi.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false; // 不是 placeholder

                    FileLogger.Log($"DEHYDRATE: UNPINNED 文件检测到，释放空间: {fullPath}");
                    int dhr = CfDehydratePlaceholder(handle, 0, -1, 0, IntPtr.Zero);
                    if (dhr >= 0)
                    {
                        FileLogger.Log($"  已释放空间: {fullPath}");
                        return true;
                    }
                    else
                    {
                        FileLogger.Log($"  Dehydrate 失败: 0x{(uint)dhr:X8} {fullPath}");
                        return false;
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
    /// 从服务端下载数据，通过 CfExecute + TRANSFER_DATA 回传给系统。
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

            // 报告下载开始
            CfReportProviderProgress(connectionKey, transferKey, fileSize, 0);

            // 从服务端下载文件数据
            byte[] data;
            try
            {
                data = Task.Run(() => _api!.DownloadFileBytesAsync(relativePath, requiredOffset, requiredLength))
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                FileLogger.Log($"  下载失败: {ex.Message}");
                TransferDataFailure(connectionKey, transferKey);
                return;
            }

            FileLogger.Log($"  下载完成: {data.Length} bytes");

            // 报告下载完成
            CfReportProviderProgress(connectionKey, transferKey, fileSize, requiredOffset + data.Length);

            // 在 CfExecute 写入数据之前就标记抑制，防止 Changed 事件竞态
            if (_syncEngine != null && _syncFolder != null)
            {
                var fullPath = Path.Combine(_syncFolder, relativePath.Replace('/', '\\'));
                _syncEngine.MarkRecentlySynced(fullPath);
            }

            // 通过 CfExecute + TRANSFER_DATA 将数据传给 Windows
            var pinnedData = GCHandle.Alloc(data, GCHandleType.Pinned);
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
                    Offset = requiredOffset,
                    Length = data.Length,
                };

                int hr = CfExecute(in opInfo, ref opParams);
                FileLogger.Log($"  CfExecute(TRANSFER_DATA) → 0x{hr:X8}");
            }
            finally
            {
                pinnedData.Free();
            }

            FileLogger.Log($"  已下载: {relativePath} ({data.Length} bytes)");

            // 标记为 ModList 抑制，防止 FETCH_DATA 下载后 FileWatcher 触发重新上传
            if (_syncEngine != null && _syncFolder != null)
            {
                var fullPath = Path.Combine(_syncFolder, relativePath.Replace('/', '\\'));
                _syncEngine.SuppressForModList(fullPath);
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
}
