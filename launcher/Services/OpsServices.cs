using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClassIntraOps.Launcher.Services;

// ---------------------------------------------------------------- 应用状态

public static class AppState
{
    public const int ConsolePort = 9099;

    public static string? CiRoot { get; set; }
    public static string Repo { get; set; } = "https://github.com/ClassIntra/ClassIntra.git";
    public static string Ref { get; set; } = "main";

    private static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "ClassIntraOps.config.json");

    public static void Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(ConfigPath));
            if (doc.RootElement.TryGetProperty("ciRoot", out var r) && r.GetString() is { Length: > 0 } c) CiRoot = c;
            if (doc.RootElement.TryGetProperty("repo", out var rp) && rp.GetString() is { Length: > 0 } repo) Repo = repo;
            if (doc.RootElement.TryGetProperty("ref", out var rf) && rf.GetString() is { Length: > 0 } rf2) Ref = rf2;
        }
        catch { /* 配置损坏则用默认值 */ }
    }

    public static void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(new { ciRoot = CiRoot, repo = Repo, @ref = Ref },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            File.WriteAllText(ConfigPath, json, new UTF8Encoding(false));
        }
        catch { /* 保存失败不阻断 */ }
    }

    /// <summary>CI 根目录判定：ecosystem.config.js 与 server/ 同时存在。</summary>
    public static bool LooksLikeCiRoot(string? dir) =>
        dir != null && File.Exists(Path.Combine(dir, "ecosystem.config.js")) && Directory.Exists(Path.Combine(dir, "server"));

    /// <summary>显式配置 > 从 ops-server 向上探测 ClassIntra 同级目录。</summary>
    public static string? ResolveCiRoot()
    {
        if (LooksLikeCiRoot(CiRoot)) return CiRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir != null; i++)
        {
            foreach (var name in new[] { "ClassIntra", "ClassIntra-main" })
            {
                var candidate = Path.Combine(dir.FullName, name);
                if (LooksLikeCiRoot(candidate)) return candidate;
            }
            if (LooksLikeCiRoot(dir.FullName)) return dir.FullName;
            dir = dir.Parent;
        }
        return CiRoot;
    }
}

// ---------------------------------------------------------------- 进程工具

public static class ToolService
{
    public static ProcessStartInfo Shell(string command, string? cwd)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cwd ?? Directory.GetCurrentDirectory()
        };
        if (OperatingSystem.IsWindows())
        {
            // Windows 上 git/pnpm 是 .cmd，必须 cmd /c；ArgumentList 负责转义
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "/bin/bash";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        return psi;
    }

    public static async Task<(bool Ok, string Output)> RunAsync(string command, string? cwd = null, int timeoutSec = 30)
    {
        try
        {
            using var p = Process.Start(Shell(command, cwd));
            if (p == null) return (false, "进程启动失败");
            var output = await p.StandardOutput.ReadToEndAsync();
            var err = await p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeoutSec * 1000);
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode == 0, (output + " " + err).Trim());
        }
        catch (OperationCanceledException) { return (false, "超时"); }
        catch (Exception e) { return (false, e.Message); }
    }

    public sealed record StreamResult(bool Ok, string Detail);    /// <summary>流式执行，逐行回调——安装向导的实时回显。</summary>
    public static async Task<StreamResult> RunStreamedAsync(string command, string? cwd,
        Action<string> onLine, int timeoutSec = 600)
    {
        Process? p = null;
        try
        {
            p = Process.Start(Shell(command, cwd));
            if (p == null) return new StreamResult(false, "进程启动失败");

            p.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            using var cts = new CancellationTokenSource(timeoutSec * 1000);
            await p.WaitForExitAsync(cts.Token);
            return new StreamResult(p.ExitCode == 0, "exit=" + p.ExitCode);
        }
        catch (OperationCanceledException)
        {
            try { p?.Kill(true); } catch { }
            return new StreamResult(false, "超时中止");
        }
        catch (Exception e)
        {
            return new StreamResult(false, e.Message);
        }
        finally
        {
            p?.Dispose();
        }
    }

    public static async Task<List<(string Name, bool Ok, string Version)>> DetectAsync()
    {
        var defs = new[] { ("Node.js", "node"), ("pnpm", "pnpm"), ("Git", "git"), ("PM2", "pm2") };
        var results = new List<(string, bool, string)>();
        foreach (var (label, cmd) in defs)
        {
            var r = await RunAsync(cmd + " --version", timeoutSec: 15);
            var first = (r.Output.Length > 0 ? r.Output : "").Split('\n')[0].Trim();
            results.Add((label, r.Ok, r.Ok ? first : "缺失"));
        }
        return results;
    }
}

