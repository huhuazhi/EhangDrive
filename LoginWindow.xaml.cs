using System.Windows;
using EhangNAS_Sync.Services;

namespace EhangNAS_Sync;

public partial class LoginWindow : Window
{
    private readonly AuthService _authService = new();
    private bool _passwordVisible;

    /// <summary>登录成功后的 Token</summary>
    public string? Token { get; private set; }

    /// <summary>服务器地址</summary>
    public string? ServerAddress { get; private set; }

    /// <summary>登录用户名</summary>
    public string? LoggedInUsername { get; private set; }

    /// <summary>是否勾选了自动登录</summary>
    public bool AutoLogin => ChkAutoLogin.IsChecked == true;

    public LoginWindow()
    {
        InitializeComponent();
        LoadSavedConfig();
        TxtUsername.Focus();
    }

    private void LoadSavedConfig()
    {
        var config = ConfigService.Load();
        if (config == null) return;

        TxtServer.Text = config.Server;
        TxtUsername.Text = config.Username;
        ChkAutoLogin.IsChecked = config.AutoLogin;
    }

    // ─── 密码小眼睛切换 ───────────────────────────────────────────
    private void BtnEye_Click(object sender, RoutedEventArgs e)
    {
        _passwordVisible = !_passwordVisible;

        if (_passwordVisible)
        {
            TxtPasswordPlain.Text = PwdPassword.Password;
            PwdPassword.Visibility = Visibility.Collapsed;
            TxtPasswordPlain.Visibility = Visibility.Visible;
            TxtPasswordPlain.Focus();
            TxtPasswordPlain.CaretIndex = TxtPasswordPlain.Text.Length;
            BtnEye.Content = "🙈";
        }
        else
        {
            PwdPassword.Password = TxtPasswordPlain.Text;
            TxtPasswordPlain.Visibility = Visibility.Collapsed;
            PwdPassword.Visibility = Visibility.Visible;
            PwdPassword.Focus();
            BtnEye.Content = "👁";
        }
    }

    private string GetPassword()
    {
        return _passwordVisible ? TxtPasswordPlain.Text : PwdPassword.Password;
    }

    // ─── 登录按钮 ─────────────────────────────────────────────────
    private async void BtnLogin_Click(object sender, RoutedEventArgs e)
    {
        var server = TxtServer.Text.Trim();
        var username = TxtUsername.Text.Trim();
        var password = GetPassword();

        // 输入验证
        if (string.IsNullOrEmpty(server))
        {
            ShowError("请输入服务器地址");
            return;
        }
        if (string.IsNullOrEmpty(username))
        {
            ShowError("请输入用户名");
            return;
        }
        if (string.IsNullOrEmpty(password))
        {
            ShowError("请输入密码");
            return;
        }

        SetLoading(true);
        HideError();

        var (success, token, user, error) = await _authService.LoginAsync(server, username, password);

        if (success)
        {
            Token = token;
            ServerAddress = server;
            LoggedInUsername = user;
            DialogResult = true;
            Close();
        }
        else
        {
            ShowError(error ?? "登录失败");
            SetLoading(false);
        }
    }

    // ─── UI 辅助方法 ──────────────────────────────────────────────
    private void SetLoading(bool loading)
    {
        BtnLogin.IsEnabled = !loading;
        BtnLogin.Content = loading ? "登录中..." : "登  录";
        TxtServer.IsEnabled = !loading;
        TxtUsername.IsEnabled = !loading;
        PwdPassword.IsEnabled = !loading;
        TxtPasswordPlain.IsEnabled = !loading;
        ChkAutoLogin.IsEnabled = !loading;
    }

    private void ShowError(string message)
    {
        TxtError.Text = message;
        TxtError.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        TxtError.Visibility = Visibility.Collapsed;
    }
}
