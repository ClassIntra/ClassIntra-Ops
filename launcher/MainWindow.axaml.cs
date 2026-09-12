using System.Net;
using System.Net.Sockets;
using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassIntraOps.Launcher.Services;
using ClassIntraOps.Launcher.Views;

namespace ClassIntraOps.Launcher;

public partial class MainWindow : Window
{
    private readonly WelcomePage _welcome = new();
    private readonly OverviewPage _overview = new();
    private readonly SecretsPage _secrets = new();
    private readonly PeersPage _peers = new();
    private readonly InstallPage _install = new();
    private readonly LogsPage _logs = new();
    private readonly SettingsPage _settings = new();

    private TrayIcon? _tray;
    private bool _forceClose;

    public MainWindow()
    {
        InitializeComponent();

        AppState.Load();
        AppState.CiRoot ??= AppState.ResolveCiRoot();
        AppState.Save();

        NavList.SelectionChanged += (_, _) => SwitchPage();
        Closing += OnWindowClosing;

        // Window XAML 里声明 Transitions 会触发编译器 bug（AVLN2000），过渡一律代码后置
        PageHost.Transitions = new Transitions
        {
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }
        };

        // 首启落点：未定位 CI 仓库 → 欢迎页（引导先下载/定位）；已定位 → 总览
        // 注意：必须走 ListBox.SelectedItem，XAML 上的 IsSelected 会在容器实例化时覆盖代码赋值
        Select(AppState.ResolveCiRoot() == null ? "welcome" : "overview");

