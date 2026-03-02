using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using EhangNAS_Sync.Models;

namespace EhangNAS_Sync.Services;

public static class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YihangDrive");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    private static readonly string ClientPath = Path.Combine(ConfigDir, "client.json");

    public static LoginConfig? Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return null;
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<LoginConfig>(json);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(LoginConfig config)
    {
        Directory.CreateDirectory(ConfigDir);
        var options = new JsonSerializerOptions { WriteIndented = true };
        var json = JsonSerializer.Serialize(config, options);
        File.WriteAllText(ConfigPath, json);
    }

    // ═══════════════════════════════════════════════════════════════
    //  客户端 ID 持久化
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// 获取或生成客户端唯一 ID。首次调用时生成 UUID 并持久化。
    /// </summary>
    public static string GetOrCreateClientId()
    {
        try
        {
            if (File.Exists(ClientPath))
            {
                var json = File.ReadAllText(ClientPath);
                var data = JsonSerializer.Deserialize<ClientIdData>(json);
                if (!string.IsNullOrEmpty(data?.ClientId))
                    return data.ClientId;
            }
        }
        catch { }

        // 首次生成
        var clientId = Guid.NewGuid().ToString();
        try
        {
            Directory.CreateDirectory(ConfigDir);
            var data = new ClientIdData { ClientId = clientId };
            File.WriteAllText(ClientPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
            FileLogger.Log($"ClientId 已生成: {clientId}");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"ClientId 保存失败: {ex.Message}");
        }
        return clientId;
    }

    /// <summary>
    /// 获取本机 hostname。
    /// </summary>
    public static string GetHostname() => Dns.GetHostName();

    /// <summary>
    /// 获取本机局域网 IP 地址。
    /// </summary>
    public static string GetLocalIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 80);
            if (socket.LocalEndPoint is IPEndPoint ep)
                return ep.Address.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    private class ClientIdData
    {
        [JsonPropertyName("client_id")]
        public string ClientId { get; set; } = "";
    }
}
