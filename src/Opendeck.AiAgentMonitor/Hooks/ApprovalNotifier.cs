using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Hooks;

/// <summary>
/// Shows a desktop notification (libnotify / KDE) with the full text of a held permission request, with
/// Approve / Deny buttons that resolve it just like the deck keys. Closed automatically when the request is
/// answered elsewhere or times out. Needed because the agent's own prompt only appears after the hook returns.
/// </summary>
public sealed class ApprovalNotifier
{
    private readonly ApprovalRegistry _approvals;
    private readonly ConcurrentDictionary<string, (Process Proc, int Id)> _open = new();

    public ApprovalNotifier(ApprovalRegistry approvals)
    {
        _approvals = approvals;
        approvals.Added += p => _ = ShowAsync(p);
        approvals.Resolved += (p, _) => Close(p);
    }

    public bool Enabled { get; set; } = true;
    public Func<int> HoldSeconds { get; set; } = () => 30;

    private async Task ShowAsync(PendingApproval p)
    {
        if (!Enabled) return;
        try
        {
            var who = p.Provider == Provider.Claude ? "Claude Code" : "Codex";
            var project = Path.GetFileName(p.Cwd.TrimEnd('/'));
            var title = $"{who} asks permission — {project}";
            var body = Escape(FullText(p)) + $"\n<i>deck / buttons decide; no answer in {HoldSeconds()} s → the app's own prompt</i>";

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
            _open.TryRemove(p.Id, out _);
            switch (action?.Trim())
            {
                case "approve": _approvals.Resolve(p, ApprovalOutcome.Allow); Log.Info($"Approved from notification: {p.Summary}"); break;
                case "deny": _approvals.Resolve(p, ApprovalOutcome.Deny, "Denied from the desktop notification"); Log.Info($"Denied from notification: {p.Summary}"); break;
            }
        }
        catch (Exception ex) { Log.Debug($"notification failed: {ex.Message}"); }
    }

    private void Close(PendingApproval p)
    {
        if (!_open.TryRemove(p.Id, out var entry)) return;
        try
        {
            if (entry.Id > 0)
            {
                var psi = new ProcessStartInfo("gdbus") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "call", "--session", "--dest", "org.freedesktop.Notifications", "--object-path", "/org/freedesktop/Notifications",
                                          "--method", "org.freedesktop.Notifications.CloseNotification", entry.Id.ToString() })
                    psi.ArgumentList.Add(a);
                Process.Start(psi)?.WaitForExit(2000);
            }
            if (!entry.Proc.HasExited) entry.Proc.Kill();
        }
        catch (Exception ex) { Log.Debug($"close notification: {ex.Message}"); }
    }

    /// <summary>The request in full: tool name plus the complete command / input (capped for sanity).</summary>
    public static string FullText(PendingApproval p)
    {
        var sb = new StringBuilder();
        sb.Append(p.ToolName);
        var input = p.ToolInput;
        if (input.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var cmd = input.Str("command");
            if (cmd is null && input.TryGetProperty("command", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                cmd = string.Join(' ', arr.EnumerateArray().Select(e => e.ToString()));
            if (cmd is not null) sb.Append(":\n").Append(cmd.Trim());
            else
            {
                foreach (var prop in input.EnumerateObject())
                {
                    var v = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String ? prop.Value.GetString() : prop.Value.ToString();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    sb.Append('\n').Append(prop.Name).Append(": ").Append(v.Trim());
                }
            }
            var desc = input.Str("description");
            if (desc is not null && cmd is not null) sb.Append("\n— ").Append(desc.Trim());
        }
        var text = sb.ToString();
        return text.Length > 1200 ? text[..1200] + "…" : text;
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
