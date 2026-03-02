using System.IO;
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

        StorageProviderSyncRootManager.Register(info);
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