// ---------------------------------------------------------------- PM2

public sealed partial class Pm2Process
{
    // 属性而非字段：Avalonia 编译绑定（x:DataType）只解析属性
    public string Name { get; set; } = "";
    public string Status { get; set; } = "unknown";
    public string Pid { get; set; } = "—";
    public string Script { get; set; } = "";
    public double? Cpu { get; set; }
    public long? Memory { get; set; }
    public int Restarts { get; set; }
    public DateTime? StartedAt { get; set; }
}

public static class Pm2Service
{
    private static readonly string[] AllowedActions = { "start", "stop", "restart", "reload", "delete", "save", "flush" };
    private static readonly Regex NameRe = new("^[A-Za-z0-9_.\\-]+$", RegexOptions.Compiled);

    public static async Task<(bool Ok, List<Pm2Process> Processes, string Error)> JListAsync()
    {
        var r = await ToolService.RunAsync("pm2 jlist", timeoutSec: 20);
        if (!r.Ok) return (false, new List<Pm2Process>(), Truncate(r.Output));

        var text = r.Output;
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return (false, new List<Pm2Process>(), "pm2 jlist 输出无法解析");

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var list = new List<Pm2Process>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var proc = new Pm2Process { Name = Str(item, "name") };
                if (item.TryGetProperty("pid", out var pid) && pid.ValueKind == JsonValueKind.Number)
                    proc.Pid = pid.GetInt32().ToString();

                if (item.TryGetProperty("pm2_env", out var env) && env.ValueKind == JsonValueKind.Object)
                {
                    if (env.TryGetProperty("status", out var st)) proc.Status = st.GetString() ?? "unknown";
                    if (env.TryGetProperty("restart_time", out var rt) && rt.ValueKind == JsonValueKind.Number)
                        proc.Restarts = rt.GetInt32();
                    if (env.TryGetProperty("pm_uptime", out var up) && up.ValueKind == JsonValueKind.Number)
                        proc.StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(up.GetInt64()).LocalDateTime;
                    if (env.TryGetProperty("pm_exec_path", out var ep) && ep.ValueKind == JsonValueKind.String)
                        proc.Script = ep.GetString() ?? "";
                }

                if (item.TryGetProperty("monit", out var monit) && monit.ValueKind == JsonValueKind.Object)
                {
                    if (monit.TryGetProperty("cpu", out var cpu) && cpu.ValueKind == JsonValueKind.Number)
                        proc.Cpu = cpu.GetDouble();
                    if (monit.TryGetProperty("memory", out var mem) && mem.ValueKind == JsonValueKind.Number)
                        proc.Memory = mem.GetInt64();
                }
                list.Add(proc);
            }
            return (true, list, "");
        }
        catch (Exception e)
        {
            return (false, new List<Pm2Process>(), "JSON 解析失败: " + e.Message);
        }
    }

    private static string Str(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Truncate(string s) => s.Length > 300 ? s[..300] : s;

    public static async Task<(bool Ok, string Output)> ActionAsync(string name, string action)
    {
        if (Array.IndexOf(AllowedActions, action) < 0) return (false, "不支持的动作: " + action);
        if (name.Length > 0 && !NameRe.IsMatch(name)) return (false, "进程名不合法: " + name);
        var cmd = action is "save" or "flush" ? "pm2 " + action : "pm2 " + action + " " + name;
        return await ToolService.RunAsync(cmd, timeoutSec: 60);
    }

    /// <summary>改完 .env 后必须用它——普通 restart 保留旧环境变量，不生效。</summary>
    public static async Task<(bool Ok, string Output)> RestartWithEnvAsync(string ciRoot) =>
        await ToolService.RunAsync("pm2 restart ecosystem.config.js --update-env", ciRoot, 120);

    public static async Task<(bool Ok, string Output)> StartAllAsync(string ciRoot)
    {
        var r = await ToolService.RunAsync("pm2 start ecosystem.config.js", ciRoot, 120);
        if (!r.Ok) return r;
        await ToolService.RunAsync("pm2 save", ciRoot, 30);
        return (true, r.Output);
    }
}

