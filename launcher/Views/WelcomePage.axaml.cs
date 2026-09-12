using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ClassIntraOps.Launcher.Services;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;

namespace ClassIntraOps.Launcher.Views;

/// <summary>
/// 欢迎页：首启引导与门禁出口。
/// 主次关系由 MainWindow 统一裁决——CI 仓库未定位时，依赖仓库的页面在导航上禁用，
/// 任何试图进入它们的操作都会被送回这里，并说明「为什么进不去、下一步做什么」。
/// </summary>
public partial class WelcomePage : UserControl
{
    public WelcomePage()
    {
        InitializeComponent();
        BtnInstall.Click += (_, _) => (VisualRoot as MainWindow)?.GoTo("install");
        BtnSettings.Click += (_, _) => (VisualRoot as MainWindow)?.GoTo("settings");
        BtnPickFolder.Click += async (_, _) => await PickFolderAsync();
        BtnReDetect.Click += async (_, _) => await DetectToolsAsync();
    }

    public async Task RefreshAsync()
    {
        var root = AppState.ResolveCiRoot();
        var ready = root != null;

        // ---- Hero 状态 ----
        HeroDot.Fill = new SolidColorBrush(ready
            ? Color.FromRgb(0xA3, 0xE6, 0x35)
            : Color.FromRgb(0xFB, 0xBF, 0x24));
        HeroStatusText.Text = ready ? "CI 仓库已就位" : "尚未定位 CI 仓库";

        // ---- 步骤状态 ----
        var envPath = ready ? Path.Combine(root!, "server", ".env") : null;
        var envExists = envPath != null && File.Exists(envPath);

        var online = 0;
        var total = 0;
        if (ready)
        {
            try
            {
                var (ok, list, _) = await Pm2Service.JListAsync();
                if (ok)
                {
                    total = list.Count;
                    online = list.Count(p => p.Status == "online");
                }
            }
            catch { /* PM2 不可用时步骤 03 保持待办 */ }
        }

        SetStep(Step1Pill, Step1Chip, Step1Detail,
            ready ? "完成" : "进行中",
            ready ? root! : "等待定位：下载安装或指向已有仓库目录",
            ready);

        SetStep(Step2Pill, Step2Chip, Step2Detail,
            envExists ? "完成" : ready ? "进行中" : "待办",
            envExists ? "server/.env 已存在"
                      : envPath != null ? "server/.env 尚未创建，保存一次即生成"
                      : "完成步骤 01 后解锁",
            envExists);

        SetStep(Step3Pill, Step3Chip, Step3Detail,
            online > 0 ? "完成" : "待办",
            online > 0 ? $"PM2 在线 {online} / {total} 个进程"
                       : ready ? "尚未启动：到总览执行「启动全部」或安装时勾选启动"
                       : "完成步骤 01 后解锁",
            online > 0);

        // ---- 当前定位 ----
        RootPathText.Text = root ?? "未定位";
        RootCheckText.Text = ready
            ? "目录校验通过：存在 ecosystem.config.js 或 server/"
            : "尚未指定目录。控制台不知道 CI 在哪，因此配置类页面保持锁定。";
        RepoText.Text = $"{AppState.Repo} @ {AppState.Ref}";

        await DetectToolsAsync();
    }

    private static void SetStep(Border pill, TextBlock chip, TextBlock detail,
        string text, string detailText, bool done)
    {
        chip.Text = text;
        detail.Text = detailText;
        pill.Background = text switch
        {
            "完成" => Palette.OkBg,
            "进行中" => Palette.WarnBg,
            _ => Palette.NeutralBg
        };
        chip.Foreground = text switch
        {
            "完成" => Palette.OkFg,
            "进行中" => Palette.WarnFg,
            _ => Palette.NeutralFg
        };
        detail.Foreground = done ? Palette.TextTertiary : Palette.TextSecondary;
    }

    /// <summary>目录选择器：直接指向已有 CI 仓库（比手打路径可靠）。</summary>
    private async Task PickFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top == null) return;

        var picked = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 ClassIntra (CI) 仓库目录",
            AllowMultiple = false
        });
        if (picked.Count == 0) return;

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrEmpty(path) || !AppState.LooksLikeCiRoot(path))
        {
            HeroDot.Fill = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            HeroStatusText.Text = "该目录不是 CI 仓库";
            RootPathText.Text = path ?? "—";
            RootCheckText.Text = "目录下未找到 ecosystem.config.js 或 server/。请选择 CI 仓库根目录，或用「下载并安装 CI」。";
            return;
        }

        AppState.CiRoot = Path.GetFullPath(path);
        AppState.Save();

        // 主窗口据此解锁导航并推进到下一步（配置密钥）
        (VisualRoot as MainWindow)?.OnCiRootLocated();
    }

    private async Task DetectToolsAsync()
    {
        ToolsHost.Children.Clear();
        ToolsHost.Children.Add(new TextBlock { Text = "检测中…", FontSize = 12, Foreground = Palette.TextTertiary });

        var tools = await ToolService.DetectAsync();
        ToolsHost.Children.Clear();

        foreach (var (name, ok, version) in tools)
        {
            var row = new Border { Classes = { "row" } };
            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,0.5*,*") };

            var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, VerticalAlignment = VerticalAlignment.Center };
            left.Children.Add(new Ellipse
            {
                Width = 6,
                Height = 6,
                Fill = ok ? Palette.Lime : Palette.ErrFg,
                VerticalAlignment = VerticalAlignment.Center
            });
            left.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Foreground = Palette.TextPrimary,
                VerticalAlignment = VerticalAlignment.Center
            });
            grid.Children.Add(left);

            var pill = new Border
            {
                Classes = { "pill" },
                Background = ok ? Palette.OkBg : Palette.ErrBg,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = ok ? "可用" : "缺失",
                    FontSize = 10,
                    FontWeight = FontWeight.Medium,
                    Foreground = ok ? Palette.OkFg : Palette.ErrFg
                }
            };
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);

            var ver = new TextBlock
            {
                Text = string.IsNullOrEmpty(version) ? "—" : version,
                FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
                FontSize = 11.5,
                Foreground = Palette.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(ver, 2);
            grid.Children.Add(ver);

            row.Child = grid;
            ToolsHost.Children.Add(row);
        }
    }

    /// <summary>从被门禁拦下的页面跳回来时，说明原因与下一步。</summary>
    public void SetGateHint(string tag)
    {
        var name = tag switch
        {
            "secrets" => "密钥配置",
            "peers" => "跨班对端",
            "logs" => "服务日志",
            _ => "该页面"
        };
        var why = tag switch
        {
            "secrets" => "它要读写仓库里的 server/.env",
            "peers" => "它要从 .env 解析 RELAY_SERVERS",
            "logs" => "它要读仓库 logs/ 下的日志文件",
            _ => "它依赖 CI 仓库"
        };
        GateBannerText.Text = $"「{name}」还没解锁：{why}。先完成步骤 01 —— 下载安装，或指到已有仓库目录。";
        GateBanner.IsVisible = true;
    }

    public void ClearGateHint() => GateBanner.IsVisible = false;
}
