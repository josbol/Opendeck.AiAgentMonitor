using System.Net;
using System.Text;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Collectors;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Hooks;

/// <summary>
/// Local HTTP endpoint that Claude Code (type "http" hook), Codex (command hook via hooks/codex-hook.sh) and the
/// Copilot CLI (command hook via hooks/copilot-hook.sh) post their permission requests to. A request is held open
/// until the deck answers it or the hold time passes.
///   POST /hooks/claude, /hooks/codex, /hooks/copilot   hook payloads
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
    /// <summary>Optional check: return a reason to answer immediately with "no decision" instead of holding
    /// (e.g. the agent's window is already focused), or null to hold the request as usual.</summary>
    public Func<PendingApproval, Task<string?>>? SkipHold { get; set; }
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
            if (req.HttpMethod == "POST" && path is "/hooks/claude" or "/hooks/codex" or "/hooks/copilot")
            {
                var provider = path.EndsWith("claude") ? Provider.Claude : path.EndsWith("codex") ? Provider.Codex : Provider.Copilot;
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

        var eventName = root.Str("hook_event_name") ?? root.Str("hookEventName") ?? "";
        Activity?.Invoke();
        // Copilot's permissionRequest payload may carry no event name; only that hook posts to /hooks/copilot
        var isPermission = eventName.Equals("PermissionRequest", StringComparison.OrdinalIgnoreCase) || (provider == Provider.Copilot && eventName.Length == 0);
        if (!isPermission)
        {
            Log.Debug($"hook {provider} {eventName}");
            res.StatusCode = 200; res.Close();   // status events: acknowledged, the collectors pick the state up
            return;
        }

        var pending = ParseRequest(provider, root, out var passThrough);
        if (passThrough is not null)
        {
            Log.Info($"{pending.ToolName} for {pending.AgentKey} handed straight to the app ({passThrough})");
            res.StatusCode = 200; res.Close();
            return;
        }

        if (SkipHold is not null && await SkipHold(pending) is { } why)
        {
            Log.Info($"Approval for {pending.AgentKey} not held: {why}");
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
                await WriteJsonAsync(res, 200, Decision(provider, "allow", null));
                break;
            case ApprovalOutcome.Deny:
                await WriteJsonAsync(res, 200, Decision(provider, "deny", pending.Message ?? "Denied from the deck"));
                break;
            default:
                res.StatusCode = 200; res.Close();   // no decision → normal permission dialog
                break;
        }
    }

    /// <summary>
    /// Builds the approval from a hook payload. <paramref name="passThrough"/> names the reason a request is answered
    /// with "no decision" right away instead of being held: interactive tools (a question's options, a plan review)
    /// can only be answered in the app, and Copilot's read requests are auto-allowed inside the workspace anyway
    /// (its permissionRequest hook fires before any rule check).
    /// </summary>
    internal static PendingApproval ParseRequest(Provider provider, JsonElement root, out string? passThrough)
    {
        passThrough = null;
        string sessionId, tool, cwd; JsonElement input;
        if (provider == Provider.Copilot)
        {
            var perm = root.Obj("permissionRequest") ?? root.Obj("toolInput") ?? root.Obj("toolArgs") ?? root.Obj("tool_input");
            var kind = root.Str("kind") ?? perm?.Str("kind");
            sessionId = root.Str("sessionId") ?? root.Str("session_id") ?? "";
            cwd = root.Str("cwd") ?? "";
            tool = root.Str("toolName") ?? root.Str("tool_name") ?? kind ?? "tool";
            input = perm ?? root;
            if (kind == "read" || tool is "read" or "view" or "grep" or "glob") passThrough = "read access, auto-allowed in the workspace";
            else if (CopilotSessionCollector.IsQuestionTool(tool)) passThrough = "interactive";
        }
        else
        {
            sessionId = root.Str("session_id") ?? root.Str("thread_id") ?? "";
            cwd = root.Str("cwd") ?? "";
            tool = root.Str("tool_name") ?? "tool";
            input = root.Prop("tool_input") ?? default;
            if (tool is "AskUserQuestion" or "ExitPlanMode" or "EnterPlanMode") passThrough = "interactive";
        }
        return new PendingApproval
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Provider = provider,
            AgentKey = $"{ProviderInfo.KeyPrefix(provider)}:{sessionId}",
            SessionId = sessionId,
            Cwd = cwd,
            ToolName = tool,
            ToolInput = input,
            Summary = PendingApproval.Summarize(tool, input),
        };
    }

    /// <summary>Claude and Codex share the hookSpecificOutput envelope; Copilot's permissionRequest hook answers with a bare {behavior, message}.</summary>
    internal static object Decision(Provider provider, string behavior, string? message)
        => provider == Provider.Copilot
            ? new { behavior, message }
            : new { hookSpecificOutput = new { hookEventName = "PermissionRequest", decision = new { behavior, message } } };

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
