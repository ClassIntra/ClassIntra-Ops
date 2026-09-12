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
        // 刷新由定时器与多个 async void 入口触发，任何异常都不允许逃逸（兜底见 CrashLog）
        try
        {
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write("Overview.Refresh", ex);
            try { StatusLine.Text = "刷新失败: " + ex.Message; } catch { /* 页面可能已卸载 */ }
        }
    }

    private async Task RefreshCoreAsync()
    {
        var ciRoot = AppState.ResolveCiRoot();

        // CI 未定位时：依赖仓库的动作置灰并说明原因（避免点了才失败）
        var hasCi = ciRoot != null;
        BtnStartAll.IsEnabled = hasCi;
        BtnApplyRestart.IsEnabled = hasCi;
        ToolTip.SetTip(BtnStartAll, hasCi
            ? "启动 ecosystem.config.js 中的全部进程"
            : "需先定位 CI 仓库：欢迎页 / 安装更新 / 环境设置");
        ToolTip.SetTip(BtnApplyRestart, hasCi
            ? "pm2 restart ecosystem.config.js --update-env"
            : "需先定位 CI 仓库：欢迎页 / 安装更新 / 环境设置");

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

        var upText = Format.Duration(TimeSpan.FromSeconds(Environment.TickCount64 / 1000));
        var diskPct = sys.Disk is { } dsk && dsk.Total > 0
            ? (double)(dsk.Total - dsk.Free) / dsk.Total * 100
            : 0;
        var diskText = sys.Disk is { } dsk2 && dsk2.Total > 0 ? $"磁盘 {diskPct:0}%" : "磁盘 —";

        // ---- KPI 仪表行 ----
        var cpuPct = Math.Clamp(sys.CpuUsage, 0, 1) * 100;
        var memPct = Math.Clamp(sys.MemUsage, 0, 1) * 100;

        KpiProcValue.Text = ok ? online.ToString() : "—";
        KpiProcTotal.Text = $"/ {(ok ? list.Count.ToString() : "—")}";
        KpiProcChip.Text = !ok ? "PM2 不可用"
            : list.Count == 0 ? "无进程"
            : online == list.Count ? "全部在线"
            : $"{list.Count - online} 个异常";
        KpiProcMeter.Value = ok && list.Count > 0 ? online * 100.0 / list.Count : 0;
        KpiProcChip.Foreground = ok && list.Count > 0 && online == list.Count
            ? Palette.Lime : Palette.WarnFg;

        KpiCpuValue.Text = $"{cpuPct:0}%";
        KpiCpuMeter.Value = cpuPct;

        KpiMemValue.Text = $"{memPct:0}%";
        KpiMemChip.Text = sys.MemTotal > 0
            ? $"{Format.Bytes((long)(sys.MemTotal * memPct / 100))} / {Format.Bytes(sys.MemTotal)}"
            : "—";
        KpiMemMeter.Value = memPct;

        KpiDiskValue.Text = sys.Disk is { Total: > 0 } ? $"{diskPct:0}%" : "—";
        KpiDiskChip.Text = sys.Disk is { } dk3 && dk3.Total > 0 ? $"可用 {Format.Bytes(dk3.Free)}" : "—";
        KpiDiskMeter.Value = diskPct;

        // ---- 右栏：运行环境 + 服务摘要 ----
        EnvHost.Text = sys.Hostname.Length > 0 ? sys.Hostname : "—";
        EnvPlatform.Text = (OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux" : "macOS")
            + " · " + (Environment.Is64BitOperatingSystem ? "x64" : "x86");
        EnvUptime.Text = upText;
        EnvDisk.Text = sys.Disk is { } dk4 && dk4.Total > 0
            ? Format.Bytes(dk4.Total - dk4.Free) + " / " + Format.Bytes(dk4.Total)
            : "—";
        SvcOnline.Text = ok ? $"{online} / {list.Count}" : "—";
        SvcRestarts.Text = ok ? list.Sum(p => (long)p.Restarts) + " 次" : "—";
        SvcUpdated.Text = DateTime.Now.ToString("HH:mm:ss");
        ProcSummary.Text = ok && list.Count > 0
            ? $"合计 {Format.Bytes(list.Sum(p => p.Memory))} · CPU {list.Sum(p => p.Cpu ?? 0):0.#}% · 重启 {list.Sum(p => p.Restarts)} 次"
            : "";

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
        // async void 里的异常会直接杀进程，必须整体兜底
        try
        {
            await OnActionCore(sender, e);
        }
        catch (Exception ex)
        {
            Services.CrashLog.Write("Overview.Action", ex);
            try { await ShowErrorAsync("操作失败", ex.Message); } catch { /* 无宿主窗口时只能落盘 */ }
        }
    }

    private async Task OnActionCore(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: Pm2Process proc } btn) return;
        if (VisualRoot is not Window owner) return;   // 页面已卸载时放弃操作
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
            var ok = await ConfirmDialog.ShowAsync(owner,
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

        if (VisualRoot is not Window owner)
        {
            Services.CrashLog.Write("Overview.ApplyRestart", new InvalidOperationException("页面无宿主窗口"));
            return;
        }

        var ok = await ConfirmDialog.ShowAsync(owner,
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
        if (VisualRoot is not Window owner)
        {
            // 页面已卸载（切页/关窗）时弹不出模态窗，落盘即可
            Services.CrashLog.Write("Overview.ShowError", new InvalidOperationException(title + ": " + detail));
            return;
        }

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
        await dlg.ShowDialog(owner);
    }
}
