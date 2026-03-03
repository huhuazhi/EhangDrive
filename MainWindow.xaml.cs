using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using EhangNAS_Sync.Models;
using EhangNAS_Sync.Services;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace EhangNAS_Sync;

/// <summary>
/// 客户端列表项 ViewModel（用于 ListView 绑定）
/// </summary>
public class ClientDisplayItem
{
    public string ClientId { get; set; } = "";
    public string Hostname { get; set; } = "";
    public string Ip { get; set; } = "";
    public bool IsCurrent { get; set; }
    public string IsCurrentText => IsCurrent ? "✓" : "";
    public System.Windows.Visibility DeleteVisibility =>
        IsCurrent ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
}

public partial class MainWindow : Window
{
    private readonly LoginConfig _config;
    private readonly SyncProviderConnection _connection;
    private readonly SyncApiService? _api;
    private readonly Action _onLogout;
    private readonly Action<string> _onChangeSyncFolder;
    private readonly SyncEngine? _syncEngine;
    private readonly string _currentClientId;
    private bool _isExiting;

    public MainWindow(LoginConfig config, SyncProviderConnection connection,
                      Action onLogout, Action<string> onChangeSyncFolder,
                      SyncEngine? syncEngine = null,
                      SyncApiService? api = null)
    {
        InitializeComponent();
        _config = config;
        _connection = connection;
        _onLogout = onLogout;
        _onChangeSyncFolder = onChangeSyncFolder;
        _syncEngine = syncEngine;
        _api = api;
        _currentClientId = ConfigService.GetOrCreateClientId();

        // 绑定传输列表和日志
        LvTransfers.ItemsSource = SyncStatusManager.Instance.Transfers;
        LbLogs.ItemsSource = SyncStatusManager.Instance.Logs;

        // 填充设置页
        LoadSettings();

        // 每秒刷新剩余文件数 + 检查脏目录
        int _idleSeconds = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            int count = _syncEngine?.PendingCount ?? 0;
            TxtPendingCount.Text = count > 0 ? $"(剩余 {count} 个)" : "";

            // 队列空闲且有脏目录时，等连续空闲 2 秒再刷新
            // 避免目录创建后立刻 flush（子文件事件可能还没到）
            if (count == 0 && (_syncEngine?.HasDirtyDirectories ?? false))
            {
                _idleSeconds++;
                if (_idleSeconds >= 2)
                {
                    _idleSeconds = 0;
                    Task.Run(() => _syncEngine?.FlushDirtyDirectories());
                }
            }
            else
            {
                _idleSeconds = 0;
            }
        };
        timer.Start();

        // 添加初始日志
        SyncStatusManager.Instance.AddLog("✅", "亿航Drive 已启动");
        SyncStatusManager.Instance.AddLog("📂", $"同步目录: {_config.SyncFolder}");

        // 加载客户端列表
        _ = LoadClientsAsync();
    }

    private void LoadSettings()
    {
        TxtSettingServer.Text = _config.Server;
        TxtSettingUsername.Text = _config.Username;
        TxtSettingSyncFolder.Text = _config.SyncFolder ?? "";
        ChkAutoLogin.IsChecked = _config.AutoLogin;
        ChkAutoStart.IsChecked = IsAutoStartEnabled();
    }

    // ─── 客户端列表管理 ──────────────────────────────────────────
    private async Task LoadClientsAsync()
    {
        if (_api == null) return;
        try
        {
            var clients = await _api.GetClientsAsync();
            var items = clients.Select(c => new ClientDisplayItem
            {
                ClientId = c.ClientId,
                Hostname = c.Hostname,
                Ip = c.Ip,
                IsCurrent = string.Equals(c.ClientId, _currentClientId, StringComparison.OrdinalIgnoreCase),
            }).OrderByDescending(c => c.IsCurrent).ToList();

            Dispatcher.Invoke(() =>
            {
                LvClients.ItemsSource = items;
                TxtClientCount.Text = $"({items.Count} 台设备)";
            });
        }
        catch (Exception ex)
        {
            FileLogger.Log($"LoadClients 异常: {ex.Message}");
        }
    }

    private async void BtnRefreshClients_Click(object sender, RoutedEventArgs e)
    {
        BtnRefreshClients.IsEnabled = false;
        await LoadClientsAsync();
        BtnRefreshClients.IsEnabled = true;
    }

    private async void BtnDeleteClient_Click(object sender, RoutedEventArgs e)
    {
        if (_api == null) return;
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string clientId) return;

        var result = MessageBox.Show(
            $"确定要删除该客户端吗？\n删除后该设备将不再同步。",
            "亿航Drive", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;

        var ok = await _api.RemoveClientAsync(clientId);
        if (ok)
        {
            SyncStatusManager.Instance.AddLog("🗑️", "已删除一个客户端");
            await LoadClientsAsync();
        }
        else
        {
            MessageBox.Show("删除失败，请重试。", "亿航Drive",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ─── 窗口关闭 → 最小化到托盘，不退出 ─────────────────────────
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// 强制关闭窗口（不拦截关闭事件），用于会话重启
    /// </summary>
    public void ForceClose()
    {
        _isExiting = true;
        Close();
    }

    /// <summary>
    /// 真正退出应用（从托盘菜单调用）
    /// </summary>
    public void ExitApplication()
    {
        _isExiting = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// 跳转到设置 Tab
    /// </summary>
    public void ShowSettingsTab()
    {
        Show();
        Activate();
        // 第二个 Tab (index 1) 是设置
        var tabControl = (System.Windows.Controls.TabControl)
            ((Grid)Content).Children[0];
        tabControl.SelectedIndex = 1;
    }

    // ─── 登出 → 返回登录界面 ────────────────────────────────────────
    private void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要登出吗？将返回登录界面。",
            "亿航Drive", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        _onLogout();
    }

    // ─── 更改同步目录 ─────────────────────────────────────────────
    private void BtnChangeSyncFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "请选择新的同步文件夹",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = _config.SyncFolder ?? "",
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var newFolder = dialog.SelectedPath;
        if (string.Equals(newFolder, _config.SyncFolder, StringComparison.OrdinalIgnoreCase))
            return; // 同一目录，无需更改

        _onChangeSyncFolder(newFolder);
    }

    // ─── 开机自启动 ───────────────────────────────────────────────
    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        SetAutoStart(ChkAutoStart.IsChecked == true);
    }

    private void ChkAutoLoginSetting_Changed(object sender, RoutedEventArgs e)
    {
        _config.AutoLogin = ChkAutoLogin.IsChecked == true;
        ConfigService.Save(_config);
    }

    // ─── 注册表开机启动 ──────────────────────────────────────────
    private const string AutoStartKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string AutoStartName = "YihangDrive";

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, false);
        return key?.GetValue(AutoStartName) != null;
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(AutoStartKey, true);
        if (key == null) return;

        if (enable)
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath))
                key.SetValue(AutoStartName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(AutoStartName, false);
        }
    }
}

/// <summary>
/// 字符串为空时隐藏控件的转换器
/// </summary>
public class StringToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
    {
        return string.IsNullOrEmpty(value as string)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter,
        System.Globalization.CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
