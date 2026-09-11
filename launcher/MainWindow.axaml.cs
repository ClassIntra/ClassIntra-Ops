using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ClassIntraOps.Launcher.Services;
using ClassIntraOps.Launcher.Views;

namespace ClassIntraOps.Launcher;

public partial class MainWindow : Window
{
    private readonly OverviewPage _overview = new();
    private readonly SecretsPage _secrets = new();
    private readonly PeersPage _peers = new();
    private readonly InstallPage _install = new();
    private readonly LogsPage _logs = new();
    private readonly SettingsPage _settings = new();

    public MainWindow()
    {
        InitializeComponent();

        AppState.Load();
        AppState.CiRoot ??= AppState.ResolveCiRoot();
        AppState.Save();

        NavList.SelectionChanged += (_, _) => SwitchPage();
        PageHost.Content = _overview;

        UpdateHeader();
        _ = _overview.RefreshAsync();
    }

    private void SwitchPage()
    {
        var tag = (NavList.SelectedItem as ListBoxItem)?.Tag as string ?? "overview";
        PageHost.Content = tag switch
        {
            "secrets" => _secrets,
            "peers" => _peers,
            "install" => _install,
            "logs" => _logs,
            "settings" => _settings,
            _ => _overview
        };

        // 切页时按需刷新，保持数据新鲜
        switch (tag)
        {
            case "overview": _ = _overview.RefreshAsync(); break;
            case "secrets": _ = _secrets.ReloadAsync(); break;
            case "peers": _ = _peers.RefreshAsync(); break;
            case "logs": _ = _logs.RefreshAsync(); break;
            case "settings": _settings.LoadInto(); break;
        }
    }

    public void UpdateHeader()
    {
        var root = AppState.ResolveCiRoot();
        var ok = root != null;
        CiStatusDot.Fill = new Avalonia.Media.SolidColorBrush(
            ok ? Avalonia.Media.Colors.MediumSeaGreen : Avalonia.Media.Colors.IndianRed);
        CiStatusText.Text = ok ? root : "未定位到 CI 仓库（到「环境设置」指定路径）";
        StatusCi.Text = "CI 仓库: " + (root ?? "未定位");

        try
        {
            var ips = Dns.GetHostAddressesAsync(Dns.GetHostName()).Result;
            var v4 = ips.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                        .Select(a => a.ToString()).ToList();
            StatusNet.Text = "本机地址: " + (v4.Count > 0 ? string.Join("、", v4) : "未知") +
                             " · 平台 " + (OperatingSystem.IsWindows() ? "Windows" :
                                OperatingSystem.IsLinux() ? "Linux" : "macOS");
        }
        catch
        {
            StatusNet.Text = "本机地址未知";
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        _overview.StopTimer();
        base.OnUnloaded(e);
    }
}
