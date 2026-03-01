using System.Runtime.InteropServices;

namespace EhangNAS_Sync.Native;

/// <summary>
/// Cloud Filter API (cldapi.dll) P/Invoke 声明。
/// 所有结构体使用 LayoutKind.Explicit 确保内存布局与 Windows SDK 完全一致。
/// </summary>
internal static class CldApi
{
    // ═══════════════════════════════════════════════════════════════
    //  API 函数
    // ═══════════════════════════════════════════════════════════════

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CfConnectSyncRoot(
        [MarshalAs(UnmanagedType.LPWStr)] string SyncRootPath,
        [In] CF_CALLBACK_REGISTRATION[] CallbackTable,
        IntPtr CallbackContext,
        CF_CONNECT_FLAGS ConnectFlags,
        out long ConnectionKey);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfDisconnectSyncRoot(long ConnectionKey);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfExecute(
        in CF_OPERATION_INFO OpInfo,
        ref CF_OPERATION_PARAMETERS OpParams);

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern int CfSetInSyncState(
        IntPtr FileHandle,
        CF_IN_SYNC_STATE InSyncState,
        CF_SET_IN_SYNC_FLAGS InSyncFlags,
        IntPtr InSyncUsn);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    public static extern int CfConvertToPlaceholder(
        IntPtr FileHandle,
        IntPtr FileIdentity,
        uint FileIdentityLength,
        CF_CONVERT_FLAGS ConvertFlags,
        IntPtr ConvertUsn,    // LONG*
        IntPtr Overlapped);   // OVERLAPPED*

    [DllImport("cldapi.dll", CharSet = CharSet.Unicode)]
    public static extern int CfCreatePlaceholders(
        string BaseDirectoryPath,
        [In, Out] CF_PLACEHOLDER_CREATE_INFO[] PlaceholderArray,
        uint PlaceholderCount,
        CF_CREATE_FLAGS CreateFlags,
        out uint EntriesProcessed);

    // ═══════════════════════════════════════════════════════════════
    //  枚举
    // ═══════════════════════════════════════════════════════════════

    [Flags]
    public enum CF_CONNECT_FLAGS : uint
    {
        CF_CONNECT_FLAG_NONE = 0x00000000,
        CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO = 0x00000002,
        CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH = 0x00000004,
    }

    public enum CF_CALLBACK_TYPE : uint
    {
        CF_CALLBACK_TYPE_FETCH_DATA = 0,
        CF_CALLBACK_TYPE_VALIDATE_DATA = 1,
        CF_CALLBACK_TYPE_CANCEL_FETCH_DATA = 2,
        CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS = 3,
        CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS = 4,
        CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION = 5,
        CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION = 6,
        CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE = 7,
        CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE_COMPLETION = 8,
        CF_CALLBACK_TYPE_NOTIFY_DELETE = 9,
        CF_CALLBACK_TYPE_NOTIFY_DELETE_COMPLETION = 10,
        CF_CALLBACK_TYPE_NOTIFY_RENAME = 11,
        CF_CALLBACK_TYPE_NOTIFY_RENAME_COMPLETION = 12,
        CF_CALLBACK_TYPE_NONE = 0xFFFFFFFF,
    }

    public enum CF_OPERATION_TYPE : uint
    {
        CF_OPERATION_TYPE_TRANSFER_DATA = 0,
        CF_OPERATION_TYPE_RETRIEVE_DATA = 1,
        CF_OPERATION_TYPE_ACK_DATA = 2,
        CF_OPERATION_TYPE_RESTART_HYDRATION = 3,
        CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS = 4,
        CF_OPERATION_TYPE_ACK_DEHYDRATE = 5,
        CF_OPERATION_TYPE_ACK_RENAME = 6,
        CF_OPERATION_TYPE_ACK_DELETE = 7,
    }

    public enum CF_IN_SYNC_STATE : uint
    {
        CF_IN_SYNC_STATE_NOT_IN_SYNC = 0,
        CF_IN_SYNC_STATE_IN_SYNC = 1,
    }

    [Flags]
    public enum CF_SET_IN_SYNC_FLAGS : uint
    {
        CF_SET_IN_SYNC_FLAG_NONE = 0,
    }

