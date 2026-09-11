using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ClassIntraOps.Launcher.Services;

namespace ClassIntraOps.Launcher.Views;

public partial class SecretsPage : UserControl
{
    private EnvSchema? _schema;
    private readonly List<(EnvField Field, TextBox Box)> _fields = new();
    private bool _showPlain;

    public SecretsPage()
    {
        InitializeComponent();
        _ = ReloadAsync();
    }

    public async Task ReloadAsync()
    {
        var ciRoot = AppState.ResolveCiRoot();
        GroupsHost.Children.Clear();
        _fields.Clear();

        if (ciRoot == null)
        {
            GroupsHost.Children.Add(Note("未定位到 CI 仓库，请到「环境设置」指定路径。"));
            return;
        }

        try
        {
            _schema = EnvService.ReadSchema(ciRoot);
        }
        catch (Exception e)
        {
            GroupsHost.Children.Add(Note("读取失败: " + e.Message));
            return;
        }

        if (!_schema.HasEnv)
        {
            GroupsHost.Children.Add(Note("server/.env 不存在。保存后会创建它（以 .env.example 的默认值为占位提示）。"));
        }

        foreach (var group in _schema.Groups) GroupsHost.Children.Add(BuildGroup(group));

        if (_schema.ExtraKeys.Count > 0)
        {
            var extra = new EnvGroup
            {
                Title = "仅出现在 ecosystem.config.js 的键",
                Description = "这些键在 .env.example 里没有，通常由部署配置注入，一般不需要手工填。"
            };
            extra.Fields.AddRange(_schema.ExtraKeys);
            GroupsHost.Children.Add(BuildGroup(extra));
        }
        await Task.CompletedTask;
    }

    private Control BuildGroup(EnvGroup group)
    {
        var card = new Border { Classes = { "card" } };
        var stack = new StackPanel { Spacing = 10 };

        stack.Children.Add(new TextBlock { Text = group.Title, FontWeight = FontWeight.SemiBold, FontSize = 14 });
        if (group.Description.Length > 0)
            stack.Children.Add(new TextBlock { Text = group.Description, FontSize = 12, Opacity = 0.62, TextWrapping = TextWrapping.Wrap });

        // 两列网格排列字段，密钥多时更紧凑（Avalonia Grid 无 ColumnSpacing，用中缝列代替）
        var grid = new Grid { ColumnDefinitions = ColumnDefinitions.Parse("*,18,*") };
        var left = new StackPanel { Spacing = 10 };
        var right = new StackPanel { Spacing = 10 };
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(right);

        for (var i = 0; i < group.Fields.Count; i++)
        {
            var host = i % 2 == 0 ? left : right;
            host.Children.Add(BuildField(group.Fields[i]));
        }

        stack.Children.Add(grid);
        card.Child = stack;
        return card;
    }

    private Control BuildField(EnvField field)
    {
        var hasValue = _schema!.Current.TryGetValue(field.Key, out var cur) && cur.Length > 0;
        var current = _schema.Current.GetValueOrDefault(field.Key, "");

        var box = new TextBox
        {
            PlaceholderText = field.Secret
                ? (hasValue ? "已设置 " + _schema.Masked.GetValueOrDefault(field.Key, "") + "（留空不修改）" : "未设置")
                : (field.DefaultValue.Length > 0 ? field.DefaultValue : ""),
            Text = field.Secret ? "" : current,
            FontSize = 12.5
        };
        if (field.Secret && !_showPlain) box.PasswordChar = '•';

        _fields.Add((field, box));

        var stack = new StackPanel { Spacing = 3 };

        // 标签行：键名 + 密钥标记
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        labelRow.Children.Add(new TextBlock
        {
            Text = field.Key,
            FontFamily = new FontFamily("Consolas, DejaVu Sans Mono, monospace"),
            FontSize = 11.5,
            Opacity = 0.72
        });
        if (field.Secret)
        {
            labelRow.Children.Add(new Border
            {
                Classes = { "pill" },
                Background = Palette.AccentBg,
                Child = new TextBlock { Text = "密钥", FontSize = 10.5, Foreground = Palette.AccentFg }
            });
        }
        stack.Children.Add(labelRow);
        stack.Children.Add(box);

        if (field.Hint.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = field.Hint,
                FontSize = 11,
                Opacity = 0.55,
                TextWrapping = TextWrapping.Wrap
            });

        return stack;
    }

    private static Control Note(string text) =>
        new TextBlock { Text = text, FontSize = 12.5, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };

    private void OnToggle(object? sender, RoutedEventArgs e)
    {
        _showPlain = !_showPlain;
        BtnToggle.Content = _showPlain ? "隐藏明文" : "显示全部明文";
        foreach (var (_, box) in _fields)
        {
            box.PasswordChar = _showPlain ? '\0' : '•';
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs e)
    {
        var ciRoot = AppState.ResolveCiRoot();
        if (ciRoot == null || _schema == null)
        {
            ShowBanner("未定位到 CI 仓库，无法保存。", false);
            return;
        }

        var updates = new Dictionary<string, string>();
        foreach (var (field, box) in _fields)
        {
            var v = box.Text ?? "";
            if (field.Secret && v.Length == 0) continue;   // 密钥留空 = 不修改
            if (!field.Secret && _schema.Current.TryGetValue(field.Key, out var cur) && cur == v) continue; // 无变化
            updates[field.Key] = v;
        }

        if (updates.Count == 0)
        {
            ShowBanner("没有需要保存的改动。", true);
            return;
        }

        var ok = await ConfirmDialog.ShowAsync(
            (Window)VisualRoot!,
            "保存环境变量",
            $"将写入 {updates.Count} 项到 {Path.Combine(ciRoot, "server", ".env")}\n\n" +
            string.Join("\n", updates.Keys.Select(k => "· " + k)) +
            "\n\n原文件会自动备份。保存后需「应用配置重启」才生效。",
            "写入");
        if (!ok) return;

        try
        {
            var (ok2, path, updated, appended, backup) = EnvService.WriteValues(ciRoot, updates);
            ShowBanner(
                $"已写入 {updated.Count} 项（新增 {appended.Count} 项）到 {path}" +
                (backup != null ? $"，备份: {Path.GetFileName(backup)}。" : "。") +
                " 改动要生效需执行「应用配置重启」——普通 restart 不会刷新环境变量。",
                true);
        }
        catch (Exception ex)
        {
            ShowBanner("保存失败: " + ex.Message, false);
        }
    }

    private async void OnApplyRestart(object? sender, RoutedEventArgs e)
    {
        var ciRoot = AppState.ResolveCiRoot();
        if (ciRoot == null) return;

        var ok = await ConfirmDialog.ShowAsync((Window)VisualRoot!,
            "应用配置重启",
            "将执行 pm2 restart ecosystem.config.js --update-env，服务会短暂中断（数秒）。继续？",
            "重启");
        if (!ok) return;

        var r = await Pm2Service.RestartWithEnvAsync(ciRoot);
        ShowBanner(r.Ok ? "已重启，新配置生效。" : "重启失败: " + r.Output, r.Ok);
    }

    private void ShowBanner(string text, bool success)
    {
        SaveBannerBorder.IsVisible = true;
        // 语义色：成功=Success，失败=Critical，都走 Fluent 资源
        SaveBannerBorder.Background = success ? Palette.OkBg : Palette.ErrBg;
        SaveBannerBorder.BorderBrush = success ? Palette.OkFg : Palette.ErrFg;
        SaveBannerText.Text = text;
    }
}
