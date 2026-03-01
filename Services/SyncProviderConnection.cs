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

    // 必须持有委托引用，防止 GC 回收导致回调崩溃
    private CF_CALLBACK? _fetchPlaceholdersCallback;
    private CF_CALLBACK? _fetchDataCallback;

    /// <summary>
    /// 连接到已注册的同步根目录
    /// </summary>
    public void Connect(string syncFolderPath, SyncApiService api)
    {
        if (_connected) return;

        _api = api;
        _syncFolder = syncFolderPath;

        // 创建回调委托并持有引用
        _fetchPlaceholdersCallback = OnFetchPlaceholders;
        _fetchDataCallback = OnFetchData;

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
    /// 暂不实现，直接忽略。
    /// </summary>
    private static void OnFetchData(IntPtr callbackInfoPtr, IntPtr callbackParamsPtr)
    {
        FileLogger.Log("FETCH_DATA 回调触发（暂未实现）");
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
