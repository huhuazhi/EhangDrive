using System.IO;
using System.Text.Json;
using EhangNAS_Sync.Models;

namespace EhangNAS_Sync.Services;

public static class ConfigService
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "YihangDrive");

    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");

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
}
