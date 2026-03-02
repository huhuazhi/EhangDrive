using System.IO;
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
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) }; // 大文件上传需要更长超时
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
    /// 下载文件指定范围的字节（FETCH_DATA 回调使用）
    /// </summary>
    public async Task<byte[]> DownloadFileBytesAsync(string relativePath, long offset, long length)
    {
        try
        {
            var url = $"{_baseUrl}/download?path={Uri.EscapeDataString(relativePath)}";
            FileLogger.Log($"DownloadFileBytesAsync: GET {url} (offset={offset}, length={length})");
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (length > 0)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
            var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var data = await resp.Content.ReadAsByteArrayAsync();
            FileLogger.Log($"  下载完成: {data.Length} bytes");
            return data;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  DownloadFileBytesAsync 异常: {ex.Message}");
            throw;
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

    /// <summary>
    /// 上传本地文件到服务端（流式 PUT /upload-stream）
    /// </summary>
    public async Task<bool> UploadFileAsync(string relativePath, string localFullPath,
        Action<long, long>? onProgress = null)
    {
        try
        {
            var mtime = new DateTimeOffset(File.GetLastWriteTimeUtc(localFullPath)).ToUnixTimeSeconds();
            var fi = new FileInfo(localFullPath);
            long totalSize = fi.Length;

            using var fs = File.OpenRead(localFullPath);
            HttpContent fileContent;
            if (onProgress != null && totalSize > 0)
                fileContent = new ProgressStreamContent(fs, totalSize, onProgress);
            else
                fileContent = new StreamContent(fs);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

            var url = $"{_baseUrl}/upload-stream?path={Uri.EscapeDataString(relativePath)}&mtime={mtime}&offset=0";
            FileLogger.Log($"UploadFileAsync: PUT {url} (size={totalSize})");
            var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = fileContent };

            var resp = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var body = await resp.Content.ReadAsStringAsync();
            FileLogger.Log($"  HTTP {(int)resp.StatusCode}: {body}");
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  UploadFileAsync 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 带进度报告的 HTTP 内容（用于上传大文件时显示进度）
    /// </summary>
    private class ProgressStreamContent : HttpContent
    {
        private readonly Stream _stream;
        private readonly long _totalSize;
        private readonly Action<long, long> _onProgress;

        public ProgressStreamContent(Stream stream, long totalSize, Action<long, long> onProgress)
        {
            _stream = stream;
            _totalSize = totalSize;
            _onProgress = onProgress;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            var buffer = new byte[81920];
            long uploaded = 0;
            int read;
            while ((read = await _stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await stream.WriteAsync(buffer, 0, read);
                uploaded += read;
                _onProgress(uploaded, _totalSize);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _totalSize;
            return true;
        }
    }

    /// <summary>
    /// 删除服务端文件或目录
    /// </summary>
    public async Task<bool> DeleteAsync(string relativePath)
    {
        try
        {
            var url = $"{_baseUrl}/delete";
            var payload = JsonSerializer.Serialize(new { path = relativePath });
            FileLogger.Log($"DeleteAsync: POST {url} (path={relativePath})");
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            FileLogger.Log($"  HTTP {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  DeleteAsync 异常: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 重命名服务端文件或目录
    /// </summary>
    public async Task<bool> RenameAsync(string oldPath, string newPath)
    {
        try
        {
            var url = $"{_baseUrl}/rename";
            var payload = JsonSerializer.Serialize(new { old_path = oldPath, new_path = newPath });
            FileLogger.Log($"RenameAsync: POST {url} ({oldPath} → {newPath})");
            var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content);
            FileLogger.Log($"  HTTP {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            FileLogger.Log($"  RenameAsync 异常: {ex.Message}");
            return false;
        }
    }
}
