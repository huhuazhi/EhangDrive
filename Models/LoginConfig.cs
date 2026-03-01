namespace EhangNAS_Sync.Models;

public class LoginConfig
{
    public string Server { get; set; } = "";
    public string Username { get; set; } = "";
    public string Token { get; set; } = "";
    public bool AutoLogin { get; set; }
    public string? SyncFolder { get; set; }
}
