using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EhangNAS_Sync.Services;

// ═══════════════════════════════════════════════════════════════
//  服务端 /tree API 响应模型
// ═══════════════════════════════════════════════════════════════

public class TreeItem
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("is_dir")]
    public bool IsDir { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    /// <summary>Unix timestamp (seconds since epoch)</summary>
    [JsonPropertyName("mtime")]
    public long Mtime { get; set; }
}

public class TreeResponse
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("items")]
    public List<TreeItem> Items { get; set; } = new();
}

// ═══════════════════════════════════════════════════════════════

/// <summary>
/// 封装服务端同步 API 调用
/// </summary>
public class SyncApiService
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public SyncApiService(string server, string token)
    {
        _baseUrl = $"http://{server}/api/sync-config";
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>
    /// 列出服务端目录内容（调用 GET /tree?path=xxx）
    /// </summary>
    public async Task<List<TreeItem>> ListDirectoryAsync(string relativePath)
    {
        try
        {
            var url = $"{_baseUrl}/tree?path={Uri.EscapeDataString(relativePath)}";
            FileLogger.Log($"ListDirectoryAsync: GET {url}");
            var response = await _http.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                FileLogger.Log($"  HTTP {(int)response.StatusCode}");
                return new List<TreeItem>();
            }
            var json = await response.Content.ReadAsStringAsync();
            var tree = JsonSerializer.Deserialize<TreeResponse>(json);
            var items = tree?.Items ?? new List<TreeItem>();
            FileLogger.Log($"  返回 {items.Count} 个条目");
            return items;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  ListDirectoryAsync 异常: {ex.Message}");
            return new List<TreeItem>();
        }
    }

    /// <summary>
    /// 在服务端创建目录
    /// </summary>
    public async Task<bool> MkdirAsync(string relativePath)
    {
        try
        {
            var url = $"{_baseUrl}/mkdir";
            var payload = JsonSerializer.Serialize(new { path = relativePath });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
