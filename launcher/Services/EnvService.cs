using System.Text;
using System.Text.RegularExpressions;

namespace ClassIntraOps.Launcher.Services;

public sealed class EnvField
{
    public string Key = "";
    public string DefaultValue = "";
    public bool Secret;
    public string Hint = "";
}

public sealed class EnvGroup
{
    public string Title = "";
    public string Description = "";
    public List<EnvField> Fields = new();
}

public sealed class EnvSchema
{
    public List<EnvGroup> Groups = new();
    public List<EnvField> ExtraKeys = new();
    public Dictionary<string, string> Current = new();
    public Dictionary<string, string> Masked = new();
    public bool HasEnv;
    public string EnvPath = "";
}

/// <summary>
/// .env 表单 schema 推导 + 保序保注释读写。
/// schema 两个来源：server/.env.example（权威，带分组注释）+ ecosystem.config.js env 块（正则提取，不求值）。
/// </summary>
public static class EnvService
{
    private static readonly Regex SecretRe = new("(KEY|SECRET|PASSWORD|TOKEN|PRIVATE|CREDENTIAL)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsSecret(string key) => SecretRe.IsMatch(key);

    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return value.Length <= 8
            ? new string('*', value.Length)
            : new string('*', value.Length - 4) + value[^4..];
    }

    private sealed record Line(string Kind, string Raw, string Key = "", string Value = "");

    private static (List<Line> Lines, Dictionary<string, string> Map) Parse(string text)
    {
        var lines = new List<Line>();
        var map = new Dictionary<string, string>();

        foreach (var raw in (text ?? "").Split('\n'))
        {
            var rawNoCr = raw.TrimEnd('\r');
            var trimmed = rawNoCr.Trim();

            if (trimmed.Length == 0) { lines.Add(new Line("blank", rawNoCr)); continue; }
            if (trimmed[0] == '#') { lines.Add(new Line("comment", rawNoCr)); continue; }

            var eq = trimmed.IndexOf('=');
            if (eq < 0) { lines.Add(new Line("raw", rawNoCr)); continue; }

            var key = trimmed[..eq].Trim();
            var value = trimmed[(eq + 1)..].Trim();
            if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                value = value[1..^1];

            lines.Add(new Line("pair", rawNoCr, key, value));
            map[key] = value;
        }
        return (lines, map);
    }

    private static List<EnvGroup> BuildGroups(string exampleText)
    {
        var (lines, _) = Parse(exampleText);
        var groups = new List<EnvGroup>();
        EnvGroup? current = null;
        var pending = new List<string>();

        void Flush()
        {
            if (current is { Fields.Count: > 0 }) groups.Add(current);
            current = null;
        }

        foreach (var line in lines)
        {
            if (line.Kind == "blank") { Flush(); pending.Clear(); continue; }

            if (line.Kind == "comment")
            {
                pending.Add(line.Raw.TrimStart().TrimStart('#').Trim());
                continue;
            }

            if (line.Kind == "pair")
            {
                if (current == null)
                {
                    current = new EnvGroup
                    {
                        Title = pending.Count > 0 ? pending[0] : "其他",
                        Description = string.Join(" ", pending.Skip(1))
                    };
                }
                else if (pending.Count > 0 && current.Fields.Count == 0)
                {
                    current.Description = (current.Description + " " + string.Join(" ", pending)).Trim();
                }

                current.Fields.Add(new EnvField
                {
                    Key = line.Key,
                    DefaultValue = line.Value,
                    Secret = IsSecret(line.Key),
                    Hint = string.Join(" ", pending)
                });
                pending.Clear();
            }
        }
        Flush();
        return groups;
    }

