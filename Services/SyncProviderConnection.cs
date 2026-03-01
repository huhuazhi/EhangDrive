using System.IO;
using System.Runtime.InteropServices;
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

        // ── 测试 CfCreatePlaceholders ──
        TestCreatePlaceholder(syncFolderPath);
    }

    /// <summary>
    /// FETCH_PLACEHOLDERS 回调 — Windows 枚举目录时调用。
    /// 使用 CfCreatePlaceholders 创建占位符，然后 CfExecute 通知完成。
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

            // ── 使用 CfCreatePlaceholders 创建占位符 ──
            if (items.Count > 0)
            {
                // 计算本地目录完整路径
                string localDirPath = string.IsNullOrEmpty(relativePath)
                    ? _syncFolder! + "\\"
                    : Path.Combine(_syncFolder!, relativePath.Replace('/', '\\')) + "\\";

                var placeholders = new CF_PLACEHOLDER_CREATE_INFO[items.Count];

                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    long fileTime = (item.Mtime + 11644473600L) * 10000000L;

                    placeholders[i] = new CF_PLACEHOLDER_CREATE_INFO
                    {
                        RelativeFileName = item.Name,
                        FsMetadata = new CF_FS_METADATA
                        {
                            BasicInfo = new FILE_BASIC_INFO
                            {
                                CreationTime = fileTime,
                                LastAccessTime = fileTime,
                                LastWriteTime = fileTime,
                                ChangeTime = fileTime,
                                FileAttributes = item.IsDir ? 0x10u : 0x20u, // DIRECTORY : NORMAL
                            },
                            FileSize = item.IsDir ? 0 : item.Size,
                        },
                        FileIdentity = IntPtr.Zero,
                        FileIdentityLength = 0,
                        Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                    };

                    FileLogger.Log($"    [{i}] name=\"{item.Name}\" is_dir={item.IsDir} size={item.Size}");
                }

                FileLogger.Log($"  CfCreatePlaceholders 调用: path=\"{localDirPath}\", count={items.Count}");
                int hr = CfCreatePlaceholders(
                    localDirPath,
                    placeholders,
                    (uint)items.Count,
                    CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                    out uint entriesProcessed);

                FileLogger.Log($"  CfCreatePlaceholders → 0x{hr:X8}, processed={entriesProcessed}/{items.Count}");

                // 读取每个 placeholder 的 Result
                for (int i = 0; i < items.Count; i++)
                {
                    FileLogger.Log($"    [{i}] Result=0x{placeholders[i].Result:X8}");
                }
            }

            // ── CfExecute 通知 Windows 枚举完成 ──
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = 40,
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
                CorrelationVector = correlationVector,
                RequestKey = requestKey,
            };

            var opParams = new CF_OPERATION_PARAMETERS
            {
                ParamSize = CF_SIZE_OF_OP_PARAM_TRANSFER_PLACEHOLDERS,
                Flags = 0x02,                       // DISABLE_ON_DEMAND_POPULATION
                CompletionStatus = 0,               // STATUS_SUCCESS
                PlaceholderTotalCount = 0,
                PlaceholderArray = IntPtr.Zero,
                PlaceholderCount = 0,
                EntriesProcessed = 0,
            };

            int hr2 = CfExecute(in opInfo, ref opParams);
            FileLogger.Log($"  CfExecute(完成信号) → 0x{hr2:X8}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  FETCH_PLACEHOLDERS 异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    /// <summary>
    /// 测试 CfCreatePlaceholders（从主线程调用，排查回调线程问题）
    /// </summary>
    private static void TestCreatePlaceholder(string syncFolder)
    {
        FileLogger.Log("=== 测试 CfCreatePlaceholders ===");
        try
        {
            // 先确认各个子结构体的 Marshal.SizeOf
            FileLogger.Log($"  sizeof(FILE_BASIC_INFO)={Marshal.SizeOf<FILE_BASIC_INFO>()}");
            FileLogger.Log($"  sizeof(CF_FS_METADATA)={Marshal.SizeOf<CF_FS_METADATA>()}");
            FileLogger.Log($"  sizeof(CF_PLACEHOLDER_CREATE_INFO)={Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>()}");

            long fileTime = DateTime.UtcNow.ToFileTimeUtc();
            string basePath = syncFolder.TrimEnd('\\') + "\\";

            var placeholders = new CF_PLACEHOLDER_CREATE_INFO[]
            {
                new()
                {
                    RelativeFileName = "TestDir",
                    FsMetadata = new CF_FS_METADATA
                    {
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            CreationTime = fileTime,
                            LastAccessTime = fileTime,
                            LastWriteTime = fileTime,
                            ChangeTime = fileTime,
                            FileAttributes = 0x10, // FILE_ATTRIBUTE_DIRECTORY
                        },
                        FileSize = 0,
                    },
                    FileIdentity = IntPtr.Zero,
                    FileIdentityLength = 0,
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                }
            };

            FileLogger.Log($"  basePath=\"{basePath}\"");

            // Hex dump: 手动 marshal 到非托管内存，查看字节布局
            int size = Marshal.SizeOf<CF_PLACEHOLDER_CREATE_INFO>();
            IntPtr pNative = Marshal.AllocHGlobal(size);
            try
            {
                // 手动设置 RelativeFileName 指针
                IntPtr pName = Marshal.StringToHGlobalUni("TestDir");
                var manualEntry = new CF_PLACEHOLDER_CREATE_INFO
                {
                    RelativeFileName = "TestDir",
                    FsMetadata = new CF_FS_METADATA
                    {
                        BasicInfo = new FILE_BASIC_INFO
                        {
                            CreationTime = fileTime,
                            LastAccessTime = fileTime,
                            LastWriteTime = fileTime,
                            ChangeTime = fileTime,
                            FileAttributes = 0x10,
                        },
                        FileSize = 0,
                    },
                    FileIdentity = IntPtr.Zero,
                    FileIdentityLength = 0,
                    Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
                };
                Marshal.StructureToPtr(manualEntry, pNative, false);

                byte[] bytes = new byte[size];
                Marshal.Copy(pNative, bytes, 0, size);
                var hex = BitConverter.ToString(bytes).Replace("-", " ");
                FileLogger.Log($"  struct hex dump ({size} bytes): {hex}");

                Marshal.FreeHGlobal(pName);
                Marshal.DestroyStructure<CF_PLACEHOLDER_CREATE_INFO>(pNative);
            }
            finally
            {
                Marshal.FreeHGlobal(pNative);
            }

            // 正式调用 CfCreatePlaceholders
            int hr = CfCreatePlaceholders(basePath, placeholders, 1,
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, out uint entriesProcessed);
            FileLogger.Log($"  CfCreatePlaceholders → 0x{hr:X8}, processed={entriesProcessed}, Result=0x{placeholders[0].Result:X8}");

            // 如果失败，用 Marshal.GetExceptionForHR 获取完整消息
            if (hr < 0)
            {
                var ex = Marshal.GetExceptionForHR(hr);
                FileLogger.Log($"  错误消息: {ex?.Message}");
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  测试异常: {ex.Message}\n{ex.StackTrace}");
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
