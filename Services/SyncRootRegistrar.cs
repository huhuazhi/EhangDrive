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

        // 先取消旧注册（策略可能已变更）
        if (IsRegistered(syncRootId))
        {
            try { StorageProviderSyncRootManager.Unregister(syncRootId); } catch { }
        }

        var info = new StorageProviderSyncRootInfo
        {
            Id = syncRootId,
            Path = folder,
            DisplayNameResource = DisplayName,
            IconResource = @"%SystemRoot%\system32\imageres.dll,-1043",
            Version = "1.0",
            HydrationPolicy = StorageProviderHydrationPolicy.Full,
            HydrationPolicyModifier = StorageProviderHydrationPolicyModifier.AutoDehydrationAllowed,
            PopulationPolicy = StorageProviderPopulationPolicy.AlwaysFull,
            InSyncPolicy = StorageProviderInSyncPolicy.FileCreationTime
                         | StorageProviderInSyncPolicy.FileLastWriteTime
                         | StorageProviderInSyncPolicy.DirectoryCreationTime
                         | StorageProviderInSyncPolicy.DirectoryLastWriteTime,
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

    private static string BuildSyncRootId(string username)
    {
        return $"{ProviderId}!{Environment.UserName}!{username}";
    }
}