// ---------------------------------------------------------------- 系统资源

public sealed class SystemInfo
{
    public double CpuUsage;          // 0..1
    public int CpuCores;
    public string CpuModel = "";
    public long MemTotal, MemFree;
    public double MemUsage => MemTotal > 0 ? (double)(MemTotal - MemFree) / MemTotal : 0;
    public (long Total, long Free)? Disk;
    public string Hostname = "";
}

public static class SystemService
{
    private static (long Idle, long Total)? _lastCpu;

    public static async Task<SystemInfo> CollectAsync(string? ciRoot)
    {
        var info = new SystemInfo
        {
            Hostname = Environment.MachineName,
            CpuCores = Environment.ProcessorCount
        };

        // 内存：Windows 走 GlobalMemoryStatusEx，Linux 读 /proc/meminfo
        if (OperatingSystem.IsWindows())
        {
            var mem = MemoryStatus();
            info.MemTotal = mem.total;
            info.MemFree = mem.free;
        }
        else
        {
            foreach (var line in File.ReadAllLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:")) info.MemTotal = ParseKb(line);
                if (line.StartsWith("MemAvailable:")) { info.MemFree = ParseKb(line); break; }
                if (line.StartsWith("MemFree:") && info.MemFree == 0) info.MemFree = ParseKb(line);
            }
        }

        // CPU：采样差分（Windows GetSystemTimes / Linux /proc/stat）
        var sample = OperatingSystem.IsWindows() ? WindowsCpuTimes() : LinuxCpuTimes();
        if (sample.HasValue)
        {
            var (idle, total) = sample.Value;
            if (_lastCpu.HasValue && total > _lastCpu.Value.Total)
            {
                var dIdle = idle - _lastCpu.Value.Idle;
                var dTotal = total - _lastCpu.Value.Total;
                if (dTotal > 0) info.CpuUsage = Math.Clamp(1.0 - (double)dIdle / dTotal, 0, 1);
            }
            _lastCpu = (idle, total);
        }
        else
        {
            info.CpuUsage = -1; // 首次采样没有基线
        }

        info.CpuModel = await GetCpuModelAsync();

        // 磁盘
        try
        {
            var root = ciRoot ?? AppContext.BaseDirectory;
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root)) ?? "/");
            if (drive.IsReady) info.Disk = (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch { /* 磁盘信息可选 */ }

        return info;
    }

    private static long ParseKb(string line)
    {
        var digits = new string(line.Where(char.IsDigit).ToArray());
        return long.TryParse(digits, out var kb) ? kb * 1024 : 0;
    }

    private static async Task<string> GetCpuModelAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var r = await ToolService.RunAsync(
                    "reg query \"HKLM\\HARDWARE\\DESCRIPTION\\System\\CentralProcessor\\0\" /v ProcessorNameString",
                    timeoutSec: 8);
                var m = Regex.Match(r.Output, "REG_SZ\\s+(.+)");
                return m.Success ? m.Groups[1].Value.Trim() : "";
            }
            foreach (var line in File.ReadAllLines("/proc/cpuinfo"))
                if (line.StartsWith("model name"))
                    return line[(line.IndexOf(':') + 1)..].Trim();
        }
        catch { }
        return "";
    }

    // ---- Windows P/Invoke ----

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    private static (long total, long free) MemoryStatus()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref st)
            ? ((long)st.ullTotalPhys, (long)st.ullAvailPhys)
            : (0, 0);
    }

    private static (long Idle, long Total)? WindowsCpuTimes()
    {
        var ok = GetSystemTimes(out var idle, out var kernel, out var user);
        if (!ok) return null;
        // kernel 时间含 idle，系统总时间 = kernel + user
        return (idle, kernel + user);
    }

    private static (long Idle, long Total)? LinuxCpuTimes()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").First(l => l.StartsWith("cpu "));
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
            var idle = parts[3] + (parts.Length > 4 ? parts[4] : 0); // idle + iowait
            return (idle, parts.Sum());
        }
        catch { return null; }
    }
}

