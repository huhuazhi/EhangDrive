using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using EhangNAS_Sync.Models;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 管理 Windows 任务栏托盘图标和右键菜单
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly string _syncFolder;
    private readonly Action _showMainWindow;
    private readonly Action _showSettings;
    private readonly Action _exitApp;

    public TrayIconService(string syncFolder, Action showMainWindow, Action showSettings, Action exitApp)
    {
        _syncFolder = syncFolder;
        _showMainWindow = showMainWindow;
        _showSettings = showSettings;
        _exitApp = exitApp;

        _notifyIcon = new NotifyIcon
        {
            Text = "亿航Drive",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _notifyIcon.DoubleClick += (_, _) => _showMainWindow();
    }

    private static Icon LoadTrayIcon()
    {
        // 使用系统图标
        return SystemIcons.Application;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        var openFolder = new ToolStripMenuItem("打开亿航Drive目录");
        openFolder.Click += (_, _) =>
        {
            if (Directory.Exists(_syncFolder))
                Process.Start("explorer.exe", _syncFolder);
        };

        var settings = new ToolStripMenuItem("设置");
        settings.Click += (_, _) => _showSettings();

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => _exitApp();

        menu.Items.Add(openFolder);
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exit);

        return menu;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
