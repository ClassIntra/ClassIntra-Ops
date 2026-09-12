using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClassIntraOps.Launcher.Services;

namespace ClassIntraOps.Launcher;

public class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // UI 线程兜底：异常先落盘再标记 Handled——窗口不闪退，完整现场在 logs/crash.log
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLog.Write("UIThread", e.Exception);
            e.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 关闭窗口 ≠ 退出：主窗口隐藏进系统托盘后台运行，真正退出走托盘菜单的显式 Shutdown
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
