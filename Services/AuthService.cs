using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EhangNAS_Sync.Services;

public class AuthService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    public async Task<(bool Success, string? Token, string? Username, string? Error)> LoginAsync(
        string server, string username, string password)
    {
        try
        {
            var url = $"http://{server}/api/auth/login";
            var payload = JsonSerializer.Serialize(new { username, password });
            var content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await Http.PostAsync(url, content);
            var json = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(json);
                var token = doc.RootElement.GetProperty("token").GetString();
                var user = doc.RootElement.GetProperty("username").GetString();
                return (true, token, user, null);
            }
            else
            {
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    var detail = doc.RootElement.GetProperty("detail").GetString();
                    return (false, null, null, detail ?? "登录失败");
                }
                catch
                {
                    return (false, null, null, $"登录失败 (HTTP {(int)response.StatusCode})");
                }
            }
        }
        catch (TaskCanceledException)
        {
            return (false, null, null, "连接超时，请检查服务器地址");
        }
        catch (HttpRequestException ex)
        {
            return (false, null, null, $"无法连接到服务器: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, null, null, $"发生错误: {ex.Message}");
        }
    }

    public async Task<bool> ValidateTokenAsync(string server, string token)
    {
        try
        {
            var url = $"http://{server}/api/auth/me";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await Http.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