    [Flags]
    public enum CF_CONVERT_FLAGS : uint
    {
        CF_CONVERT_FLAG_NONE = 0x00000000,
        CF_CONVERT_FLAG_MARK_IN_SYNC = 0x00000001,
        CF_CONVERT_FLAG_DEHYDRATE = 0x00000002,
        CF_CONVERT_FLAG_ENABLE_ON_DEMAND_POPULATION = 0x00000004,
        CF_CONVERT_FLAG_ALWAYS_FULL = 0x00000008,
        CF_CONVERT_FLAG_FORCE_CONVERT_TO_CLOUD_FILE = 0x00000010,
    }

    [Flags]
    public enum CF_PLACEHOLDER_CREATE_FLAGS : uint
    {
        CF_PLACEHOLDER_CREATE_FLAG_NONE = 0x00000000,
        CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION = 0x00000001,
        CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC = 0x00000002,
        CF_PLACEHOLDER_CREATE_FLAG_SUPERSEDE = 0x00000004,
        CF_PLACEHOLDER_CREATE_FLAG_ALWAYS_FULL = 0x00000008,
    }

    [Flags]
    public enum CF_CREATE_FLAGS : uint
    {
        CF_CREATE_FLAG_NONE = 0x00000000,
        CF_CREATE_FLAG_STOP_ON_ERROR = 0x00000001,
    }

    // ═══════════════════════════════════════════════════════════════
    //  CF_PLACEHOLDER_CREATE_INFO 及相关结构体
    //  使用 LayoutKind.Sequential 扁平布局，与参考项目保持一致。
    //  RelativeFileName 用 IntPtr，由调用者手动 Marshal。
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// CF_FS_METADATA — 文件系统元数据（扁平结构，含显式 padding）
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CF_FS_METADATA
    {
        public long BasicInfo_CreationTime;
        public long BasicInfo_LastAccessTime;
        public long BasicInfo_LastWriteTime;
        public long BasicInfo_ChangeTime;
        public uint BasicInfo_FileAttributes;
        public uint _padding;               // 对齐 FileSize 到 8 字节
        public long FileSize;
    }

    /// <summary>
    /// CF_PLACEHOLDER_CREATE_INFO — CfCreatePlaceholders 的占位符描述。
    /// RelativeFileName 是 IntPtr（LPCWSTR），由调用者用 Marshal.StringToHGlobalUni 分配。
    /// Result / CreateUsn 为输出字段。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CF_PLACEHOLDER_CREATE_INFO
    {
        public IntPtr RelativeFileName;                 // LPCWSTR
        public CF_FS_METADATA FsMetadata;
        public IntPtr FileIdentity;                     // LPCVOID
        public uint FileIdentityLength;
        public CF_PLACEHOLDER_CREATE_FLAGS Flags;
        public int Result;                              // HRESULT [output]
        public long CreateUsn;                          // USN [output]
    }

    // 文件属性常量
    public const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
    public const uint FILE_ATTRIBUTE_NORMAL = 0x80;

    // ═══════════════════════════════════════════════════════════════
    //  回调委托 — 使用 IntPtr 避免结构体编组问题
    // ═══════════════════════════════════════════════════════════════

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    public delegate void CF_CALLBACK(IntPtr callbackInfo, IntPtr callbackParams);

    // ═══════════════════════════════════════════════════════════════
    //  CF_CALLBACK_INFO — 只读取需要的字段
    //
    //  x64 内存布局 (来自 Windows SDK cfapi.h):
    //    Offset  0: StructSize          (ULONG, 4)
    //    Offset  4: padding             (4)
    //    Offset  8: ConnectionKey       (LARGE_INTEGER, 8)
    //    Offset 16: CallbackContext     (pointer, 8)
    //    Offset 24: VolumeGuidName      (PCWSTR, 8)
    //    Offset 32: VolumeDosName       (PCWSTR, 8)
    //    Offset 40: VolumeSerialNumber  (ULONG, 4)
    //    Offset 44: padding             (4)
    //    Offset 48: SyncRootFileId      (LARGE_INTEGER, 8)
    //    Offset 56: SyncRootIdentity    (pointer, 8)
    //    Offset 64: SyncRootIdentityLen (ULONG, 4)
    //    Offset 68: padding             (4)
    //    Offset 72: FileId              (LARGE_INTEGER, 8)
    //    Offset 80: FileSize            (LARGE_INTEGER, 8)
    //    Offset 88: FileIdentity        (pointer, 8)
    //    Offset 96: FileIdentityLength  (ULONG, 4)
    //    Offset 100: padding            (4)
    //    Offset 104: NormalizedPath     (PCWSTR, 8)
    //    Offset 112: TransferKey        (LARGE_INTEGER, 8)
    //    Offset 120: PriorityHint       (UCHAR, 1)
    //    Offset 121: padding            (7)
    //    Offset 128: CorrelationVector  (pointer, 8)
    //    Offset 136: ProcessInfo        (pointer, 8)
    //    Offset 144: RequestKey         (LARGE_INTEGER, 8)
    //    Total: 152 bytes
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 从 CF_CALLBACK_INFO 的原始指针安全读取需要的字段
    /// </summary>
    public static class CallbackInfoReader
    {
        public static long GetConnectionKey(IntPtr info) => Marshal.ReadInt64(info, 8);
        public static long GetTransferKey(IntPtr info) => Marshal.ReadInt64(info, 112);
        public static IntPtr GetNormalizedPath(IntPtr info) => Marshal.ReadIntPtr(info, 104);
        public static IntPtr GetCorrelationVector(IntPtr info) => Marshal.ReadIntPtr(info, 128);
        public static long GetRequestKey(IntPtr info) => Marshal.ReadInt64(info, 144);

