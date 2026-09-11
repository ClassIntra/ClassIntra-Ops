using Avalonia;
using Avalonia.Media;

namespace ClassIntraOps.Launcher.Services;

public static class Format
{
    public static string Bytes(long? n)
    {
        if (n is null or < 0) return "—";
        double v = n.Value;
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{v:0} {units[i]}" : $"{v:0.#} {units[i]}";
    }

    public static string Duration(TimeSpan? t)
    {
        if (t is null) return "—";
        var s = (long)t.Value.TotalSeconds;
        if (s < 0) return "—";
        var d = s / 86400; s %= 86400;
        var h = s / 3600; s %= 3600;
        var m = s / 60;
        if (d > 0) return $"{d}天{h}小时";
        if (h > 0) return $"{h}小时{m}分";
        if (m > 0) return $"{m}分{s % 60}秒";
        return $"{s}秒";
    }
}

/// <summary>
/// code-behind 彩色控件统一取色入口：优先解析 App.axaml 资源（产品级深色配色），
/// 再回退 Fluent 语义资源，最后用内置色——查不到不崩。
/// </summary>
public static class Palette
{
    private static IBrush Res(params (string Key, Color Fallback)[] candidates)
    {
        foreach (var (key, fallback) in candidates)
        {
            try
            {
                if (Application.Current != null &&
                    Application.Current.Resources.TryGetResource(key, null, out var value) &&
                    value is IBrush brush)
                {
                    return brush;
                }
            }
            catch { /* 资源系统未就绪 */ }
        }
        return new SolidColorBrush(candidates[^1].Fallback);
    }

    public static readonly IBrush TextPrimary = Res(("AppTextPrimaryBrush", Color.FromRgb(0xF3, 0xF4, 0xF6)),
        ("TextFillColorPrimaryBrush", Color.FromRgb(0xF3, 0xF4, 0xF6)));
    public static readonly IBrush TextSecondary = Res(("AppTextSecondaryBrush", Color.FromRgb(0x9C, 0xA3, 0xAF)),
        ("TextFillColorSecondaryBrush", Color.FromRgb(0x9C, 0xA3, 0xAF)));
    public static readonly IBrush TextTertiary = Res(("AppTextTertiaryBrush", Color.FromRgb(0x6B, 0x72, 0x80)),
        ("TextFillColorTertiaryBrush", Color.FromRgb(0x6B, 0x72, 0x80)));

    public static readonly IBrush CardBg = Res(("AppCardBrush", Color.FromRgb(0x14, 0x16, 0x1B)));
    public static readonly IBrush CardBorder = Res(("AppCardBorderBrush", Color.FromRgb(0x22, 0x25, 0x2D)));
    public static readonly IBrush Divider = Res(("AppDividerBrush", Color.FromRgb(0x1E, 0x21, 0x29)));

    // lime 点缀（截图 chips 风）
    public static readonly IBrush LimeBg = Res(("LimeBgBrush", Color.FromArgb(0x26, 0xA3, 0xE6, 0x35)));
    public static readonly IBrush Lime = Res(("LimeBrush", Color.FromRgb(0xA3, 0xE6, 0x35)));

    public static readonly IBrush OkBg = Res(("LimeBgBrush", Color.FromArgb(0x26, 0xA3, 0xE6, 0x35)),
        ("SystemFillColorSuccessBackgroundBrush", Color.FromRgb(0x0f, 0x6e, 0x56)));
    public static readonly IBrush OkFg = Res(("LimeBrush", Color.FromRgb(0xA3, 0xE6, 0x35)),
        ("SystemFillColorSuccessBrush", Color.FromRgb(0x34, 0xd3, 0x99)));

    public static readonly IBrush ErrBg = Res(("SystemFillColorCriticalBackgroundBrush", Color.FromRgb(0x79, 0x1f, 0x1f)));
    public static readonly IBrush ErrFg = Res(("SystemFillColorCriticalBrush", Color.FromRgb(0xf8, 0x71, 0x71)));
    public static readonly IBrush WarnBg = Res(("SystemFillColorCautionBackgroundBrush", Color.FromRgb(0x63, 0x38, 0x06)));
    public static readonly IBrush WarnFg = Res(("SystemFillColorCautionBrush", Color.FromRgb(0xfb, 0xbf, 0x24)));
    public static readonly IBrush NeutralBg = Res(("SystemFillColorNeutralBackgroundBrush", Color.FromRgb(0x2a, 0x2e, 0x37)));
    public static readonly IBrush NeutralFg = Res(("SystemFillColorNeutralBrush", Color.FromRgb(0x9a, 0xa2, 0xb0)));