    /// <summary>从 ecosystem.config.js 的 env 块提取键名。纯文本扫描 + 大括号配平，不执行 JS。</summary>
    private static List<string> ExtractEcosystemKeys(string text)
    {
        var keys = new List<string>();
        var s = text ?? "";
        var start = Regex.Match(s, @"env\s*:\s*\{");
        if (!start.Success) return keys;

        var depth = 0;
        var end = -1;
        for (var i = start.Index; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        if (end < 0) return keys;

        var block = s[(start.Index + start.Length)..end];
        foreach (Match m in Regex.Matches(block, @"(?m)^\s*([A-Za-z_][A-Za-z0-9_]*)\s*:"))
            keys.Add(m.Groups[1].Value);
        return keys;
    }

    public static EnvSchema ReadSchema(string ciRoot)
    {
        var result = new EnvSchema();

        var examplePath = Path.Combine(ciRoot, "server", ".env.example");
        if (File.Exists(examplePath))
        {
            result.Groups = BuildGroups(File.ReadAllText(examplePath, Encoding.UTF8));
        }

        var known = new HashSet<string>();
        foreach (var g in result.Groups)
            foreach (var f in g.Fields)
                known.Add(f.Key);

        var ecoPath = Path.Combine(ciRoot, "ecosystem.config.js");
        if (File.Exists(ecoPath))
        {
            foreach (var k in ExtractEcosystemKeys(File.ReadAllText(ecoPath, Encoding.UTF8)))
            {
                if (!known.Contains(k))
                    result.ExtraKeys.Add(new EnvField { Key = k, Secret = IsSecret(k) });
            }
        }

        // 当前生效值
        result.EnvPath = Path.Combine(ciRoot, "server", ".env");
        if (File.Exists(result.EnvPath))
        {
            result.HasEnv = true;
            var (_, map) = Parse(File.ReadAllText(result.EnvPath, Encoding.UTF8));
            result.Current = map;
            foreach (var (k, v) in map)
                result.Masked[k] = IsSecret(k) ? Mask(v) : v;
        }
        return result;
    }

    /// <summary>
    /// 写 .env：保留注释与键的原有位置，新键追加到末尾。
    /// 强制 UTF-8 + LF 无 BOM——PowerShell 5.1 的 Set-Content 默认 GBK，必须绕开。
    /// </summary>
    public static (bool Ok, string Path, List<string> Updated, List<string> Appended, string? Backup) WriteValues(
        string ciRoot, Dictionary<string, string> updates)
    {
        var envPath = Path.Combine(ciRoot, "server", ".env");
        var original = File.Exists(envPath) ? File.ReadAllText(envPath, Encoding.UTF8) : "";

        string? backup = null;
        if (original.Length > 0)
        {
            backup = envPath + ".bak-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            File.WriteAllText(backup, original, new UTF8Encoding(false));
        }

        var (lines, _) = Parse(original);
        var remaining = new Dictionary<string, string>(updates);

        static string Quote(string v) =>
            v.Contains(' ') || v.Contains('"') || v.Contains('#') ? "\"" + v.Replace("\"", "\\\"") + "\"" : v;

        var outLines = new List<string>();
        foreach (var line in lines)
        {
            if (line.Kind == "pair" && remaining.ContainsKey(line.Key))
            {
                outLines.Add(line.Key + "=" + Quote(remaining[line.Key]));
                remaining.Remove(line.Key);
            }
            else
            {
                outLines.Add(line.Raw);
            }
        }

        if (remaining.Count > 0)
        {
            outLines.Add("");
            outLines.Add("# 由 ClassIntraOps 追加 " + DateTime.Now.ToString("s"));
            foreach (var (k, v) in remaining)
                outLines.Add(k + "=" + Quote(v));
        }

        var serverDir = Path.GetDirectoryName(envPath)!;
        if (!Directory.Exists(serverDir)) Directory.CreateDirectory(serverDir);

        var text = string.Join("\n", outLines).Replace("\r\n", "\n") + "\n";
        File.WriteAllText(envPath, text, new UTF8Encoding(false));

        return (true, envPath, updates.Keys.ToList(), remaining.Keys.ToList(), backup);
    }
}
