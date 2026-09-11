namespace ClassIntraOps.Launcher.Services;

public sealed record ResourceSample(DateTime Time, double Cpu, double Mem, double Disk);

/// <summary>
/// 资源历史：内存滚动窗口，容量 24 个点（30s 一采 ≈ 12 分钟）。
/// 不持久化——控制台重启从零攒，用户已确认接受。
/// </summary>
public static class HistoryService
{
    private static readonly object Gate = new();
    private static readonly List<ResourceSample> Samples = new();
    private const int Capacity = 24;

    public static void Add(ResourceSample sample)
    {
        lock (Gate)
        {
            Samples.Add(sample);
            if (Samples.Count > Capacity) Samples.RemoveAt(0);
        }
    }

    public static List<ResourceSample> Snapshot()
    {
        lock (Gate) return new List<ResourceSample>(Samples);
    }
}
