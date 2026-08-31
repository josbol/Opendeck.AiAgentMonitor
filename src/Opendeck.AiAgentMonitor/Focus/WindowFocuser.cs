using System.Diagnostics;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Focus;

/// <summary>
/// Brings the window that hosts an agent to the front on X11 (KDE): switches to its virtual desktop,
/// un-minimizes, raises and focuses it, verifies the result and falls back to a KWin script when the
/// window manager ignored the request. A Codex desktop-app window hidden to the tray is relaunched.
/// </summary>
public static class WindowFocuser
{
    private sealed record Win(string Id, int Desktop, int Pid, string Title)
    {
        public long Xid => Convert.ToInt64(Id, 16);
    }

    public static async Task<bool> FocusAsync(AgentInfo agent, bool dryRun = false)
    {
        try
        {
            if (!dryRun) await TrySelectKonsoleTabAsync(agent);   // before matching: selecting the tab retitles the window
            var windows = await ListWindowsAsync();
            var win = Pick(agent, windows);
            if (win is null && agent.Host == "App" && !dryRun)
            {
                // The Codex desktop app hides its window when "closed" to the tray; launching it again shows it.
                Log.Info("Codex app window not mapped; relaunching the app to show it");
                await LaunchDetachedAsync("chatgpt");
                for (var i = 0; i < 12 && win is null; i++) { await Task.Delay(250); win = Pick(agent, await ListWindowsAsync()); }
            }
            if (win is null) { Log.Info($"No window found for {agent.Key}"); return false; }
            Log.Info($"{(dryRun ? "Would focus" : "Focusing")} window {win.Id} '{win.Title}' (pid {win.Pid}, desktop {win.Desktop}) for {agent.Key}");
            if (dryRun) return true;
            return await ActivateAsync(win);
        }
        catch (Exception ex) { Log.Warn($"focus failed: {ex.Message}"); return false; }
    }

    /// <summary>True when the agent's window is the active (focused) window right now — and, in Konsole, its tab is the current one.</summary>
    public static async Task<bool> IsAgentWindowActiveAsync(AgentInfo agent)
    {
        try
        {
            var win = Pick(agent, await ListWindowsAsync());
            if (win is null) return false;
            var active = (await RunAsync("xdotool", "getactivewindow")).Trim();
            if (!long.TryParse(active, out var id) || id != win.Xid) return false;
            var tab = await FindKonsoleTabAsync(agent);
            return tab is null || tab.IsCurrent;
        }
        catch { return false; }
    }

    // ---- Konsole tabs ---------------------------------------------------------------------
    // Konsole hosts many tabs (sessions) in one window; raising the window shows whichever tab is
    // current. Its per-process D-Bus service (org.kde.konsole-<pid>) maps sessions to shell pids,
    // so the tab whose shell is an ancestor of the agent can be selected before the window is raised.

    private sealed record KonsoleTab(string Service, string WindowPath, string SessionId, bool IsCurrent);

    private static async Task<KonsoleTab?> FindKonsoleTabAsync(AgentInfo agent)
    {
        try
        {
            if (agent.Pid is not { } pid) return null;
            var chain = new List<int> { pid };
            var konsole = 0;
            foreach (var (apid, comm) in ProcUtil.Ancestors(pid))
            {
                if (comm == "konsole") { konsole = apid; break; }
                chain.Add(apid);
            }
            if (konsole == 0) return null;

            var service = $"org.kde.konsole-{konsole}";
            var paths = (await RunAsync("qdbus6", service)).Split('\n').Select(l => l.Trim()).ToList();
            string? sessionId = null;
            foreach (var s in paths.Where(p => p.StartsWith("/Sessions/", StringComparison.Ordinal)))
            {
                var shell = (await RunAsync("qdbus6", service, s, "org.kde.konsole.Session.processId")).Trim();
                if (int.TryParse(shell, out var shellPid) && chain.Contains(shellPid)) { sessionId = s["/Sessions/".Length..]; break; }
            }
            if (sessionId is null) return null;

            foreach (var w in paths.Where(p => p.StartsWith("/Windows/", StringComparison.Ordinal) && p.Length > "/Windows/".Length))
            {
                var ids = (await RunAsync("qdbus6", service, w, "org.kde.konsole.Window.sessionList"))
                    .Split('\n', ' ').Select(x => x.Trim()).Where(x => x.Length > 0);
                if (!ids.Contains(sessionId)) continue;
                var current = (await RunAsync("qdbus6", service, w, "org.kde.konsole.Window.currentSession")).Trim();
                return new KonsoleTab(service, w, sessionId, current == sessionId);
            }
        }
        catch (Exception ex) { Log.Debug($"konsole tab lookup: {ex.Message}"); }
        return null;
    }

    private static async Task TrySelectKonsoleTabAsync(AgentInfo agent)
    {
        var tab = await FindKonsoleTabAsync(agent);
        if (tab is null || tab.IsCurrent) return;
        await RunAsync("qdbus6", tab.Service, tab.WindowPath, "org.kde.konsole.Window.setCurrentSession", tab.SessionId);
        Log.Info($"Konsole: selected tab (session {tab.SessionId}) in {tab.Service}{tab.WindowPath}");
        await Task.Delay(120);   // let the window title follow the new tab
    }

