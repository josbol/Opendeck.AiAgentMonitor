using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Focus;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Hooks;

/// <summary>
/// Shows a held permission request on screen — the agent's own prompt only appears after the hook returns, so
/// without this the request is only readable on the deck. Two styles:
///   dialog       a kdialog (or zenity) window with the full text and Approve / Deny / "Decide in the app" buttons,
///                kept on top but without stealing keyboard focus (an Enter you were typing must not approve anything);
///   notification a libnotify / KDE notification with Approve / Deny action buttons.
/// Whatever the user answers resolves the same request as the deck keys; the popup closes when the request is
/// answered elsewhere or times out.
/// </summary>
public sealed class ApprovalNotifier
{
    private readonly ApprovalRegistry _approvals;
    private readonly ConcurrentDictionary<string, (Process Proc, int NotificationId)> _open = new();

    public ApprovalNotifier(ApprovalRegistry approvals)
    {
        _approvals = approvals;
        approvals.Added += p => _ = ShowAsync(p);
        approvals.Resolved += (p, _) => Close(p);
    }

    /// <summary>auto | dialog | notification | none</summary>
    public string Style { get; set; } = "auto";
    /// <summary>center | primary | mouse — the monitor the popup opens on.</summary>
    public string Screen { get; set; } = "center";
    public Func<int> HoldSeconds { get; set; } = () => 30;

    /// <summary>"qt" when python3 with a Qt binding can run hooks/approval-dialog.py, else kdialog / zenity, else null.</summary>
    private static readonly Lazy<string?> DialogTool = new(() =>
    {
        if (QtDialogScript() is not null && Exists("python3") && PythonHas("PyQt6", "PySide6", "PyQt5")) return "qt";
        return new[] { "kdialog", "zenity" }.FirstOrDefault(Exists);
    });

    private static string? QtDialogScript()
    {
        foreach (var c in new[] { Path.Combine(AppContext.BaseDirectory, "..", "..", "hooks", "approval-dialog.py"), Path.Combine(AppContext.BaseDirectory, "..", "hooks", "approval-dialog.py"), Path.Combine(AppContext.BaseDirectory, "hooks", "approval-dialog.py") })
            if (File.Exists(c)) return Path.GetFullPath(c);
        return null;
    }

    private static bool PythonHas(params string[] modules)
    {
        try
        {
            var code = "import importlib.util, sys\nsys.exit(0 if any(importlib.util.find_spec(m) for m in " + Json.Serialize(modules) + ") else 1)";
            var psi = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(code);
            using var p = Process.Start(psi); p?.WaitForExit(5000);
            var ok = p?.ExitCode == 0;
            Log.Info(ok ? "Approval popup: Qt dialog (python3)" : "Approval popup: no Python Qt binding; using kdialog/zenity/notification");
            return ok;
        }
        catch { return false; }
    }

    private async Task ShowAsync(PendingApproval p)
    {
        var style = Style;
        if (style is "auto") style = DialogTool.Value is not null ? "dialog" : "notification";
        try
        {
            switch (style)
            {
                case "dialog" when DialogTool.Value is not null: await ShowDialogAsync(p, DialogTool.Value); break;
                case "dialog":
                case "notification": await ShowNotificationAsync(p); break;
            }
        }
        catch (Exception ex) { Log.Debug($"popup failed: {ex.Message}"); }
    }

    // ---- dialog -----------------------------------------------------------------------------

