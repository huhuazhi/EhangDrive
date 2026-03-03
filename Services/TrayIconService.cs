using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using EhangNAS_Sync.Models;

namespace EhangNAS_Sync.Services;

/// <summary>
/// 管理 Windows 任务栏托盘图标和右键菜单。
/// 状态指示灯（右下角立体圆点）：
///   绿色 = 空闲已连接
///   橙色 = 忙碌（有上传/下载/全量同步等任务）
///   闪烁 = 有新活动（橙色/无点交替，或绿色/无点交替）
/// </summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>全局实例，方便其他服务触发活动动画</summary>
    public static TrayIconService? Current { get; private set; }

    private readonly NotifyIcon _notifyIcon;
    private readonly string _syncFolder;
    private readonly Action _showMainWindow;
    private readonly Action _showSettings;
    private readonly Action _exitApp;

    // ─── 图标状态 ────────────────────────────────────────────
    private readonly Icon _iconGreen;    // 空闲：绿色立体圆点
    private readonly Icon _iconOrange;   // 忙碌：橙色立体圆点
    private readonly Icon _iconNoDot;    // 闪烁用：无圆点
    private readonly System.Windows.Forms.Timer _blinkTimer;
    private int _blinkRemaining;
    private int _busyCount;              // >0 表示有活动任务

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public TrayIconService(string syncFolder, Action showMainWindow, Action showSettings, Action exitApp)
    {
        Current = this;
        _syncFolder = syncFolder;
        _showMainWindow = showMainWindow;
        _showSettings = showSettings;
        _exitApp = exitApp;

        _iconGreen = CreateTrayIcon(DotColor.Green);
        _iconOrange = CreateTrayIcon(DotColor.Orange);
        _iconNoDot = CreateTrayIcon(DotColor.None);

        _notifyIcon = new NotifyIcon
        {
            Text = "亿航Drive - 空闲",
            Icon = _iconGreen,
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _notifyIcon.DoubleClick += (_, _) => _showMainWindow();

        _blinkTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _blinkTimer.Tick += OnBlinkTick;
    }

    // ═══════════════════════════════════════════════════════════
    //  忙碌状态管理
    // ═══════════════════════════════════════════════════════════

    /// <summary>标记有一个新的忙碌任务开始（全量同步/上传/下载等）</summary>
    public void BeginBusy(string? taskName = null)
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d)
            d.BeginInvoke(() =>
            {
                Interlocked.Increment(ref _busyCount);
                _notifyIcon.Icon = _iconOrange;
                _notifyIcon.Text = $"亿航Drive - 同步中{(taskName != null ? $"（{taskName}）" : "")}";
            });
    }

    /// <summary>标记一个忙碌任务结束</summary>
    public void EndBusy()
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d)
            d.BeginInvoke(() =>
            {
                var count = Interlocked.Decrement(ref _busyCount);
                if (count <= 0)
                {
                    _busyCount = 0;
                    _notifyIcon.Icon = _iconGreen;
                    _notifyIcon.Text = "亿航Drive - 空闲";
                }
            });
    }

    /// <summary>当前是否忙碌</summary>
    public bool IsBusy => _busyCount > 0;

    // ═══════════════════════════════════════════════════════════
    //  活动闪烁（硬盘灯效果）
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 通知托盘图标有同步活动，触发闪烁（像硬盘指示灯一样）。
    /// 忙碌时橙色闪烁，空闲时绿色闪烁。可从任意线程安全调用。
    /// </summary>
    public void NotifyActivity()
    {
        if (System.Windows.Application.Current?.Dispatcher is { } d)
            d.BeginInvoke(StartBlink);
    }

    private void StartBlink()
    {
        _blinkRemaining = 6; // 3 轮亮灭闪烁（约 900ms）
        if (!_blinkTimer.Enabled)
            _blinkTimer.Start();
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        if (_blinkRemaining <= 0)
        {
            // 恢复到当前状态对应的常亮图标
            _notifyIcon.Icon = IsBusy ? _iconOrange : _iconGreen;
            _blinkTimer.Stop();
            return;
        }
        _blinkRemaining--;
        var dotIcon = IsBusy ? _iconOrange : _iconGreen;
        _notifyIcon.Icon = (_blinkRemaining % 2 == 0) ? dotIcon : _iconNoDot;
    }

    // ═══════════════════════════════════════════════════════════
    //  16×16 托盘图标绘制
    // ═══════════════════════════════════════════════════════════

    private enum DotColor { None, Green, Orange }

    /// <summary>
    /// 程序化绘制 16×16 托盘图标：蓝色圆角背景 + 白色 "e" + 立体状态圆点
    /// </summary>
    private static Icon CreateTrayIcon(DotColor dotColor)
    {
        const int sz = 16;
        using var bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.Clear(Color.Transparent);

            // ── 蓝色圆角方形背景 ──
            using var bgPath = new GraphicsPath();
            const float cr = 3.5f;
            bgPath.AddArc(0f, 0f, cr, cr, 180, 90);
            bgPath.AddArc(sz - cr - 1, 0f, cr, cr, 270, 90);
            bgPath.AddArc(sz - cr - 1, sz - cr - 1, cr, cr, 0, 90);
            bgPath.AddArc(0f, sz - cr - 1, cr, cr, 90, 90);
            bgPath.CloseFigure();

            using var bgBrush = new LinearGradientBrush(
                new Rectangle(0, 0, sz, sz),
                Color.FromArgb(255, 0, 137, 208),   // #0089D0
                Color.FromArgb(255, 0, 82, 155),    // #00529B
                LinearGradientMode.ForwardDiagonal);
            g.FillPath(bgBrush, bgPath);

            // ── 白色 "e" 字母 ──
            using var ePen = new Pen(Color.White, 2.0f);
            ePen.StartCap = LineCap.Round;
            ePen.EndCap = LineCap.Round;
            var eRect = new RectangleF(3f, 2.5f, 9f, 9f);
            g.DrawArc(ePen, eRect, 20, 320); // 开口朝右
            g.DrawLine(ePen, 3.8f, 7f, 11.5f, 7f); // 中横线

            // ── 立体状态圆点（右下角）──
            if (dotColor != DotColor.None)
            {
                DrawDot3D(g, 10.2f, 10.2f, 5.5f, dotColor);
            }
        }

        var hIcon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hIcon).Clone();
        DestroyIcon(hIcon);
        return icon;
    }

    /// <summary>
    /// 绘制立体圆点（带高光、阴影、白色边框）
    /// </summary>
    private static void DrawDot3D(Graphics g, float x, float y, float d, DotColor color)
    {
        var rect = new RectangleF(x, y, d, d);

        // 外圈白色边框
        using var borderPen = new Pen(Color.White, 1.0f);
        g.DrawEllipse(borderPen, rect);

        // 底色
        Color baseColor = color == DotColor.Green
            ? Color.FromArgb(255, 76, 175, 80)    // #4CAF50
            : Color.FromArgb(255, 255, 152, 0);   // #FF9800

        using var baseBrush = new SolidBrush(baseColor);
        g.FillEllipse(baseBrush, rect);

        // 高光（顶部偏亮）- 立体效果
        var highlightRect = new RectangleF(x + d * 0.15f, y + d * 0.05f, d * 0.7f, d * 0.5f);
        Color hlColor = color == DotColor.Green
            ? Color.FromArgb(120, 200, 255, 200)
            : Color.FromArgb(120, 255, 230, 150);
        using var hlBrush = new SolidBrush(hlColor);
        g.FillEllipse(hlBrush, highlightRect);
    }

    // ═══════════════════════════════════════════════════════════
    //  右键菜单
    // ═══════════════════════════════════════════════════════════

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
        Current = null;
        _blinkTimer.Stop();
        _blinkTimer.Dispose();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _iconGreen.Dispose();
        _iconOrange.Dispose();
        _iconNoDot.Dispose();
    }
}
