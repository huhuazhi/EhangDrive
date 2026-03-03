using System.IO;
using System.Threading;
using System.Windows;
using EhangNAS_Sync.Models;
using EhangNAS_Sync.Services;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace EhangNAS_Sync;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;

    private SyncProviderConnection? _connection;
    private TrayIconService? _trayIcon;
    private SyncEngine? _syncEngine;
    private FileWatcherService? _fileWatcher;
    private ModListPollingService? _modListPoller;
    private MainWindow? _mainWindow;
    private string? _currentUsername;

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        // ──── 单实例检查 ─────────────────────────────────────────
        _singleInstanceMutex = new Mutex(true, "Global\\EhangDrive_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("亿航Drive 已在运行中。", "亿航Drive", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 防止关闭最后一个窗口时退出应用（托盘模式）
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        FileLogger.CleanupOldLogs();          // 启动时清理 7 天前的旧日志
        FileLogger.Log("========== 应用启动 ==========");
        await StartSession();
    }

    // ═══════════════════════════════════════════════════════════════
    //  会话启动：登录 → 选择同步目录 → 注册 → 连接 → 启动主窗口
    // ═══════════════════════════════════════════════════════════════

    private async Task StartSession()
    {
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

        _currentUsername = username;

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
        var clientId = ConfigService.GetOrCreateClientId();
        var api = new SyncApiService(newConfig.Server, newConfig.Token, clientId);
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

        // ──── 启动同步引擎（文件监听在全量同步完成后启动）───────
        _syncEngine = new SyncEngine(api, newConfig.SyncFolder);
        SyncProviderConnection.SetSyncEngine(_syncEngine);

        _fileWatcher = new FileWatcherService(newConfig.SyncFolder, _syncEngine);
        // 注意：_fileWatcher.Start() 延迟到全量同步完成后调用
        // 避免全量同步中的文件操作（删除旧文件→重建占位符）被误识别为用户操作

        // ──── 清空上一轮的日志和传输列表 ─────────────────────────
        SyncStatusManager.Instance.Clear();

        // ──── 先创建并显示主窗口（让 UI 立即可见）───────────────
        _mainWindow = new MainWindow(newConfig, _connection,
            onLogout: HandleLogout,
            onChangeSyncFolder: HandleChangeSyncFolder,
            syncEngine: _syncEngine,
            api: api);

        // ──── 创建托盘图标 ───────────────────────────────────────
        _trayIcon = new TrayIconService(
            newConfig.SyncFolder,
            showMainWindow: () => { _mainWindow?.Show(); _mainWindow?.Activate(); },
            showSettings: () => _mainWindow?.ShowSettingsTab(),
            exitApp: () =>
            {
                CleanupSession();
                _mainWindow?.ExitApplication();
            });

        _mainWindow.Show();

        // ──── 首次全量同步（如果尚未完成）──────────────────────
        bool initialSyncDone = InitialSyncService.IsCompleted(newConfig.SyncFolder);
        if (!initialSyncDone)
        {
            FileLogger.Log("InitialSync: 未完成标记，执行全量同步...");
            SyncStatusManager.Instance.AddLog("🔄", "正在执行全量同步...");
            try
            {
                await InitialSyncService.SyncAsync(api, newConfig.SyncFolder);
                initialSyncDone = true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"InitialSync 异常: {ex.Message}");
            }
        }
        else
        {
            FileLogger.Log("InitialSync: 已完成，跳过全量同步");
        }

        // ──── 注册客户端 ─────────────────────────────────────────
        var hostname = ConfigService.GetHostname();
        var localIp = ConfigService.GetLocalIp();
        RegisterClientResponse registerResult;
        try
        {
            registerResult = await api.RegisterClientAsync(clientId, hostname, localIp);
        }
        catch (Exception ex)
        {
            FileLogger.Log($"RegisterClient 异常: {ex.Message}");
            registerResult = new RegisterClientResponse { ClientCount = 1 };
        }
        int clientCount = registerResult.ClientCount;

        // ──── 服务端要求重新全量同步（被删除后重新注册）─────────
        if (registerResult.NeedFullSync && initialSyncDone)
        {
            FileLogger.Log("服务端要求重新全量同步（该客户端曾被删除）");
            InitialSyncService.ClearMarker(newConfig.SyncFolder);
            initialSyncDone = false;
            SyncStatusManager.Instance.AddLog("🔄", "服务端要求重新全量同步...");
            try
            {
                await InitialSyncService.SyncAsync(api, newConfig.SyncFolder);
                initialSyncDone = true;
            }
            catch (Exception ex)
            {
                FileLogger.Log($"重新全量同步异常: {ex.Message}");
            }
        }

        // ──── 全量同步完成后启动文件监听 ────────────────────────
        _fileWatcher.Start();
        FileLogger.Log("FileWatcher 已启动（全量同步完成后）");

        // ──── 全量同步完成后且有多客户端时才启动 modlist 轮询 ────
        if (initialSyncDone && clientCount >= 2)
        {
            _modListPoller = new ModListPollingService(api, newConfig.SyncFolder, _syncEngine);
            FileLogger.Log($"ModListPollingService 已启动（全量同步完成, {clientCount} 台客户端）");
        }
        else if (initialSyncDone && clientCount < 2)
        {
            FileLogger.Log($"ModListPollingService 未启动（仅 {clientCount} 台客户端，无需轮询）");
        }
        else
        {
            FileLogger.Log("ModListPollingService 未启动（全量同步未完成）");
        }

        FileLogger.Log($"同步引擎+文件监听 已启动, folder={newConfig.SyncFolder}, clients={clientCount}");
    }

    // ═══════════════════════════════════════════════════════════════
    //  登出：清理当前会话 → 清除凭据 → 返回登录界面
    // ═══════════════════════════════════════════════════════════════

    private async void HandleLogout()
    {
        FileLogger.Log("用户登出，返回登录界面");

        // 清除全量同步标记（登出后换账号/重新选目录需要重新同步）
        var oldConfig = ConfigService.Load();
        if (oldConfig?.SyncFolder != null)
            InitialSyncService.ClearMarker(oldConfig.SyncFolder);

        // 取消同步根注册
        if (_currentUsername != null)
        {
            try { SyncRootRegistrar.Unregister(_currentUsername); }
            catch (Exception ex) { FileLogger.Log($"Unregister 异常: {ex.Message}"); }
        }

        // 清理当前会话
        CleanupSession();
        _mainWindow?.ForceClose();
        _mainWindow = null;

        // 修改配置：清除 Token 和 SyncFolder，但保留 AutoLogin 偏好
        var config = ConfigService.Load();
        if (config != null)
        {
            config.Token = "";
            config.SyncFolder = null;
            // 保留 config.AutoLogin，这样用户下次看到登录窗口时勾选状态不变
            ConfigService.Save(config);
        }

        // 重新开始会话（会弹出登录窗口 → 选择同步目录）
        await StartSession();
    }

    // ═══════════════════════════════════════════════════════════════
    //  更改同步目录：选择新目录 → 清理 → 用新目录重连
    // ═══════════════════════════════════════════════════════════════

    private async void HandleChangeSyncFolder(string newFolder)
    {
        FileLogger.Log($"更改同步目录: {newFolder}");

        // 清除旧目录的全量同步标记（新目录需要重新全量同步）
        var oldConf = ConfigService.Load();
        if (oldConf?.SyncFolder != null)
            InitialSyncService.ClearMarker(oldConf.SyncFolder);

        // 取消旧同步根注册
        if (_currentUsername != null)
        {
            try { SyncRootRegistrar.Unregister(_currentUsername); }
            catch (Exception ex) { FileLogger.Log($"Unregister 异常: {ex.Message}"); }
        }

        // 清理当前会话
        CleanupSession();
        _mainWindow?.ForceClose();
        _mainWindow = null;

        // 更新配置中的同步目录（Token 保留，会自动登录，不弹出登录窗口）
        var config = ConfigService.Load();
        if (config != null)
        {
            config.SyncFolder = newFolder;
            ConfigService.Save(config);
        }

        // 重新开始会话
        await StartSession();
    }

    // ═══════════════════════════════════════════════════════════════
    //  清理当前同步会话
    // ═══════════════════════════════════════════════════════════════

    private void CleanupSession()
    {
        _modListPoller?.Dispose();
        _modListPoller = null;
        _fileWatcher?.Dispose();
        _fileWatcher = null;
        _syncEngine?.Dispose();
        _syncEngine = null;
        _connection?.Dispose();
        _connection = null;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
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