    private async Task ShowDialogAsync(PendingApproval p, string tool)
    {
        var who = p.Provider == Provider.Claude ? "Claude Code" : "Codex";
        var project = Path.GetFileName(p.Cwd.TrimEnd('/'));
        var title = $"{who} asks permission — {project}";
        var previousActive = (await WindowFocuser.RunAsync("xdotool", "getactivewindow")).Trim();

        var psi = new ProcessStartInfo(tool == "qt" ? "python3" : tool) { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = tool == "qt", UseShellExecute = false };
        string? stdinJson = null;
        if (tool == "qt")
        {
            psi.ArgumentList.Add(QtDialogScript()!);
            var desc = p.ToolInput.ValueKind == System.Text.Json.JsonValueKind.Object ? p.ToolInput.Str("description") : null;
            var command = Body(p);
            if (desc is not null && command.EndsWith("\n— " + desc.Trim(), StringComparison.Ordinal)) command = command[..^("\n— " + desc.Trim()).Length];
            stdinJson = Json.Serialize(new { provider = who, project, tool = p.ToolName, command, description = desc, hold_seconds = HoldSeconds(), received_at = p.ReceivedAt.ToUniversalTime().ToString("o"), screen = Screen });
        }
        else if (tool == "kdialog")
        {
            var html = $"""
                <html><body style="font-size:12pt">
                <p><span style="font-size:15pt"><b>{Html(who)} asks permission</b></span> &nbsp;<span style="color:#8a93a6">{Html(project)}</span></p>
                <p><b>{Html(p.ToolName)}</b></p>
                <pre style="font-size:12pt; white-space:pre-wrap; background:#1b1f27; color:#eceff4; padding:8px">{Html(Body(p))}</pre>
                <p style="color:#8a93a6; font-size:10pt">Deck keys or these buttons decide. No answer within {HoldSeconds()} s → the app shows its own prompt.</p>
                </body></html>
                """;
            var geometry = await GeometryOnScreenAsync(780, 360);
            foreach (var a in new[] { "--title", title, "--icon", "dialog-question", "--geometry", geometry,
                                      "--yes-label", "Approve", "--no-label", "Deny", "--cancel-label", "Decide in the app", "--yesnocancel", html })
                psi.ArgumentList.Add(a);
        }
        else // zenity (GTK / Pango markup)
        {
            var text = $"<span size='x-large' weight='bold'>{Html(who)} asks permission</span>  <span foreground='#8a93a6'>{Html(project)}</span>\n\n<b>{Html(p.ToolName)}</b>\n<tt>{Html(Body(p))}</tt>\n\n<span size='small' foreground='#8a93a6'>Deck keys or these buttons decide. No answer within {HoldSeconds()} s → the app shows its own prompt.</span>";
            foreach (var a in new[] { "--question", "--title", title, "--width", "760", "--icon", "dialog-question",
                                      "--ok-label", "Approve", "--cancel-label", "Deny", "--extra-button", "Decide in the app", "--text", text })
                psi.ArgumentList.Add(a);
        }

        var proc = Process.Start(psi);
        if (proc is null) return;
        _open[p.Id] = (proc, 0);
        if (stdinJson is not null)
        {
            await proc.StandardInput.WriteAsync(stdinJson);
            proc.StandardInput.Close();
        }
        else _ = KeepAboveWithoutFocusAsync(proc, previousActive);   // the Qt dialog handles on-top / no-focus itself

        var stdout = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (!_open.TryRemove(p.Id, out _)) return;   // closed by us: the request was already answered elsewhere
        if (p.IsResolved) return;

        var outcome = tool is "kdialog" or "qt"
            ? proc.ExitCode switch { 0 => ApprovalOutcome.Allow, 1 => ApprovalOutcome.Deny, _ => ApprovalOutcome.Release }
            : stdout.Contains("Decide", StringComparison.OrdinalIgnoreCase) ? ApprovalOutcome.Release
              : proc.ExitCode switch { 0 => ApprovalOutcome.Allow, 1 => ApprovalOutcome.Deny, _ => ApprovalOutcome.Release };
        _approvals.Resolve(p, outcome, outcome == ApprovalOutcome.Deny ? "Denied from the desktop dialog" : null);
        Log.Info($"{outcome} from dialog: {p.Summary}");
    }