    public static readonly IBrush Accent = Res(("AccentFillColorDefaultBrush", Color.FromRgb(0x7b, 0xa4, 0xf3)));
    public static IBrush AccentFg => Res(("TextOnAccentFillColorPrimaryBrush", Colors.White));
    public static IBrush AccentBg => Accent;

    // 多彩图标系（App.axaml 同名资源；Transactions 式列表的圆底用）
    public static readonly IBrush IconPurple = Res(("IconPurpleBrush", Color.FromRgb(0xa7, 0x8b, 0xfa)));
    public static readonly IBrush IconBlue = Res(("IconBlueBrush", Color.FromRgb(0x60, 0xa5, 0xfa)));
    public static readonly IBrush IconGreen = Res(("IconGreenBrush", Color.FromRgb(0x34, 0xd3, 0x99)));
    public static readonly IBrush IconAmber = Res(("IconAmberBrush", Color.FromRgb(0xfb, 0xbf, 0x24)));
    public static readonly IBrush IconPurpleBg = Res(("IconPurpleBgBrush", Color.FromArgb(0x2e, 0x8b, 0x5c, 0xf6)));
    public static readonly IBrush IconBlueBg = Res(("IconBlueBgBrush", Color.FromArgb(0x2e, 0x3b, 0x82, 0xf6)));
    public static readonly IBrush IconGreenBg = Res(("IconGreenBgBrush", Color.FromArgb(0x2e, 0x10, 0xb9, 0x81)));
    public static readonly IBrush IconAmberBg = Res(("IconAmberBgBrush", Color.FromArgb(0x2e, 0xf5, 0x9e, 0x0b)));
}

/// <summary>柱状图单点：Label=时刻，*H=三色柱高度（px）。公开类型供 XAML x:DataType 编译绑定。</summary>
public sealed record BarGroup(string Label, double CpuH, double MemH, double DiskH)
{
    /// <summary>空槽（滚动窗口左侧补位）不渲染柱体。</summary>
    public bool HasBars => CpuH > 0 || MemH > 0 || DiskH > 0;
}

/// <summary>展示用的计算属性，直接给 DataTemplate 绑定。</summary>
public partial class Pm2Process
{
    public string CpuText => Cpu is >= 0 ? Cpu.Value.ToString("0.#") + "%" : "—";
    public string MemText => Format.Bytes(Memory);
    public string RestartsText => Restarts.ToString();
    public string UptimeText => Format.Duration(StartedAt.HasValue ? DateTime.Now - StartedAt.Value : null);

    /// <summary>Transactions 式副标题：脚本名 · 重启次数 · 已运行</summary>
    public string SubLine =>
        (string.IsNullOrEmpty(Script) ? "pm2 托管进程" : System.IO.Path.GetFileName(Script))
        + $" · 重启 {Restarts} 次 · " + UptimeText;

    /// <summary>圆底图标：进程名首字母，底色按名字 hash 从四色系取，同一进程颜色稳定</summary>
    public string IconGlyph => string.IsNullOrEmpty(Name) ? "?" : Name.Substring(0, 1).ToUpperInvariant();

    public IBrush IconBg
    {
        get
        {
            var hash = Math.Abs(Name.GetHashCode()) % 4;
            return hash switch
            {
                0 => Palette.IconPurpleBg,
                1 => Palette.IconBlueBg,
                2 => Palette.IconGreenBg,
                _ => Palette.IconAmberBg
            };
        }
    }

    public IBrush IconFg
    {
        get
        {
            var hash = Math.Abs(Name.GetHashCode()) % 4;
            return hash switch
            {
                0 => Palette.IconPurple,
                1 => Palette.IconBlue,
                2 => Palette.IconGreen,
                _ => Palette.IconAmber
            };
        }
    }

    public IBrush PillBg => Status switch
    {
        "online" => Palette.OkBg,
        "errored" => Palette.ErrBg,
        "stopped" => Palette.NeutralBg,
        _ => Palette.WarnBg
    };

    public IBrush PillFg => Status switch
    {
        "online" => Palette.OkFg,
        "errored" => Palette.ErrFg,
        "stopped" => Palette.NeutralFg,
        _ => Palette.WarnFg
    };
}
