using Avalonia;
using System.Runtime.Versioning;

namespace ClassIntraOps.Launcher;

internal static class Program
{
    [STAThread]
    [SupportedOSPlatform("windows")]
    [SupportedOSPlatform("linux")]
    public static void Main(string[] args)
    {
        // 全局兜底：未处理异常先落盘（logs/crash.log）——运维工具不允许无声死掉
        System.AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Services.CrashLog.Write("AppDomain", e.ExceptionObject as Exception);
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Services.CrashLog.Write("UnobservedTask", e.Exception);
            e.SetObserved();   // 后台任务的未观察异常不杀进程
        };

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
