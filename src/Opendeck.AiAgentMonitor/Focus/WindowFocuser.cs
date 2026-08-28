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

    /// <summary>True when the agent's window is the active (focused) window right now.</summary>
    public static async Task<bool> IsAgentWindowActiveAsync(AgentInfo agent)
    {
        try
        {
            var win = Pick(agent, await ListWindowsAsync());
            if (win is null) return false;
            var active = (await RunAsync("xdotool", "getactivewindow")).Trim();
            return long.TryParse(active, out var id) && id == win.Xid;
        }
        catch { return false; }
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
        var project = agent.ProjectName;
        int Score(Win w)
        {
            var t = w.Title;
            if (project.Length == 0) return 0;
            if (t.StartsWith("Terminal - " + project, StringComparison.OrdinalIgnoreCase)) return 3;   // detached IDE terminal window
            if (t.Contains(project, StringComparison.OrdinalIgnoreCase)) return 2;
            return 0;
        }
        Win? Best(IEnumerable<Win> ws) => ws.OrderByDescending(Score).ThenBy(w => w.Title.Length).FirstOrDefault();

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
