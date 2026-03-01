using System.IO;
using System.Windows;
using EhangNAS_Sync.Models;
using EhangNAS_Sync.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace EhangNAS_Sync;

public partial class App : Application
{
    private SyncProviderConnection? _connection;
    private TrayIconService? _trayIcon;
    private SyncEngine? _syncEngine;
    private FileWatcherService? _fileWatcher;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        // 防止关闭最后一个窗口时退出应用（托盘模式）
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        FileLogger.Log("========== 应用启动 ==========");

        string? token = null;
        string? server = null;
        string? username = null;
        bool autoLogin = false;

        // ──── 尝试自动登录 ────────────────────────────────────────
        var config = ConfigService.Load();
        if (config is { AutoLogin: true, Token.Length: > 0 })
        {
            var auth = new AuthService();
            var valid = await auth.ValidateTokenAsync(config.Server, config.Token);
            if (valid)
            {
                token = config.Token;
                server = config.Server;
                username = config.Username;
                autoLogin = true;
            }
        }

        // ──── 若自动登录失败，弹出登录窗口 ────────────────────────
        if (token == null)
        {
            var loginWindow = new LoginWindow();
            if (loginWindow.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
            token = loginWindow.Token;
            server = loginWindow.ServerAddress;
            username = loginWindow.LoggedInUsername;
            autoLogin = loginWindow.AutoLogin;
        }

        // ──── 保存登录配置 ────────────────────────────────────────
        var newConfig = new LoginConfig
        {
            Server = server!,
            Username = username!,
            Token = token!,
            AutoLogin = autoLogin,
            SyncFolder = config?.SyncFolder,
        };

        // ──── 如果还没有同步目录，弹出文件夹选择 ─────────────────
        if (string.IsNullOrEmpty(newConfig.SyncFolder) || !Directory.Exists(newConfig.SyncFolder))
        {
            var selectedFolder = PickSyncFolder();
            if (string.IsNullOrEmpty(selectedFolder))
            {
                MessageBox.Show("未选择同步文件夹，程序将退出。",
                    "亿航Drive", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
            newConfig.SyncFolder = selectedFolder;
        }

        // ──── 注册同步根目录 ─────────────────────────────────────
        try
        {
            FileLogger.Log($"注册同步根: folder={newConfig.SyncFolder}, user={username}");
            await SyncRootRegistrar.RegisterAsync(newConfig.SyncFolder, username!);
            FileLogger.Log("注册同步根 成功");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"注册同步根 失败: {ex.Message}");
            MessageBox.Show(
                $"注册同步根目录失败:\n{ex.Message}",
                "亿航Drive", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // ──── 连接同步根（保持云提供程序在线）────────────────────
        var api = new SyncApiService(newConfig.Server, newConfig.Token);
        try
        {
            _connection = new SyncProviderConnection();
            _connection.Connect(newConfig.SyncFolder, api);
            FileLogger.Log("SyncProviderConnection.Connect 成功");
        }
        catch (Exception ex)
        {
            FileLogger.Log($"SyncProviderConnection.Connect 失败: {ex.Message}\n{ex.StackTrace}");
            MessageBox.Show(
                $"连接同步根目录失败:\n{ex.Message}",
                "亿航Drive", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        ConfigService.Save(newConfig);

        // ──── 启动同步引擎 + 文件监听 ───────────────────────────
        _syncEngine = new SyncEngine(api, newConfig.SyncFolder);
        _fileWatcher = new FileWatcherService(newConfig.SyncFolder, _syncEngine);
        _fileWatcher.Start();
        FileLogger.Log($"同步引擎+文件监听 已启动, folder={newConfig.SyncFolder}");

        // ──── 创建主窗口 ─────────────────────────────────────────
        var mainWindow = new MainWindow(newConfig, _connection);

        // ──── 创建托盘图标 ───────────────────────────────────────
        _trayIcon = new TrayIconService(
            newConfig.SyncFolder,
            showMainWindow: () => { mainWindow.Show(); mainWindow.Activate(); },
            showSettings: () => mainWindow.ShowSettingsTab(),
            exitApp: () =>
            {
                _fileWatcher?.Dispose();
                _syncEngine?.Dispose();
                _trayIcon?.Dispose();
                mainWindow.ExitApplication();
            });

        mainWindow.Show();
    }

    private static string? PickSyncFolder()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "请选择本地同步文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }
}

