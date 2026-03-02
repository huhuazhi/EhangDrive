using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EhangNAS_Sync.Models;

/// <summary>
/// 文件传输任务（上传/下载）的进度信息
/// </summary>
public class TransferItem : INotifyPropertyChanged
{
    private string _fileName = "";
    private TransferDirection _direction;
    private double _progress;
    private string _speed = "";
    private TransferStatus _status;

    public string FileName
    {
        get => _fileName;
        set { _fileName = value; OnPropertyChanged(); }
    }

    public TransferDirection Direction
    {
        get => _direction;
        set { _direction = value; OnPropertyChanged(); OnPropertyChanged(nameof(DirectionText)); }
    }

    public string DirectionText => Direction == TransferDirection.Upload ? "↑ 上传" : "↓ 下载";

    /// <summary>0~100</summary>
    public double Progress
    {
        get => _progress;
        set { _progress = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProgressText)); }
    }

    public string ProgressText => $"{Progress:F1}%";

    public string Speed
    {
        get => _speed;
        set { _speed = value; OnPropertyChanged(); }
    }

    public TransferStatus Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusText)); OnPropertyChanged(nameof(StatusColor)); }
    }

    /// <summary>进度条颜色：传输中蓝色、已完成绿色、失败红色</summary>
    public string StatusColor => Status switch
    {
        TransferStatus.Completed => "#27AE60",
        TransferStatus.Failed => "#E74C3C",
        _ => "#1A73E8"
    };

    public string StatusText => Status switch
    {
        TransferStatus.Waiting => "等待中",
        TransferStatus.Transferring => "传输中",
        TransferStatus.Completed => "已完成",
        TransferStatus.Failed => "失败",
        _ => ""
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum TransferDirection
{
    Upload,
    Download
}

public enum TransferStatus
{
    Waiting,
    Transferring,
    Completed,
    Failed
}

/// <summary>
/// 同步日志条目
/// </summary>
public class SyncLogEntry
{
    public DateTime Time { get; set; } = DateTime.Now;
    public string Icon { get; set; } = "🔵";
    public string Message { get; set; } = "";

    public string DisplayText => $"[{Time:HH:mm:ss}] {Icon} {Message}";
}

/// <summary>
/// 全局同步状态管理器，供主窗口绑定
/// </summary>
public class SyncStatusManager : INotifyPropertyChanged
{
    public static SyncStatusManager Instance { get; } = new();

    public ObservableCollection<TransferItem> Transfers { get; } = new();
    public ObservableCollection<SyncLogEntry> Logs { get; } = new();

    public void AddLog(string icon, string message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Logs.Insert(0, new SyncLogEntry { Icon = icon, Message = message });
            // 最多保留 200 条日志
            while (Logs.Count > 200)
                Logs.RemoveAt(Logs.Count - 1);
        });
    }

    public void AddTransfer(TransferItem item)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Transfers.Insert(0, item);
            // 最多保留 50 条传输历史
            while (Transfers.Count > 50)
                Transfers.RemoveAt(Transfers.Count - 1);
        });
    }

    public void RemoveTransfer(TransferItem item)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Transfers.Remove(item);
        });
    }

    public void Clear()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            Transfers.Clear();
            Logs.Clear();
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
