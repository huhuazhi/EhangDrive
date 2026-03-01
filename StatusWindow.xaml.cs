using System.ComponentModel;
using System.Windows;
using EhangNAS_Sync.Services;

namespace EhangNAS_Sync;

public partial class StatusWindow : Window
{
    private readonly SyncProviderConnection _connection;

    public StatusWindow(string syncFolder, string username, SyncProviderConnection connection)
    {
        InitializeComponent();
        _connection = connection;
        TxtInfo.Text = $"用户: {username}\n同步目录: {syncFolder}";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _connection.Dispose();
    }
}