// ---------------------------------------------------------------- 对端探活

public sealed class PeerStatus
{
    public string Url = "";
    public bool Ok;
    public string? Error;
    public int? StatusCode;   // 补充信息：relay 常不响应普通 GET，取不到不影响判定
    public long Ms;
}

public static class PeerService
{
    /// <summary>
    /// 探活单个对端。为什么用 TCP 而不是 HTTP：CI 的 relay 只在 /relay 路径响应
    /// （426 Upgrade Required），根路径会把请求挂到超时误判成离线；TCP 握手不受应用路由影响。
    /// </summary>
    public static async Task<PeerStatus> ProbeAsync(string rawUrl, int timeoutMs = 5000)
    {
        var status = new PeerStatus { Url = rawUrl };
        var target = (rawUrl ?? "").Trim();
        if (target.Length == 0) { status.Error = "空地址"; return status; }

        // ws:// → http://，wss:// → https://
        if (target.StartsWith("ws://", StringComparison.OrdinalIgnoreCase)) target = "http://" + target[5..];
        else if (target.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) target = "https://" + target[6..];
        else if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase)) target = "http://" + target;

        Uri uri;
        try { uri = new Uri(target); }
        catch { status.Error = "地址无法解析"; return status; }

        var port = uri.IsDefaultPort ? (uri.Scheme == "https" ? 443 : 80) : uri.Port;
        var t0 = Environment.TickCount64;

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync(uri.Host, port, cts.Token);
            status.Ms = Environment.TickCount64 - t0;
            status.Ok = true;

            // TCP 通了再顺带取一次 HTTP 状态码，拿不到不影响判定
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(3000) };
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.UserAgent.ParseAdd("ClassIntraOps/1.0");
                using var resp = await http.SendAsync(req);
                status.StatusCode = (int)resp.StatusCode;
            }
            catch { /* 状态码拿不到就算了 */ }

            return status;
        }
        catch (OperationCanceledException) { status.Error = "超时"; }
        catch (SocketException e) { status.Error = e.SocketErrorCode.ToString(); }
        catch (Exception e) { status.Error = e.Message; }
        status.Ms = Environment.TickCount64 - t0;
        return status;
    }

    public static async Task<List<PeerStatus>> ProbeAllAsync(IEnumerable<string> targets, int timeoutMs = 5000)
    {
        var tasks = targets.Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => ProbeAsync(t, timeoutMs));
        var all = await Task.WhenAll(tasks);
        return all.ToList();
    }

    /// <summary>从 server/.env 读 RELAY_SERVERS（逗号分隔）。</summary>
    public static (string ServerId, List<string> Peers, bool HasSecret) ReadRelayConfig(string ciRoot)
    {
        var envPath = Path.Combine(ciRoot, "server", ".env");
        var map = new Dictionary<string, string>();
        if (File.Exists(envPath))
        {
            foreach (var line in File.ReadAllLines(envPath))
            {
                var t = line.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                var eq = t.IndexOf('=');
                if (eq <= 0) continue;
                map[t[..eq].Trim()] = t[(eq + 1)..].Trim().Trim('"');
            }
        }
        var peers = (map.GetValueOrDefault("RELAY_SERVERS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        return (map.GetValueOrDefault("RELAY_SERVER_ID") ?? "", peers,
            !string.IsNullOrEmpty(map.GetValueOrDefault("RELAY_SECRET")));
    }
}