        /// <summary>
        /// 读取 NormalizedPath 并转换为 string
        /// </summary>
        public static string GetNormalizedPathString(IntPtr info)
        {
            IntPtr ptr = GetNormalizedPath(info);
            return ptr == IntPtr.Zero ? "(null)" : Marshal.PtrToStringUni(ptr) ?? "(null)";
        }
    }

    // ═══════════════════════════════════════════════════════════════
    //  CF_OPERATION_INFO (x64, 48 bytes)
    //
    //    Offset  0: StructSize       (ULONG, 4)
    //    Offset  4: Type             (enum, 4)
    //    Offset  8: ConnectionKey    (LARGE_INTEGER, 8)
    //    Offset 16: TransferKey      (LARGE_INTEGER, 8)
    //    Offset 24: CorrelationVector (pointer, 8)
    //    Offset 32: SyncStatus       (pointer, 8)   ← 之前遗漏！
    //    Offset 40: RequestKey       (LARGE_INTEGER, 8)
    //    Total: 48 bytes
    // ═══════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_OPERATION_INFO
    {
        public uint StructSize;
        public CF_OPERATION_TYPE Type;
        public long ConnectionKey;
        public long TransferKey;
        public IntPtr CorrelationVector;
        public IntPtr SyncStatus;               // CF_SYNC_STATUS*，可为 IntPtr.Zero
        public long RequestKey;
    }

    // ═══════════════════════════════════════════════════════════════
    //  CF_OPERATION_PARAMETERS — TransferPlaceholders 变体 (x64)
    //
    //    Offset  0: ParamSize             (ULONG, 4)
    //    Offset  4: padding               (4, 对齐 union 到 8)
    //    —— union TransferPlaceholders 开始 ——
    //    Offset  8: Flags                 (ULONG, 4)
    //    Offset 12: CompletionStatus      (NTSTATUS=LONG, 4)
    //    Offset 16: PlaceholderTotalCount (LONGLONG, 8)
    //    Offset 24: PlaceholderArray      (pointer, 8)
    //    Offset 32: PlaceholderCount      (ULONG, 4)
    //    Offset 36: EntriesProcessed      (ULONG, 4)
    //
    //    ParamSize = 8 (header) + 32 (TransferPlaceholders) = 40
    //    Struct size = 48 (aligned to largest union variant)
    // ═══════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════
    //  CF_OPERATION_PARAMETERS — TransferPlaceholders 变体 (x64)
    //  使用 Sequential 布局，对齐参考项目。
    // ═══════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_OPERATION_PARAMETERS
    {
        public uint ParamSize;
        public uint _padding;               // 对齐 union 到 8 字节
        // union TransferPlaceholders:
        public uint Flags;
        public int CompletionStatus;        // NTSTATUS
        public long PlaceholderTotalCount;
        public IntPtr PlaceholderArray;     // CF_PLACEHOLDER_CREATE_INFO*
        public uint PlaceholderCount;
        public uint EntriesProcessed;
    }

    // ═══════════════════════════════════════════════════════════════
    //  CF_CALLBACK_REGISTRATION
    // ═══════════════════════════════════════════════════════════════

    [StructLayout(LayoutKind.Sequential)]
    public struct CF_CALLBACK_REGISTRATION
    {
        public CF_CALLBACK_TYPE Type;
        public IntPtr Callback;

        public static CF_CALLBACK_REGISTRATION CF_CALLBACK_REGISTRATION_END => new()
        {
            Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE,
            Callback = IntPtr.Zero
        };
    }
}
