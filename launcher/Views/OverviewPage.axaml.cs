using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ClassIntraOps.Launcher.Services;

namespace ClassIntraOps.Launcher.Views;

public partial class OverviewPage : UserControl
{
    private const double BarMaxHeight = 86;
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(30);

    private DispatcherTimer? _timer;
    private DateTime _lastSample = DateTime.MinValue;
    private DateTime _firstSeen = DateTime.Now;
    private SystemInfo? _lastSys;

    public OverviewPage()
    {
        InitializeComponent();
        BtnStartAll.Click += async (_, _) => await StartAllAsync();
        BtnApplyRestart.Click += async (_, _) => await ApplyRestartAsync();

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
    }

    public void StopTimer() => _timer?.Stop();

    public async Task RefreshAsync()
    {
        var ciRoot = AppState.ResolveCiRoot();

        // ---- 系统采样（每次刷新都采，达到间隔才写入历史窗口）----
        var sys = await SystemService.CollectAsync(ciRoot);
        _lastSys = sys;
        if (DateTime.Now - _lastSample >= SampleInterval)
        {
            _lastSample = DateTime.Now;
            HistoryService.Add(new ResourceSample(
                DateTime.Now,
                Math.Clamp(sys.CpuUsage, 0, 1),
                Math.Clamp(sys.MemUsage, 0, 1),
                sys.Disk is { } d && d.Total > 0 ? Math.Clamp((double)(d.Total - d.Free) / d.Total, 0, 1) : 0));
        }

        // ---- PM2 状态 ----
        var (ok, list, err) = await Pm2Service.JListAsync();
        var online = list.Count(p => p.Status == "online");

        // 大数字卡
        OnlineCount.Text = ok ? online.ToString() : "—";
        OnlineTotal.Text = $"/ {(ok ? list.Count.ToString() : "—")} 进程在线";

        var upText = Format.Duration(TimeSpan.FromSeconds(Environment.TickCount64 / 1000));
        var diskText = sys.Disk is { } dd && dd.Total > 0
            ? $"磁盘 {(double)(dd.Total - dd.Free) / dd.Total * 100:0}%"
            : "磁盘 —";
        StatusLine.Inlines?.Clear();
        StatusLine.Text = ok && list.Count > 0
            ? $"控制台已运行 {upText} · {diskText} · 主机 {sys.Hostname}"
            : $"PM2 不可用: {err}";

        // LIVE 胶囊
        var allOnline = ok && list.Count > 0 && online == list.Count;
        LivePill.Background = allOnline ? Palette.LimeBg : Palette.WarnBg;
        LiveText.Text = allOnline ? "LIVE" : "DEGRADED";
        LiveText.Foreground = allOnline ? Palette.Lime : Palette.WarnFg;
        LiveDot.Fill = allOnline ? Palette.Lime : Palette.WarnFg;

        // 柱状图：固定 24 槽，新样本从右侧推入，不足补空槽（横向铺满，滚动窗口感）
        CpuNow.Text = $"{Math.Clamp(sys.CpuUsage, 0, 1) * 100:0}%";
        MemNow.Text = $"{Math.Clamp(sys.MemUsage, 0, 1) * 100:0}%";
        DiskNow.Text = sys.Disk is { } dx && dx.Total > 0
            ? $"{(double)(dx.Total - dx.Free) / dx.Total * 100:0}%"
            : "—";
        const int slots = 24;
        var samples = HistoryService.Snapshot();
        var bars = new List<BarGroup>(slots);
        for (var i = 0; i < slots - samples.Count; i++)
            bars.Add(new BarGroup("", 0, 0, 0));
        bars.AddRange(samples.Select(s => new BarGroup(
            s.Time.ToString("HH:mm"),
            Math.Max(3, s.Cpu * BarMaxHeight),
            Math.Max(3, s.Mem * BarMaxHeight),
            Math.Max(3, s.Disk * BarMaxHeight))));
        BarChart.ItemsSource = bars;
        ChartFrom.Text = samples.Count > 0 ? samples[0].Time.ToString("HH:mm") : "";

        // 进程列表
        if (!ok)
        {
            ProcList.ItemsSource = null;
            ProcBanner.IsVisible = true;
            ProcBanner.Text = "PM2 不可用: " + err;
            return;
        }
        ProcBanner.IsVisible = list.Count == 0;
        ProcBanner.Text = list.Count == 0 ? "当前没有 PM2 进程。" : "";
        ProcCountText.Text = ok ? list.Count.ToString() : "0";
        ProcList.ItemsSource = list;

        if (VisualRoot is MainWindow main)
        {
            main.StatusRefresh.Text = "更新于 " + DateTime.Now.ToString("HH:mm:ss");
        }
    }

    private async void OnAction(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Pm2Process proc } btn) return;
        var action = btn.Tag as string ?? "";

        var label = action switch
        {
            "restart" => "重启",
            "stop" => "停止",
            "delete" => "删除并移出 PM2",
            _ => action
        };

        if (action != "restart")
        {
            var ok = await ConfirmDialog.ShowAsync((Window)VisualRoot!,
                label + "进程",
                $"确定要{label} {proc.Name} 吗？" + (action == "delete" ? "\n删除后需 pm2 save 才持久化。" : ""),
                label);
            if (!ok) return;
        }

        var r = await Pm2Service.ActionAsync(proc.Name, action);
        if (!r.Ok) await ShowErrorAsync(label + "失败", r.Output);
        await RefreshAsync();
    }

    private async Task StartAllAsync()
    {
        var ciRoot = AppState.ResolveCiRoot();
        if (ciRoot == null)
        {
            await ShowErrorAsync("启动失败", "未定位到 CI 仓库，请到「环境设置」指定路径。");
            return;
        }
        var r = await Pm2Service.StartAllAsync(ciRoot);
        if (!r.Ok) await ShowErrorAsync("启动失败", r.Output);
        await RefreshAsync();
    }

    private async Task ApplyRestartAsync()
    {
        var ciRoot = AppState.ResolveCiRoot();
        if (ciRoot == null)
        {
            await ShowErrorAsync("重启失败", "未定位到 CI 仓库。");
            return;
        }

        var ok = await ConfirmDialog.ShowAsync((Window)VisualRoot!,
            "应用配置重启",
            "将执行 pm2 restart ecosystem.config.js --update-env，服务会短暂中断（数秒）。继续？",
            "重启");
        if (!ok) return;

        var r = await Pm2Service.RestartWithEnvAsync(ciRoot);
        if (!r.Ok) await ShowErrorAsync("重启失败", r.Output);
        await RefreshAsync();
    }

    private async Task ShowErrorAsync(string title, string detail)
    {
        var dlg = new Window
        {
            Title = title,
            SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight,
            CanResize = false,
            WindowStartupLocation = Avalonia.Controls.WindowStartupLocation.CenterOwner,
            MaxWidth = 620,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = detail, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12.5 },
                    new Button { Content = "关闭", Padding = new Avalonia.Thickness(16, 5) }
                }
            }
        };
        ((Button)((StackPanel)dlg.Content).Children[^1]).Click += (_, _) => dlg.Close();
        await dlg.ShowDialog((Window)VisualRoot!);
    }
}
