using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIntraOps.Launcher.Services;

namespace ClassIntraOps.Launcher.Views;

public partial class SettingsPage : UserControl
{
    private Process? _webProc;

    public SettingsPage()
    {
        InitializeComponent();
        BtnSaveRoot.Click += (_, _) => SaveRoot();
        BtnDetect.Click += async (_, _) => await DetectAsync();
        BtnStartWeb.Click += async (_, _) => await StartWebAsync();
        BtnOpenWeb.Click += (_, _) => OpenUrl("http://127.0.0.1:" + AppState.ConsolePort);
        LoadInto();
    }

    public void LoadInto()
    {
        RootBox.Text = AppState.ResolveCiRoot() ?? "";
        _ = DetectAsync();
    }

    private void SaveRoot()
    {
        var v = RootBox.Text?.Trim();
        if (string.IsNullOrEmpty(v) || !AppState.LooksLikeCiRoot(v))
        {
            WebStatus.Text = "该目录下没有 ecosystem.config.js 或 server/，不是 CI 仓库";
            return;
        }
        AppState.CiRoot = Path.GetFullPath(v);
        AppState.Save();
        WebStatus.Text = "已保存: " + AppState.CiRoot;
        if (VisualRoot is MainWindow main) main.UpdateHeader();
    }

    private async Task DetectAsync()
    {
        ToolsHost.Children.Clear();
        ToolsHost.Children.Add(new TextBlock { Text = "检测中…", FontSize = 12, Opacity = 0.6 });
        var tools = await ToolService.DetectAsync();
        ToolsHost.Children.Clear();
        foreach (var (name, ok, version) in tools)
        {
            var row = new Border { Classes = { "row" } };
            var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("Auto,0.5*,*") };

            grid.Children.Add(new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

            var pill = new Border
            {
                Classes = { "pill" },
                Background = ok ? Palette.OkBg : Palette.ErrBg,
                Child = new TextBlock
                {
                    Text = ok ? "可用" : "缺失",
                    FontSize = 11,
                    Foreground = ok ? Palette.OkFg : Palette.ErrFg
                }
            };
            Grid.SetColumn(pill, 1);
            grid.Children.Add(pill);

            var ver = new TextBlock
            {
                Text = version,
                FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
                FontSize = 12,
                Opacity = 0.8,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(ver, 2);
            grid.Children.Add(ver);

            row.Child = grid;
            ToolsHost.Children.Add(row);
        }
    }

    private async Task StartWebAsync()
    {
        if (await PortOpenAsync(AppState.ConsolePort))
        {
            WebStatus.Text = "Web 控制台已在运行: http://127.0.0.1:" + AppState.ConsolePort;
            return;
        }

        // 从 exe 向上找 ops-server/server.js
        var opsDir = FindOpsServerDir();
        if (opsDir == null)
        {
            WebStatus.Text = "未找到 ops-server/server.js（需与 launcher 同仓库部署）";
            return;
        }

        _webProc = Process.Start(new ProcessStartInfo
        {
            FileName = "node",
            Arguments = "server.js",
            WorkingDirectory = opsDir,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(500);
            if (await PortOpenAsync(AppState.ConsolePort))
            {
                WebStatus.Text = "Web 控制台已启动: http://127.0.0.1:" + AppState.ConsolePort;
                return;
            }
        }
        WebStatus.Text = "等待端口超时，请检查 node 是否可用";
    }

    private static string? FindOpsServerDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ops-server", "server.js")))
                return Path.Combine(dir.FullName, "ops-server");
            if (dir.Name == "ops-server" && File.Exists(Path.Combine(dir.FullName, "server.js")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    private static async Task<bool> PortOpenAsync(int port)
    {
        try
        {
            using var c = new Socket(SocketType.Stream, ProtocolType.Tcp);
            await c.ConnectAsync(IPAddress.Loopback, port, new CancellationTokenSource(1500).Token);
            return c.Connected;
        }
        catch { return false; }
    }

    private static void OpenUrl(string url)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                Process.Start("open", url);
            else if (OperatingSystem.IsLinux())
                Process.Start("xdg-open", url);
        }
        catch { /* 无桌面环境静默失败 */ }
    }
}