    /// <summary>Activates a window by X id (hex or decimal). Diagnostic entry point (`--activate`).</summary>
    public static async Task<bool> ActivateByIdAsync(string id)
    {
        var xid = id.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(id[2..], 16) : long.Parse(id);
        var win = (await ListWindowsAsync()).FirstOrDefault(w => w.Xid == xid);
        if (win is null) { Log.Warn($"window {id} is not listed by wmctrl"); return false; }
        return await ActivateAsync(win);
    }

    private static async Task<bool> ActivateAsync(Win win)
    {
        // 1. right virtual desktop (wmctrl -ia does this too, but be explicit and give KWin a moment)
        if (win.Desktop >= 0)
        {
            var current = await CurrentDesktopAsync();
            if (current >= 0 && current != win.Desktop) { await RunAsync("wmctrl", "-s", win.Desktop.ToString()); await Task.Delay(150); }
        }
        // 2. un-minimize (map) + activate + raise
        if (await IsMinimizedAsync(win)) await RunAsync("xdotool", "windowmap", win.Xid.ToString());
        await RunAsync("wmctrl", "-ia", win.Id);
        await RunAsync("xdotool", "windowactivate", win.Xid.ToString());
        await RunAsync("xdotool", "windowraise", win.Xid.ToString());
        if (await VerifyAsync(win, 600)) return true;

        // 3. second attempt
        Log.Info($"activation of {win.Id} not confirmed; retrying");
        await RunAsync("xdotool", "windowmap", win.Xid.ToString());
        await RunAsync("wmctrl", "-ia", win.Id);
        if (await VerifyAsync(win, 600)) return true;

        // 4. last resort: ask KWin directly through a one-shot script (works regardless of focus-stealing prevention)
        Log.Info($"falling back to a KWin script for {win.Id}");
        if (await KWinActivateAsync(win) && await VerifyAsync(win, 800)) return true;
        Log.Warn($"could not confirm that {win.Id} became the active window");
        return false;
    }

