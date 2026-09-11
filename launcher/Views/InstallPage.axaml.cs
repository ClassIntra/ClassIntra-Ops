using Avalonia.Controls;
using Avalonia.Threading;
using ClassIntraOps.Launcher.Services;

namespace ClassIntraOps.Launcher.Views;

public partial class InstallPage : UserControl
{
    private bool _running;

    public InstallPage()
    {
        InitializeComponent();
        RepoBox.Text = AppState.Repo;
        RefBox.Text = AppState.Ref;
        DirBox.Text = AppState.ResolveCiRoot() ?? "";
        BtnRun.Click += async (_, _) => await RunAsync();
    }

    private void Log(string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text += line + Environment.NewLine;
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });
    }

    private async Task RunAsync()
    {
        if (_running) return;
        var repo = RepoBox.Text?.Trim();
        var gitRef = RefBox.Text?.Trim();
        if (string.IsNullOrEmpty(repo) || string.IsNullOrEmpty(gitRef))
        {
            StatusText.Text = "仓库与分支不能为空";
            return;
        }

        AppState.Repo = repo;
        AppState.Ref = gitRef;
        AppState.Save();

        var target = DirBox.Text?.Trim();
        if (string.IsNullOrEmpty(target))
        {
            StatusText.Text = "目标目录不能为空";
            return;
        }

        _running = true;
        BtnRun.IsEnabled = false;
        LogBox.Text = "";
        StatusText.Text = "执行中，见下方日志…";

        try
        {
            var exists = Directory.Exists(Path.Combine(target, ".git"));
            var cwdForClone = exists ? target : Directory.GetParent(target)?.FullName ?? ".";

            Log(exists
                ? $"[1/3] 更新已有仓库到 {gitRef}（{target}）"
                : $"[1/3] 克隆 {repo} @ {gitRef} → {target}");
            var g = exists
                ? await ToolService.RunStreamedAsync(
                    $"git fetch --all --tags && git checkout {gitRef} && git pull --ff-only", target, Log, 300)
                : await ToolService.RunStreamedAsync(
                    $"git clone --branch {gitRef} \"{repo}\" \"{target}\"", cwdForClone, Log, 600);
            if (!g.Ok)
            {
                StatusText.Text = "拉取源码失败，已保留现场";
                Log("== 失败: " + g.Detail + " ==");
                return;
            }

            if (ChkInstall.IsChecked == true)
            {
                Log("[2/3] 安装依赖（pnpm install）");
                var i = await ToolService.RunStreamedAsync("pnpm install", target, Log, 1800);
                if (!i.Ok) Log("pnpm install 未成功，构建可能失败；若 better-sqlite3 编译失败，请安装 VS Build Tools 后重试。");
            }

            if (ChkBuild.IsChecked == true)
            {
                Log("[3/3] 构建前端（cd client && pnpm run build）");
                var b = await ToolService.RunStreamedAsync("cd client && pnpm run build", target, Log, 900);
                if (!b.Ok)
                {
                    StatusText.Text = "构建失败，已保留现场";
                    Log("== 失败: " + b.Detail + " ==");
                    return;
                }
            }

            Log("== 完成 ==");
            StatusText.Text = "完成。";

            if (ChkStart.IsChecked == true) await StartServerAsync();
        }
        finally
        {
            _running = false;
            BtnRun.IsEnabled = true;
        }
    }

    /// <summary>启动 CI 服务本体（pm2 start ecosystem.config.js && pm2 save）。</summary>
    private async Task StartServerAsync()
    {
        var ciRoot = AppState.ResolveCiRoot() ?? DirBox.Text?.Trim();
        if (string.IsNullOrEmpty(ciRoot) || !AppState.LooksLikeCiRoot(ciRoot))
        {
            StatusText.Text = "未定位到 CI 仓库";
            return;
        }

        Log("[服务] pm2 start ecosystem.config.js && pm2 save");
        var r = await ToolService.RunStreamedAsync("pm2 start ecosystem.config.js && pm2 save", ciRoot, Log, 300);
        StatusText.Text = r.Ok ? "服务已启动" : "启动失败: " + r.Detail;
    }
}
