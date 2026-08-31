using System.Net;
using System.Text;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Hooks;

/// <summary>
/// Local HTTP endpoint that Claude Code (type "http" hook) and Codex (command hook via hooks/codex-hook.sh) post
/// their hook events to. A PermissionRequest is held open until the deck answers it or the hold time passes.
///   POST /hooks/claude, POST /hooks/codex          hook payloads
///   GET  /pending                                   pending approvals (diagnostics)
///   POST|GET /approve/{id}, /deny/{id}, /release/{id}   scriptable decisions
/// </summary>
public sealed class HookServer : IDisposable
{
    private readonly ApprovalRegistry _approvals;
    private readonly CancellationTokenSource _cts = new();
    private HttpListener? _listener;

    public int Port { get; private set; }
    public Func<TimeSpan> HoldTime { get; set; } = () => TimeSpan.FromSeconds(30);
    /// <summary>Optional check: return true to skip the hold (e.g. the agent's window is already focused).</summary>
    public Func<PendingApproval, Task<bool>>? SkipHold { get; set; }
    public event Action? Activity;

    public HookServer(ApprovalRegistry approvals) { _approvals = approvals; }

    public void Start(int port)
    {
        Port = port;
        _ = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
                _listener.Start();
                Log.Info($"Hook server listening on http://127.0.0.1:{Port}/");
                while (!_cts.IsCancellationRequested)
                {
                    var ctx = await _listener.GetContextAsync();
                    _ = Task.Run(() => HandleAsync(ctx));
                }
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                Log.Warn($"Hook server error ({ex.Message}); retrying in 30 s");
                try { _listener?.Close(); } catch { }
                await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ContinueWith(_ => { });
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var req = ctx.Request; var res = ctx.Response;
        try
        {
            var path = req.Url?.AbsolutePath ?? "/";
            string body;
            using (var sr = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8)) body = await sr.ReadToEndAsync();

            if (req.HttpMethod == "GET" && path == "/pending")
            {
                await WriteJsonAsync(res, 200, _approvals.Pending.Select(p => new { p.Id, provider = p.Provider.ToString(), p.AgentKey, p.ToolName, p.Summary, p.Cwd, p.ReceivedAt }));
                return;
            }
            if (req.HttpMethod == "GET" && path == "/health") { await WriteJsonAsync(res, 200, new { ok = true, port = Port }); return; }
            if (req.HttpMethod is "POST" or "GET" && (path.StartsWith("/approve/") || path.StartsWith("/deny/") || path.StartsWith("/release/")))
            {
                var id = path[(path.IndexOf('/', 1) + 1)..];
                var outcome = path.StartsWith("/approve/") ? ApprovalOutcome.Allow : path.StartsWith("/deny/") ? ApprovalOutcome.Deny : ApprovalOutcome.Release;
                var ok = _approvals.Resolve(id, outcome, outcome == ApprovalOutcome.Deny ? "Denied from the deck" : null);
                await WriteJsonAsync(res, ok ? 200 : 404, new { ok });
                return;
            }
            if (req.HttpMethod == "POST" && (path == "/hooks/claude" || path == "/hooks/codex"))
            {
                var provider = path.EndsWith("claude") ? Provider.Claude : Provider.Codex;
                await HandleHookAsync(provider, body, res);
                return;
            }
            res.StatusCode = 404; res.Close();
        }
        catch (Exception ex)
        {
            Log.Warn($"hook request failed: {ex.Message}");
            try { res.StatusCode = 500; res.Close(); } catch { }
        }
    }

    private async Task HandleHookAsync(Provider provider, string body, HttpListenerResponse res)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body).RootElement.Clone(); }
        catch (JsonException) { res.StatusCode = 400; res.Close(); return; }

        var eventName = root.Str("hook_event_name") ?? "";
        Activity?.Invoke();
        if (eventName != "PermissionRequest")
        {
            Log.Debug($"hook {provider} {eventName}");
            res.StatusCode = 200; res.Close();   // status events: acknowledged, the collectors pick the state up
            return;
        }

        var sessionId = root.Str("session_id") ?? root.Str("thread_id") ?? "";
        var tool = root.Str("tool_name") ?? "tool";
        var input = root.Prop("tool_input") ?? default;

        // interactive tools (a question's options, a plan review) can only be answered in the
        // terminal — approve/deny is meaningless and holding them just delays the prompt; the
        // session's waiting state alerts the deck instead
        if (tool is "AskUserQuestion" or "ExitPlanMode" or "EnterPlanMode")
        {
            Log.Info($"{tool} for {provider}:{sessionId} handed straight to the terminal (interactive)");
            res.StatusCode = 200; res.Close();
            return;
        }
        var pending = new PendingApproval
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Provider = provider,
            AgentKey = $"{(provider == Provider.Claude ? "claude" : "codex")}:{sessionId}",
            SessionId = sessionId,
            Cwd = root.Str("cwd") ?? "",
            ToolName = tool,
            ToolInput = input,
            Summary = PendingApproval.Summarize(tool, input),
        };

        if (SkipHold is not null && await SkipHold(pending))
        {
            Log.Info($"Approval for {pending.AgentKey} handed to the terminal (window focused)");
            res.StatusCode = 200; res.Close();
            return;
        }

        _approvals.Add(pending);
        ApprovalOutcome outcome;
        try
        {
            var hold = HoldTime();
            var done = await Task.WhenAny(pending.Outcome, Task.Delay(hold, _cts.Token));
            outcome = done == pending.Outcome ? pending.Outcome.Result : ApprovalOutcome.Release;
            if (outcome == ApprovalOutcome.Release && !pending.IsResolved)
            {
                _approvals.Resolve(pending, ApprovalOutcome.Release);
                Log.Info($"Approval for {pending.AgentKey} timed out after {hold.TotalSeconds:0}s → terminal");
            }
        }
        finally { _approvals.Remove(pending); }

        switch (outcome)
        {
            case ApprovalOutcome.Allow:
                await WriteJsonAsync(res, 200, new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = "allow" } } });
                break;
            case ApprovalOutcome.Deny:
                await WriteJsonAsync(res, 200, new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior = "deny", message = pending.Message ?? "Denied from the deck" } } });
                break;
            default:
                res.StatusCode = 200; res.Close();   // no decision → normal permission dialog
                break;
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, int status, object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(Json.Serialize(payload));
        res.StatusCode = status; res.ContentType = "application/json"; res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Close(); } catch { }
    }
}
