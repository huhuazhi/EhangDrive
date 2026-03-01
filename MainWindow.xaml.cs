using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using EhangNAS_Sync.Models;
using EhangNAS_Sync.Services;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace EhangNAS_Sync;

public partial class MainWindow : Window
{
    private readonly LoginConfig _config;
    private readonly SyncProviderConnection _connection;
    private bool _isExiting;

    public MainWindow(LoginConfig config, SyncProviderConnection connection)
    {
        InitializeComponent();
        _config = config;
        _connection = connection;

        // 绑定传输列表和日志
        LvTransfers.ItemsSource = SyncStatusManager.Instance.Transfers;
        LbLogs.ItemsSource = SyncStatusManager.Instance.Logs;

        // 填充设置页
        LoadSettings();

        // 添加初始日志
        SyncStatusManager.Instance.AddLog("✅", "亿航Drive 已启动");
        SyncStatusManager.Instance.AddLog("📂", $"同步目录: {_config.SyncFolder}");
    }

    private void LoadSettings()
    {
        TxtSettingServer.Text = _config.Server;
        TxtSettingUsername.Text = _config.Username;
        TxtSettingSyncFolder.Text = _config.SyncFolder ?? "";
        ChkAutoLogin.IsChecked = _config.AutoLogin;
        ChkAutoStart.IsChecked = IsAutoStartEnabled();
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
    /// 真正退出应用（从托盘菜单调用）
    /// </summary>
    public void ExitApplication()
    {
        _isExiting = true;
        _connection.Dispose();
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

    // ─── 登出 ─────────────────────────────────────────────────────
    private void BtnLogout_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要登出吗？登出后将关闭同步连接并退出程序。",
            "亿航Drive", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        // 清除保存的 token
        _config.Token = "";
        _config.AutoLogin = false;
        ConfigService.Save(_config);

        // 取消同步根注册
        SyncRootRegistrar.Unregister(_config.Username);

        _isExiting = true;
        _connection.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    // ─── 更改同步目录 ─────────────────────────────────────────────
    private void BtnChangeSyncFolder_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("更改同步目录需要重新启动程序。\n请先登出，再重新登录选择新目录。",
            "亿航Drive", MessageBoxButton.OK, MessageBoxImage.Information);
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
