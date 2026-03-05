namespace EhangNAS_Sync.Models;

public class LoginConfig
{
    public string Server { get; set; } = "";
    public string Username { get; set; } = "";
    public string Token { get; set; } = "";
    public bool AutoLogin { get; set; }
    public bool StartMinimized { get; set; } = true;
    public string? SyncFolder { get; set; }
}
