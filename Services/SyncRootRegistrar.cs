using System.IO;
using EhangNAS_Sync.Native;
using Windows.Security.Cryptography;
using Windows.Storage;
using Windows.Storage.Provider;

namespace EhangNAS_Sync.Services;

public static class SyncRootRegistrar
{
    private const string ProviderId = "EhangNAS";
    private const string DisplayName = "亿航Drive";

    /// <summary>
    /// 将指定的本地文件夹注册为 Windows Cloud Files 同步根目录
    /// </summary>
    public static async Task RegisterAsync(string syncFolderPath, string username)
    {
        // 确保文件夹存在
        Directory.CreateDirectory(syncFolderPath);

        var folder = await StorageFolder.GetFolderFromPathAsync(syncFolderPath);

        // 生成唯一的同步根 ID
        var syncRootId = BuildSyncRootId(username);

        // 如果已注册且路径一致，仍执行 Register（覆盖更新策略，不会影响现有 placeholder）
        // 路径不同则需要先注销再重新注册（用户换了同步文件夹）
        if (IsRegistered(syncRootId))
        {
            var existingPath = GetRegisteredPath(syncRootId);
            if (!string.Equals(existingPath, syncFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                FileLogger.Log($"同步根路径已变更: {existingPath} → {syncFolderPath}，重新注册");
                try { StorageProviderSyncRootManager.Unregister(syncRootId); }
                catch { }
            }
        }

        var info = new StorageProviderSyncRootInfo
        {
            Id = syncRootId,
            Path = folder,
            DisplayNameResource = DisplayName,
            IconResource = @"%SystemRoot%\system32\imageres.dll,-1043",
            Version = "1.0",
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.StreamingAllowed
                                    | StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
            PopulationPolicy = StorageProviderPopulationPolicy.Full,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime
                         | StorageProviderInSyncPolicy.DirectoryCreationTime
                         | (StorageProviderInSyncPolicy)0x80000000, // PreserveInSyncForSyncEngine
            HardlinkPolicy = StorageProviderHardlinkPolicy.None,
            Context = CryptographicBuffer.ConvertStringToBinary(
                syncRootId, BinaryStringEncoding.Utf8),
            ShowSiblingsAsGroup = false,
        };

        // WinRT 注册（提供 Shell 集成、图标等）
        // 注意：跳过 WinRT 注册来测试纯原生注册是否让 VALIDATE_DATA 工作
        // StorageProviderSyncRootManager.Register(info);

        // 仅原生注册（含 VALIDATION_REQUIRED）
        EnableValidationRequired(syncFolderPath, syncRootId);
    }

    /// <summary>
    /// 通过原生 P/Invoke 更新同步根策略，添加 VALIDATION_REQUIRED。
    /// 必须在 WinRT Register 之后调用（使用 UPDATE 标志）。
    /// </summary>
    private static void EnableValidationRequired(string syncFolderPath, string syncRootId)
    {
        try
        {
            var identityBytes = System.Text.Encoding.UTF8.GetBytes(syncRootId);
            var identityPin = System.Runtime.InteropServices.GCHandle.Alloc(identityBytes, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var reg = new CldApi.CF_SYNC_REGISTRATION
                {
                    StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CldApi.CF_SYNC_REGISTRATION>(),
                    ProviderName = "亿航Drive",
                    ProviderVersion = "1.0",
                    SyncRootIdentity = identityPin.AddrOfPinnedObject(),
                    SyncRootIdentityLength = (uint)identityBytes.Length,
                    FileIdentity = IntPtr.Zero,
                    FileIdentityLength = 0,
                    ProviderId = Guid.Empty,
                };

                var policies = new CldApi.CF_SYNC_POLICIES
                {
                    StructSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<CldApi.CF_SYNC_POLICIES>(),
                    Hydration = new CldApi.CF_HYDRATION_POLICY
                    {
                        Primary = CldApi.CF_HYDRATION_POLICY_PRIMARY_FULL,
                        Modifier = (ushort)(CldApi.CF_HYDRATION_POLICY_MODIFIER_STREAMING_ALLOWED
                                          | CldApi.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED
                                          | CldApi.CF_HYDRATION_POLICY_MODIFIER_VALIDATION_REQUIRED),
                    },
                    Population = new CldApi.CF_POPULATION_POLICY
                    {
                        Primary = CldApi.CF_POPULATION_POLICY_PRIMARY_FULL,
                        Modifier = 0,
                    },
                    InSync = CldApi.CF_INSYNC_POLICY_TRACK_FILE_CREATION_TIME
                           | CldApi.CF_INSYNC_POLICY_TRACK_DIRECTORY_CREATION_TIME
                           | CldApi.CF_INSYNC_POLICY_PRESERVE_INSYNC_FOR_SYNC_ENGINE,
                    HardLink = 0,
                    PlaceholderManagement = 0,
                };

                int hr = CldApi.CfRegisterSyncRoot(
                    syncFolderPath,
                    in reg,
                    in policies,
                    CldApi.CF_REGISTER_FLAGS.CF_REGISTER_FLAG_NONE);

                FileLogger.Log($"CfRegisterSyncRoot(NATIVE+VALIDATION_REQUIRED) → 0x{hr:X8}" +
                    $" regSize={reg.StructSize} polSize={policies.StructSize}" +
                    $" hydPri={policies.Hydration.Primary} hydMod=0x{policies.Hydration.Modifier:X4}");
            }
            finally
            {
                identityPin.Free();
            }
        }
        catch (Exception ex)
        {
            FileLogger.Log($"EnableValidationRequired 异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 取消注册同步根目录
    /// </summary>
    public static void Unregister(string username)
    {
        var syncRootId = BuildSyncRootId(username);
        try
        {
            StorageProviderSyncRootManager.Unregister(syncRootId);
        }
        catch
        {
            // 未注册时忽略
        }
    }

    /// <summary>
    /// 检查指定的同步根是否已注册
    /// </summary>
    public static bool IsRegistered(string syncRootId)
    {
        try
        {
            var roots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
            foreach (var root in roots)
            {
                if (string.Equals(root.Id, syncRootId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // 忽略错误
        }
        return false;
    }

    /// <summary>
    /// 获取已注册同步根的路径
    /// </summary>
    private static string? GetRegisteredPath(string syncRootId)
    {
        try
        {
            var roots = StorageProviderSyncRootManager.GetCurrentSyncRoots();
            foreach (var root in roots)
            {
                if (string.Equals(root.Id, syncRootId, StringComparison.OrdinalIgnoreCase))
                    return root.Path?.Path;
            }
        }
        catch { }
        return null;
    }

    private static string BuildSyncRootId(string username)
    {
        return $"{ProviderId}!{Environment.UserName}!{username}";
    }
}
