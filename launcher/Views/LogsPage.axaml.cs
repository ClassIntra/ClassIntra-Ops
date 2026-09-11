using Avalonia.Controls;
using ClassIntraOps.Launcher.Services;

namespace ClassIntraOps.Launcher.Views;

public partial class LogsPage : UserControl
{
    public LogsPage()
    {
        InitializeComponent();
        BtnRead.Click += async (_, _) => await RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        var ciRoot = AppState.ResolveCiRoot();
        if (ciRoot == null)
        {
            LogBox.Text = "未定位到 CI 仓库，请到「环境设置」指定路径。";
            return;
        }

        var which = (FileBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "out";
        var file = which == "error" ? "server-error.log" : "server-out.log";
        var path = Path.Combine(ciRoot, "logs", file);

        try
        {
            var text = await File.ReadAllTextAsync(path);
            var lines = text.Split('\n');
            LogBox.Text = string.Join('\n', lines[^Math.Min(300, lines.Length)..]);
            LogMeta.Text = $"{path} · 共 {lines.Length} 行，显示尾部 {Math.Min(300, lines.Length)} 行";
        }
        catch (Exception e)
        {
            LogBox.Text = "日志不可读: " + e.Message;
            LogMeta.Text = path;
        }
    }
}