    /// <summary>kdialog geometry (WxH+X+Y) centred on the configured monitor, from xrandr; falls back to size only.</summary>
    private async Task<string> GeometryOnScreenAsync(int w, int h)
    {
        try
        {
            var monitors = new List<(int W, int H, int X, int Y, bool Primary)>();
            foreach (var line in (await WindowFocuser.RunAsync("xrandr", "--query")).Split('\n'))
            {
                if (!line.Contains(" connected ")) continue;
                var m = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)x(\d+)\+(\d+)\+(\d+)");
                if (m.Success) monitors.Add((int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), line.Contains(" primary ")));
            }
            if (monitors.Count == 0) return $"{w}x{h}";
            var pick = Screen switch
            {
                "primary" => monitors.FirstOrDefault(m => m.Primary) is { W: > 0 } pm ? pm : monitors[0],
                "mouse" => await MonitorUnderMouseAsync(monitors),
                _ => monitors.OrderBy(m => m.X).ThenBy(m => m.Y).ElementAt(monitors.Count / 2),
            };
            return $"{w}x{h}+{pick.X + (pick.W - w) / 2}+{pick.Y + (pick.H - h) / 2}";
        }
        catch { return $"{w}x{h}"; }
    }

    private static async Task<(int W, int H, int X, int Y, bool Primary)> MonitorUnderMouseAsync(List<(int W, int H, int X, int Y, bool Primary)> monitors)
    {
        var loc = await WindowFocuser.RunAsync("xdotool", "getmouselocation");
        var mx = System.Text.RegularExpressions.Regex.Match(loc, @"x:(\d+) y:(\d+)");
        if (mx.Success)
        {
            int x = int.Parse(mx.Groups[1].Value), y = int.Parse(mx.Groups[2].Value);
            foreach (var m in monitors) if (x >= m.X && x < m.X + m.W && y >= m.Y && y < m.Y + m.H) return m;
        }
        return monitors[0];
    }

    /// <summary>Keeps the dialog above other windows and gives keyboard focus back to the window the user was using.</summary>
    private static async Task KeepAboveWithoutFocusAsync(Process proc, string previousActive)
    {
        try
        {
            string? id = null;
            for (var i = 0; i < 12 && id is null && !proc.HasExited; i++)
            {
                await Task.Delay(150);
                foreach (var line in (await WindowFocuser.RunAsync("wmctrl", "-lp")).Split('\n'))
                {
                    var parts = line.Split(' ', 5, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3 && int.TryParse(parts[2], out var pid) && pid == proc.Id) { id = parts[0]; break; }
                }
            }
            if (id is null) return;
            await WindowFocuser.RunAsync("wmctrl", "-i", "-r", id, "-b", "add,above");
            if (long.TryParse(previousActive, out var prev) && prev > 0)
                await WindowFocuser.RunAsync("xdotool", "windowactivate", prev.ToString());
        }
        catch (Exception ex) { Log.Debug($"dialog placement: {ex.Message}"); }
    }

    // ---- notification -------------------------------------------------------------------------

    private async Task ShowNotificationAsync(PendingApproval p)
    {
        var who = p.Provider == Provider.Claude ? "Claude Code" : "Codex";
        var project = Path.GetFileName(p.Cwd.TrimEnd('/'));
        var title = $"{who} asks permission — {project}";
        var body = Html(FullText(p)) + $"\n<i>deck / buttons decide; no answer in {HoldSeconds()} s → the app's own prompt</i>";

        var psi = new ProcessStartInfo("notify-send") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-p", "-a", "AI Agent Monitor", "-i", "dialog-question", "-u", "normal", "-t", (HoldSeconds() * 1000).ToString(),
                                  "-A", "approve=Approve", "-A", "deny=Deny", title, body })
            psi.ArgumentList.Add(a);
        var proc = Process.Start(psi);
        if (proc is null) return;

        // first stdout line: the notification id (-p); a later line: the chosen action (-A implies --wait)
        var idLine = await proc.StandardOutput.ReadLineAsync();
        var id = int.TryParse(idLine?.Trim(), out var n) ? n : 0;
        _open[p.Id] = (proc, id);
        var action = await proc.StandardOutput.ReadLineAsync();
        if (!_open.TryRemove(p.Id, out _)) return;
        switch (action?.Trim())
        {
            case "approve": _approvals.Resolve(p, ApprovalOutcome.Allow); Log.Info($"Approved from notification: {p.Summary}"); break;
            case "deny": _approvals.Resolve(p, ApprovalOutcome.Deny, "Denied from the desktop notification"); Log.Info($"Denied from notification: {p.Summary}"); break;
        }
    }

    // ---- shared ----------------------------------------------------------------------------------

    private void Close(PendingApproval p)
    {
        if (!_open.TryRemove(p.Id, out var entry)) return;
        try
        {
            if (entry.NotificationId > 0)
            {
                var psi = new ProcessStartInfo("gdbus") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
                                          "--method", "org.freedesktop.Notifications.CloseNotification", entry.NotificationId.ToString() })
                    psi.ArgumentList.Add(a);
                Process.Start(psi)?.WaitForExit(2000);
            }
            if (!entry.Proc.HasExited) entry.Proc.Kill();
        }
        catch (Exception ex) { Log.Debug($"close popup: {ex.Message}"); }
    }

    /// <summary>The request in full: tool name plus the complete command / input (capped for sanity).</summary>
    public static string FullText(PendingApproval p)
    {
        var body = Body(p);
        return body.Length == 0 ? p.ToolName : p.ToolName + ":\n" + body;
    }

    /// <summary>Just the command / input text of the request, without the tool name.</summary>
    public static string Body(PendingApproval p)
    {
        var sb = new StringBuilder();
        var input = p.ToolInput;
        if (input.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var cmd = input.Str("command");
            if (cmd is null && input.TryGetProperty("command", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                cmd = string.Join(' ', arr.EnumerateArray().Select(e => e.ToString()));
            if (cmd is not null) sb.Append(cmd.Trim());
            else if (FirstString(input) is { } first && first.TrimStart().StartsWith("*** Begin Patch", StringComparison.Ordinal))
            {
                // Codex apply_patch: list the touched files first, then the patch itself
                var files = PendingApproval.PatchFiles(first);
                if (files.Count > 0) sb.Append(string.Join("\n", files)).Append("\n\n");
                sb.Append(first.Trim());
            }
            else
            {
                foreach (var prop in input.EnumerateObject())
                {
                    var v = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(prop.Name).Append(": ").Append(v.Trim());
                }
            }
            var desc = input.Str("description");
            if (desc is not null && cmd is not null) sb.Append("\n— ").Append(desc.Trim());
        }
        var text = sb.ToString();
        return text.Length > 1200 ? text[..1200] + "…" : text;
    }

    private static string? FirstString(System.Text.Json.JsonElement input)
    {
        foreach (var prop in input.EnumerateObject())
            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String && prop.Value.GetString() is { Length: > 0 } v) return v;
        return null;
    }

    private static string Html(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static bool Exists(string tool)
    {
        try
        {
            var psi = new ProcessStartInfo("which", tool) { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi); p?.WaitForExit(2000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }
}
