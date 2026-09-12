using System.IO;

namespace ClassIntraOps.Launcher.Services;

/// <summary>
/// 崩溃与未处理异常的落盘日志。
/// 运维工具不允许无声死掉：任何被兜底捕获的异常都写进 logs/crash.log——
/// 既保住进程不闪退，也留下完整现场供追溯。
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static void Write(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            var line =
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{source}] {ex?.GetType().Name}: {ex?.Message}\n" +
                (string.IsNullOrEmpty(ex?.StackTrace) ? "(无堆栈)" : ex!.StackTrace) + "\n\n";
            lock (Gate) File.AppendAllText(Path.Combine(dir, "crash.log"), line);
        }
        catch
        {
            // 落盘失败（目录只读等）只能放弃——兜底逻辑不能再抛出二次异常
        }
    }
}
