namespace Opendeck.AiAgentMonitor.Util;

/// <summary>Helpers over /proc (Linux only).</summary>
public static class ProcUtil
{
    public static bool IsAlive(int pid, string? expectedStartTicks = null)
    {
        if (pid <= 0) return false;
        var stat = ReadStat(pid);
        if (stat is null) return false;
        if (expectedStartTicks is null) return true;
        return stat.Value.StartTicks == expectedStartTicks;
    }

    /// <summary>Parses /proc/pid/stat: returns (comm, ppid, starttime in clock ticks).</summary>
    public static (string Comm, int Ppid, string StartTicks)? ReadStat(int pid)
    {
        try
        {
            var text = File.ReadAllText($"/proc/{pid}/stat");
            var close = text.LastIndexOf(')');
            if (close < 0) return null;
            var open = text.IndexOf('(');
            var comm = text.Substring(open + 1, close - open - 1);
            var rest = text[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // rest[0]=state rest[1]=ppid ... starttime is field 22 overall => rest index 19
            var ppid = int.Parse(rest[1]);
            var start = rest.Length > 19 ? rest[19] : "";
            return (comm, ppid, start);
        }
        catch { return null; }
    }

    public static string? Exe(int pid)
    {
        try { return new FileInfo($"/proc/{pid}/exe").LinkTarget; } catch { return null; }
    }

    public static string? Cwd(int pid)
    {
        try { return new DirectoryInfo($"/proc/{pid}/cwd").LinkTarget; } catch { return null; }
    }

    public static string? CmdLine(int pid)
    {
        try { return File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ').Trim(); } catch { return null; }
    }

    /// <summary>Ancestors of pid, nearest first, excluding pid itself and pid 1.</summary>
    public static List<(int Pid, string Comm)> Ancestors(int pid)
    {
        var list = new List<(int, string)>();
        var cur = pid;
        for (var i = 0; i < 64; i++)
        {
            var st = ReadStat(cur);
            if (st is null) break;
            var ppid = st.Value.Ppid;
            if (ppid <= 1) break;
            var pst = ReadStat(ppid);
            if (pst is null) break;
            list.Add((ppid, pst.Value.Comm));
            cur = ppid;
        }
        return list;
    }

    public static IEnumerable<int> AllPids()
    {
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories("/proc"); } catch { yield break; }
        foreach (var d in dirs)
        {
            var name = Path.GetFileName(d);
            if (int.TryParse(name, out var pid)) yield return pid;
        }
    }

    /// <summary>Pids whose comm equals one of the names.</summary>
    public static List<int> FindByComm(params string[] names)
    {
        var set = new HashSet<string>(names, StringComparer.Ordinal);
        var result = new List<int>();
        foreach (var pid in AllPids())
        {
            var st = ReadStat(pid);
            if (st is not null && set.Contains(st.Value.Comm)) result.Add(pid);
        }
        return result;
    }

    /// <summary>Best-effort classification of the host application an agent process runs in.</summary>
    public static string DetectHost(int pid)
    {
        foreach (var (apid, comm) in Ancestors(pid))
        {
            var c = comm.ToLowerInvariant();
            if (c is "rider" or "idea" or "clion" or "pycharm" or "webstorm" or "goland" or "rustrover" or "datagrip" || c.StartsWith("jetbrains"))
                return "Rider";
            var exe = Exe(apid)?.ToLowerInvariant() ?? "";
            if (exe.Contains("jetbrains") || exe.Contains("/rider/")) return "Rider";
            if (c.Contains("code") && (exe.Contains("vscode") || exe.Contains("code-oss") || exe.Contains("/code"))) return "VS Code";
            if (c is "konsole" or "kitty" or "alacritty" or "wezterm" or "wezterm-gui" or "foot" or "xterm" or "gnome-terminal-" or "ptyxis" or "tilix" or "terminator" or "ghostty" or "yakuake")
                return Cap(c.TrimEnd('-'));
            if (c is "tmux: server" || c.StartsWith("tmux")) return "tmux";
            if (c is "chatgpt") return "App";
        }
        return "Term";
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