    private static async Task<bool> VerifyAsync(Win win, int waitMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);
        while (true)
        {
            var active = (await RunAsync("xdotool", "getactivewindow")).Trim();
            if (long.TryParse(active, out var id) && id == win.Xid && !await IsMinimizedAsync(win)) return true;
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(100);
        }
    }

    private static async Task<bool> IsMinimizedAsync(Win win)
    {
        var state = await RunAsync("xprop", "-id", win.Id, "_NET_WM_STATE");
        return state.Contains("_NET_WM_STATE_HIDDEN", StringComparison.Ordinal);
    }

    private static async Task<int> CurrentDesktopAsync()
    {
        var output = await RunAsync("wmctrl", "-d");
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && parts[1] == "*" && int.TryParse(parts[0], out var d)) return d;
        }
        return -1;
    }

    private static async Task<bool> KWinActivateAsync(Win win)
    {
        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"aiagentmonitor-focus-{Environment.ProcessId}.js");
            var title = win.Title.Replace("\\", "\\\\").Replace("\"", "\\\"");
            File.WriteAllText(script, $$"""
                const pid = {{win.Pid}};
                const title = "{{title}}";
                for (const w of workspace.windowList()) {
                    if (w.pid === pid && w.caption === title) {
                        w.minimized = false;
                        if (!w.onAllDesktops && w.desktops && w.desktops.length > 0) workspace.currentDesktop = w.desktops[0];
                        workspace.activeWindow = w;
                        break;
                    }
                }
                """);
            var id = (await RunAsync("qdbus6", "org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.loadScript", script, $"aiagentmonitor-focus-{Environment.ProcessId}")).Trim();
            if (!int.TryParse(id, out var scriptId) || scriptId < 0) { Log.Debug($"KWin loadScript returned '{id}'"); return false; }
            await RunAsync("qdbus6", "org.kde.KWin", $"/Scripting/Script{scriptId}", "org.kde.kwin.Script.run");
            await Task.Delay(300);
            await RunAsync("qdbus6", "org.kde.KWin", $"/Scripting/Script{scriptId}", "org.kde.kwin.Script.stop");
            await RunAsync("qdbus6", "org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.unloadScript", $"aiagentmonitor-focus-{Environment.ProcessId}");
            try { File.Delete(script); } catch { }
            return true;
        }
        catch (Exception ex) { Log.Debug($"KWin script fallback failed: {ex.Message}"); return false; }
    }

    // ---- choosing the window ------------------------------------------------------------

    private static Win? Pick(AgentInfo agent, List<Win> windows)
    {
        var names = ProjectNames(agent.Cwd);
        int Score(Win w) => ScoreTitle(w.Title, names, agent.Provider);
        // highest score; among equals prefer a detached tool window ("Terminal - Project") over a main window, then the shorter title
        Win? Best(IEnumerable<Win> ws) => ws.OrderByDescending(Score).ThenByDescending(w => w.Title.Contains(" - ", StringComparison.Ordinal) ? 1 : 0).ThenBy(w => w.Title.Length).FirstOrDefault();

        // 1. Walk the process ancestry of the agent process (Claude: registry pid; Codex: lock owner) and match windows by _NET_WM_PID.
        if (agent.Pid is { } pid)
        {
            foreach (var (apid, _) in ProcUtil.Ancestors(pid))
            {
                var byPid = windows.Where(w => w.Pid == apid).ToList();
                if (byPid.Count > 0) return Best(byPid);
            }
        }
        // 2. Host heuristics.
        if (agent.Host == "App")
            return windows.FirstOrDefault(w => w.Title.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase) || w.Title.Contains("Codex", StringComparison.OrdinalIgnoreCase));
        if (agent.Host == "Rider")
        {
            var rider = windows.Where(w => ProcUtil.ReadStat(w.Pid)?.Comm is "rider" or "idea").ToList();
            if (rider.Count > 0) return Best(rider);
        }
        // 3. Title match anywhere.
        return windows.Where(w => Score(w) > 0).OrderByDescending(Score).FirstOrDefault();
    }

    /// <summary>
    /// How well a window title matches the agent's project. JetBrains IDEs title the main window "Project" or
    /// "Project – file" and detached tool windows "Terminal - Project"; the session lives in the terminal, so that
    /// wins for Claude. Names come from <see cref="ProjectNames"/> (directory, .idea, .sln, git root).
    /// </summary>
    internal static int ScoreTitle(string title, IReadOnlyList<string> names, Provider provider)
    {
        const StringComparison cmp = StringComparison.OrdinalIgnoreCase;
        var best = 0;
        var sep = title.IndexOf(" - ", StringComparison.Ordinal);
        var tool = sep > 0 ? title[..sep] : null;
        var toolProject = sep > 0 ? title[(sep + 3)..] : null;
        foreach (var name in names)
        {
            if (name.Length == 0) continue;
            int s;
            if (toolProject is not null && toolProject.Equals(name, cmp))
                s = provider == Provider.Claude && tool!.Equals("Terminal", cmp) ? 40 : 30;      // detached tool window of this project
            else if (title.Equals(name, cmp) || title.StartsWith(name + " ", cmp) || title.StartsWith(name + "–", cmp))
                s = 20;                                                                          // main IDE window
            else if (title.Contains(name, cmp))
                s = 10;
            else s = 0;
            best = Math.Max(best, s);
        }
        return best;
    }

    private static readonly Dictionary<string, (DateTime At, List<string> Names)> NameCache = new();

    /// <summary>Names a window title may carry for a working directory: its basename, the JetBrains project name
    /// (.idea/.idea.&lt;Name&gt; or .idea/.name), solution names (*.sln, *.slnx) and the git root's basename, searched upwards.</summary>
    internal static List<string> ProjectNames(string cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return new List<string>();
        lock (NameCache)
            if (NameCache.TryGetValue(cwd, out var cached) && (DateTime.UtcNow - cached.At).TotalSeconds < 60) return cached.Names;

        var names = new List<string>();
        void Add(string? n)
        {
            n = n?.Trim();
            if (!string.IsNullOrEmpty(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
        }
        Add(Path.GetFileName(cwd.TrimEnd('/')));
        var dir = cwd.TrimEnd('/');
        for (var depth = 0; depth < 4 && !string.IsNullOrEmpty(dir); depth++)
        {
            try
            {
                var idea = Path.Combine(dir, ".idea");
                if (Directory.Exists(idea))
                {
                    foreach (var d in Directory.EnumerateDirectories(idea, ".idea.*")) Add(Path.GetFileName(d)[6..]);
                    var nameFile = Path.Combine(idea, ".name");
                    if (File.Exists(nameFile)) Add(File.ReadAllText(nameFile));
                }
                foreach (var f in Directory.EnumerateFiles(dir, "*.sln")) Add(Path.GetFileNameWithoutExtension(f));
                foreach (var f in Directory.EnumerateFiles(dir, "*.slnx")) Add(Path.GetFileNameWithoutExtension(f));
                if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git"))) Add(Path.GetFileName(dir));
            }
            catch { /* unreadable directory: ignore */ }
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        lock (NameCache) NameCache[cwd] = (DateTime.UtcNow, names);
        return names;
    }

    private static async Task<List<Win>> ListWindowsAsync()
    {
        var output = await RunAsync("wmctrl", "-lp");
        var list = new List<Win>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // 0x02897ac7  3 6299   host Title with spaces      (desktop -1 = on all desktops)
            var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            if (!int.TryParse(parts[1], out var desktop) || !int.TryParse(parts[2], out var pid)) continue;
            list.Add(new Win(parts[0], desktop, pid, parts[4]));
        }
        return list;
    }

    // ---- process helpers ------------------------------------------------------------------

    public static async Task<string> RunAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
        return stdout;
    }

    private static Task LaunchDetachedAsync(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { Process.Start(psi); } catch (Exception ex) { Log.Warn($"launch {file}: {ex.Message}"); }
        return Task.CompletedTask;
    }
}