        UpdateHeader();
        if (AppState.ResolveCiRoot() == null) _ = _welcome.RefreshAsync();
        else _ = _overview.RefreshAsync();
    }

    /// <summary>按导航 tag 选中页面（ListBox 级别，避免 IsSelected 时序问题）。</summary>
    private void Select(string tag)
    {
        foreach (var item in NavList.Items.OfType<ListBoxItem>())
        {
            if ((item.Tag as string) == tag)
            {
                NavList.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>依赖 CI 仓库的页面：未定位前不允许进入（避免看到空数据而不知所以然）。</summary>
    private static bool IsGated(string tag) => tag is "secrets" or "peers" or "logs";

    private void SwitchPage()
    {
        var tag = (NavList.SelectedItem as ListBoxItem)?.Tag as string ?? "overview";

        // 门禁：被拦下时统一落到欢迎页，并把「为什么进不去 / 下一步做什么」交给欢迎页呈现
        if (IsGated(tag) && AppState.ResolveCiRoot() == null)
        {
            _welcome.SetGateHint(tag);
            Select("welcome");
            return;
        }
        _welcome.ClearGateHint();

        PageHost.Content = tag switch
        {
            "welcome" => _welcome,
            "secrets" => _secrets,
            "peers" => _peers,
            "install" => _install,
            "logs" => _logs,
            "settings" => _settings,
            _ => _overview
        };

        // 命令栏标题与副标题（页面自述：这一页能做什么）
        var (title, sub) = tag switch
        {
            "welcome" => ("欢迎", "三步开始：定位仓库 → 配置密钥 → 启动服务"),
            "secrets" => ("密钥配置", "schema 由 server/.env.example 推导 · 加字段零漂移"),
            "peers" => ("跨班对端", "RELAY_SERVERS 解析 · TCP 探活判定"),
            "install" => ("安装更新", "拉取源码 → 装依赖 → 构建 → PM2 启动"),
            "logs" => ("服务日志", "server-out.log / server-error.log 尾部"),
            "settings" => ("环境设置", "CI 仓库路径 · 工具检测 · Web 控制台"),
            _ => ("总览", "实时监控 · 1 秒刷新")
        };
        PageTitle.Text = title;
        PageSub.Text = sub;

        // 切页淡入：150~200ms 级微过渡，页面"流"进来而不是跳变
        PageHost.Opacity = 0;
        Dispatcher.UIThread.Post(() => PageHost.Opacity = 1, DispatcherPriority.Background);

        // 切页时按需刷新，保持数据新鲜
        switch (tag)
        {
            case "welcome": _ = _welcome.RefreshAsync(); break;
            case "overview": _ = _overview.RefreshAsync(); break;
            case "secrets": _ = _secrets.ReloadAsync(); break;
            case "peers": _ = _peers.RefreshAsync(); break;
            case "logs": _ = _logs.RefreshAsync(); break;
            case "settings": _settings.LoadInto(); break;
        }
    }

    /// <summary>CI 仓库定位成功后由欢迎页调用：解锁导航并推进到下一步（由 UpdateHeader 内统一处理）。</summary>
    public void OnCiRootLocated() => UpdateHeader();

    /// <summary>按导航 tag 切页（供子页面跳转，例如欢迎页 → 安装更新）。</summary>
    public void GoTo(string tag)
    {
        foreach (var item in NavList.Items.OfType<ListBoxItem>())
        {
            if ((item.Tag as string) == tag && item.IsEnabled)
            {
                NavList.SelectedItem = item;
                return;
            }
        }
    }

    // ---------------- 自绘标题栏 ----------------
    // 客户区扩展后（ExtendClientAreaToDecorationsHint），原生标题栏不再出现：
    // 命令栏空白处承担拖动/双击最大化，右上自绘最小化/最大化/关闭。

    private void OnCaptionPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
    }

    private void OnCaptionDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void OnMinimize(object? sender, TappedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeToggle(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void OnClose(object? sender, TappedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnResizeGripPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginResizeDrag(WindowEdge.SouthEast, e);
    }

    // ---------------- 托盘常驻 ----------------
    // 关闭窗口 = 隐藏进系统托盘后台运行（监控定时器继续工作）；
    // 真正退出走托盘菜单「退出」。这要求 ShutdownMode.OnExplicitShutdown（App.axaml.cs）。

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_forceClose || e.CloseReason == WindowCloseReason.ApplicationShutdown) return;

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        EnsureTrayIcon();
        Hide();
    }

    private void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void RequestExit()
    {
        _forceClose = true;
        if (_tray != null) _tray.IsVisible = false;
        Close();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }

    private void EnsureTrayIcon()
    {
        if (_tray != null) return;

        var open = new NativeMenuItem("打开主窗口");
        open.Click += (_, _) => ShowFromTray();
        var exit = new NativeMenuItem("退出 ClassIntraOps");
        exit.Click += (_, _) => RequestExit();

        var menu = new NativeMenu();
        menu.Add(open);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);

        _tray = new TrayIcon
        {
            ToolTipText = "ClassIntraOps · CI 运维控制台（后台运行中）",
            Icon = BuildTrayIcon(),
            Menu = menu,
            IsVisible = true
        };
        _tray.Clicked += (_, _) => ShowFromTray();
        TrayIcon.SetIcons(Application.Current!, new TrayIcons { _tray });
    }

    /// <summary>程序化绘制托盘图标：品牌渐变圆角方块 + CI 字样，免去随包分发 .ico。</summary>
    private static WindowIcon BuildTrayIcon()
    {
        var rtb = new RenderTargetBitmap(new PixelSize(32, 32), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var bg = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromRgb(0x63, 0x66, 0xF1), 0),
                    new GradientStop(Color.FromRgb(0xA8, 0x55, 0xF7), 1)
                }
            };
            ctx.DrawRectangle(bg, null, new RoundedRect(new Rect(2, 2, 28, 28), 8));

            var ft = new FormattedText(
                "CI", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 13, Brushes.White);
            ctx.DrawText(ft, new Point((32 - ft.Width) / 2, (32 - ft.Height) / 2 + 0.5));
        }
        return new WindowIcon(rtb);
    }

    public void UpdateHeader()
    {
        var root = AppState.ResolveCiRoot();
        var ok = root != null;
        // 语义色与设计令牌一致：lime 正常 / 红 缺配置
        CiStatusDot.Fill = new Avalonia.Media.SolidColorBrush(
            ok ? Avalonia.Media.Color.FromRgb(0xA3, 0xE6, 0x35) : Avalonia.Media.Color.FromRgb(0xF8, 0x71, 0x71));
        CiStatusText.Text = ok ? "CI 已定位" : "未定位到 CI 仓库";
        StatusCi.Text = "CI 仓库  " + (root ?? "未定位  ·  请先下载或指定 CI 仓库");

        // 主次关系随状态收敛：未定位时欢迎页是主路径，依赖 CI 的页面禁用
        var ready = ok;
        NavSecrets.IsEnabled = ready;
        NavPeers.IsEnabled = ready;
        NavLogs.IsEnabled = ready;
        NavWelcome.IsVisible = !ready;
        ToolTip.SetTip(NavSecrets, ready ? "密钥配置" : "需先定位 CI 仓库（安装更新 / 环境设置）");
        ToolTip.SetTip(NavPeers, ready ? "跨班对端" : "需先定位 CI 仓库（安装更新 / 环境设置）");
        ToolTip.SetTip(NavLogs, ready ? "服务日志" : "需先定位 CI 仓库（安装更新 / 环境设置）");

        if (!ready)
        {
            if (PageHost.Content is not WelcomePage) Select("welcome");
        }
        else if (PageHost.Content is WelcomePage)
        {
            // 定位完成即收敛主次：欢迎页退场，直接进入下一步（配置密钥）
            Select("secrets");
        }

        try
        {
            var ips = Dns.GetHostAddressesAsync(Dns.GetHostName()).Result;
            var v4 = ips.Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                        .Select(a => a.ToString()).ToList();
            StatusNet.Text = "本机地址  " + (v4.Count > 0 ? string.Join("、", v4) : "未知") +
                             "  ·  平台 " + (OperatingSystem.IsWindows() ? "Windows" :
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
