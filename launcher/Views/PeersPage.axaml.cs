using Avalonia.Controls;
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

    private Control BuildRow(PeerStatus p)
    {
        var row = new Border { Classes = { "row" } };
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("1.6*,Auto,0.6*,Auto") };

        grid.Children.Add(new TextBlock
        {
            Text = p.Url,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center
        });

        var pill = new Border
        {
            Classes = { "pill" },
            Background = p.Ok ? Palette.OkBg : Palette.ErrBg,
            Child = new TextBlock
            {
                Text = p.Ok ? (p.StatusCode is { } sc ? "在线 · HTTP " + sc : "在线 · TCP") : "不可达 · " + p.Error,
                FontSize = 11,
                Foreground = p.Ok ? Palette.OkFg : Palette.ErrFg
            }
        };
        Grid.SetColumn(pill, 1);
        grid.Children.Add(pill);

        var addr = new TextBlock
        {
            Text = HostOf(p.Url) + (p.Ms > 0 ? $" · {p.Ms} ms" : ""),
            FontSize = 12,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(addr, 2);
        grid.Children.Add(addr);

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
        catch { return ""; }
    }

    private async Task ProbeOneAsync()
    {
        var v = ProbeInput.Text?.Trim();
        if (string.IsNullOrEmpty(v)) return;
        ProbeOut.Text = "探测中…";
        var p = await PeerService.ProbeAsync(v);
        ProbeOut.Text = $"地址: {p.Url}\n" +
                        $"判定: {(p.Ok ? "在线" : "不可达 · " + p.Error)}\n" +
                        $"延迟: {p.Ms} ms\n" +
                        (p.StatusCode is { } sc ? $"HTTP: {sc}（补充信息，不影响判定）" : "HTTP: 未取得（relay 端点常不响应普通 GET）");
    }
}
