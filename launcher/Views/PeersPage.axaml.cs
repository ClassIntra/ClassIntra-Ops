using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIntraOps.Launcher.Services;

namespace ClassIntraOps.Launcher.Views;

public partial class PeersPage : UserControl
{
    public PeersPage()
    {
        InitializeComponent();
        BtnRefresh.Click += async (_, _) => await RefreshAsync();
        BtnProbe.Click += async (_, _) => await ProbeOneAsync();
    }

    public async Task RefreshAsync()
    {
        var ciRoot = AppState.ResolveCiRoot();
        PeerHost.Children.Clear();

        if (ciRoot == null)
        {
            SummaryText.Text = "未定位到 CI 仓库";
            return;
        }

        var (serverId, peers, hasSecret) = PeerService.ReadRelayConfig(ciRoot);
        if (peers.Count == 0)
        {
            SummaryText.Text = "RELAY_SERVERS 为空，当前是单机模式";
            DetailText.Text = "本机 ID: " + (serverId.Length > 0 ? serverId : "未设置") +
                              (hasSecret ? " · 已配置 RELAY_SECRET" : " · 未配置 RELAY_SECRET");
            return;
        }

        SummaryText.Text = "本机 ID: " + (serverId.Length > 0 ? serverId : "未设置") + " · 探活中…";
        DetailText.Text = peers.Count + " 个对端" + (hasSecret ? " · 已配置 RELAY_SECRET" : " · 未配置 RELAY_SECRET");

        var results = await PeerService.ProbeAllAsync(peers);
        var online = results.Count(p => p.Ok);

        SummaryText.Text = $"本机 ID: {(serverId.Length > 0 ? serverId : "未设置")} · " +
                           $"{results.Count} 个对端，在线 {online} 个";
        foreach (var p in results) PeerHost.Children.Add(BuildRow(p));
    }

    /// <summary>对端行：状态点 + 标识徽标 + 名称 / 原始地址 / TCP 延迟（含微型柱）/ 状态胶囊 / 行内探活</summary>
    private Control BuildRow(PeerStatus p)
    {
        var host = HostOf(p.Url);
        var glyph = host.Length > 0 ? char.ToUpperInvariant(host[0]).ToString() : "?";
        var chipBg = p.Ok ? Palette.LimeBg : Palette.ErrBg;
        var chipFg = p.Ok ? Palette.Lime : Palette.ErrFg;
        var neutral = new SolidColorBrush(Color.FromRgb(0x3A, 0x44, 0x54));

        var row = new Border { Classes = { "row" } };
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,200,130,90,Auto") };

        // ---- 对端列 ----
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        left.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = chipFg, VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new Border
        {
            Classes = { "iconbadge" },
            Background = chipBg,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = chipFg,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        });
        var nameCol = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        nameCol.Children.Add(new TextBlock
        {
            Text = host,
            FontSize = 12.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Palette.TextPrimary
        });
        nameCol.Children.Add(new TextBlock
        {
            Text = p.Ok
                ? (p.StatusCode is { } sc ? "HTTP " + sc + "（补充信息）" : "TCP 可达")
                : "不可达 · " + p.Error,
            FontSize = 10.5,
            Foreground = Palette.TextTertiary
        });
        left.Children.Add(nameCol);
        Grid.SetColumn(left, 0);
        grid.Children.Add(left);

        // ---- 地址列 ----
        var url = new TextBlock
        {
            Text = p.Url,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 11,
            Foreground = Palette.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(url, 1);
        grid.Children.Add(url);

        // ---- 延迟列：数值 + 微型柱（在线时按延迟波动，超时显示平灰柱） ----
        var latency = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center
        };
        latency.Children.Add(new TextBlock
        {
            Text = p.Ok ? p.Ms + " ms" : "超时",
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Foreground = p.Ok ? Palette.Lime : Palette.ErrFg,
            VerticalAlignment = VerticalAlignment.Center
        });
        var bars = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        for (var i = 0; i < 5; i++)
        {
            var h = p.Ok ? Math.Clamp(Math.Clamp(p.Ms, 4, 16) + (i - 2) * 1.5, 4, 16) : 4;
            bars.Children.Add(new Rectangle
            {
                Width = 3,
                Height = h,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = p.Ok ? Palette.Lime : neutral
            });
        }
        latency.Children.Add(bars);
        Grid.SetColumn(latency, 2);
        grid.Children.Add(latency);

        // ---- 状态列 ----
        var pill = new Border
        {
            Classes = { "pill" },
            Background = chipBg,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = p.Ok ? "online" : "offline",
                FontSize = 10,
                FontWeight = FontWeight.Medium,
                FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
                Foreground = chipFg
            }
        };
        Grid.SetColumn(pill, 3);
        grid.Children.Add(pill);

        // ---- 操作列：行内探活 ----
        var act = new Button
        {
            Content = "探活",
            Classes = { "ghost" },
            Tag = p.Url,
            Width = 56,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        act.Click += OnRowProbe;
        Grid.SetColumn(act, 4);
        grid.Children.Add(act);

        row.Child = grid;
        return row;
    }

    private static string HostOf(string url)
    {
        try
        {
            var t = url.Trim();
            if (t.StartsWith("ws://")) t = "http://" + t[5..];
            else if (t.StartsWith("wss://")) t = "https://" + t[6..];
            else if (!t.StartsWith("http")) t = "http://" + t;
            var u = new Uri(t);
            return u.Host + ":" + (u.IsDefaultPort ? 80 : u.Port);
        }
        catch { return url; }
    }

    private async void OnRowProbe(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url }) await ProbeOneAsync(url);
    }

    private async Task ProbeOneAsync(string? target = null)
    {
        var v = (target ?? ProbeInput.Text)?.Trim();
        if (string.IsNullOrEmpty(v)) return;

        ProbeOut.Text = "探测中…";
        var p = await PeerService.ProbeAsync(v);

        // 终端式输出：时间戳 + 四行结论
        ProbeOut.Text =
            $"[{DateTime.Now:HH:mm:ss}] {p.Url}\n" +
            $"  判定: {(p.Ok ? "在线" : "不可达 · " + p.Error)}\n" +
            $"  延迟: {p.Ms} ms\n" +
            (p.StatusCode is { } sc
                ? $"  HTTP: {sc}（补充信息，不影响判定）"
                : "  HTTP: 未取得（relay 端点常不响应普通 GET）");
    }
}
