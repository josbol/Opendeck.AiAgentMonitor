using System.Globalization;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Collectors;

/// <summary>
/// Discovers GitHub Copilot sessions — the Copilot CLI in a terminal, and the JetBrains plugin's chat, which runs the
/// same engine (copilot-language-server --headless under the IDE) — from ~/.copilot/session-state/&lt;session&gt;/:
///   workspace.yaml      cwd, client_name (github/cli | copilot-intellij), name (the first prompt)
///   inuse.&lt;pid&gt;.lock   pid of the process that has the session open (a plain pid file, gone when it exits)
///   events.jsonl        assistant.turn_start / turn_end, permission.requested / completed, tool.execution_*, …
/// State comes from tailing events.jsonl, liveness from the lock's pid (/proc). Copilot does not persist its
/// context-window use, so the context bar stays empty.
/// </summary>
public sealed class CopilotSessionCollector
{
    private readonly string _home;
    private readonly string _sessionsDir;
    private readonly Dictionary<string, Session> _sessions = new();   // by directory
    private readonly Dictionary<int, string> _hostByPid = new();
    private DateTime _lastScan = DateTime.MinValue;

    /// <summary>A turn that shows no writes for this long is assumed dead (process killed mid-turn).</summary>
    public TimeSpan StuckTurnTimeout { get; set; } = TimeSpan.FromMinutes(30);
    /// <summary>Whether the pid from an inuse lock is a live Copilot process (overridable for tests).</summary>
    public Func<int, bool> IsCopilotProcess { get; set; } = DefaultIsCopilotProcess;
    /// <summary>Host classification of a live pid (overridable for tests).</summary>
    public Func<int, string> DetectHost { get; set; } = ProcUtil.DetectHost;

    public CopilotSessionCollector(string? copilotHome = null)
    {
        _home = copilotHome ?? Environment.GetEnvironmentVariable("COPILOT_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");
        _sessionsDir = Path.Combine(_home, "session-state");
    }

    public string SessionsDir => _sessionsDir;

    internal sealed class Session
    {
        public required string Dir;
        public required string Id;
        public long Offset;
        public string Partial = "";
        public bool SkipPartialFirst;
        public string Cwd = "";
        public string Client = "";
        public string? Name;
        public string? Title;
        public string? Model;
        public long? ContextTokens;          // prompt size of the last model call (usage checkpoint); the window itself is not recorded
        public AgentState State = AgentState.Idle;
        public string? Detail;
        public DateTimeOffset StartedAt, StateSince, LastActivity;
        public readonly Dictionary<string, string> OpenPermissions = new();   // requestId → summary
        public string? OpenQuestion;         // toolCallId of an ask_user call the user has not answered yet
        public bool ShutDown;
        public DateTime WorkspaceReadAt = DateTime.MinValue;
        public int Pid;
    }

    /// <summary>The CLI names its main thread, so comm is useless; the command line always contains "copilot".</summary>
    private static bool DefaultIsCopilotProcess(int pid)
        => ProcUtil.IsAlive(pid) && (ProcUtil.CmdLine(pid) ?? "").Contains("copilot", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<AgentInfo> Collect(DateTimeOffset now)
    {
        ScanDirs();
        var result = new List<AgentInfo>();
        var stale = new List<string>();
        foreach (var (dir, s) in _sessions)
        {
            var pid = LockPid(dir);
            if (pid <= 0 || !IsCopilotProcess(pid)) { stale.Add(dir); continue; }   // closed, or a lock left behind by a crash
            s.Pid = pid;
            ReadWorkspace(s);
            Tail(s);
            if (s.ShutDown) continue;

            var state = s.State;
            if (state == AgentState.Working && now - s.LastActivity > StuckTurnTimeout) state = AgentState.Idle;
            result.Add(new AgentInfo
            {
                Key = $"copilot:{s.Id}",
                Provider = Provider.Copilot,
                Name = Path.GetFileName(s.Cwd.TrimEnd('/')) is { Length: > 0 } n ? n : s.Id[..Math.Min(8, s.Id.Length)],
                Cwd = s.Cwd,
                Host = HostFor(s),
                State = state,
                StateSince = s.StateSince,
                LastActivity = s.LastActivity,
                StartedAt = s.StartedAt,
                Model = s.Model,
                ContextTokens = s.ContextTokens,
                Detail = state == s.State ? s.Detail : null,
                Title = s.Title ?? (string.IsNullOrWhiteSpace(s.Name) ? null : Shorten(s.Name, 40)),
                Pid = pid,
                SessionId = s.Id,
            });
        }
        foreach (var d in stale) _sessions.Remove(d);

        // one agent per process: the JetBrains engine keeps earlier sessions of the same chat open too — show the newest
        return result
            .GroupBy(a => a.Pid)
            .Select(g => g.OrderByDescending(a => a.LastActivity).ThenByDescending(a => a.StartedAt).First())
            .ToList();
    }

    private string HostFor(Session s)
    {
        if (!_hostByPid.TryGetValue(s.Pid, out var h))
        {
            h = DetectHost(s.Pid);
            _hostByPid[s.Pid] = h;
        }
        // the engine the JetBrains plugin spawns normally sits under the IDE process; fall back on the client name
        if (h == "Term" && s.Client.Contains("intellij", StringComparison.OrdinalIgnoreCase)) h = "Rider";
        return h;
    }

    private void ScanDirs()
    {
        if ((DateTime.UtcNow - _lastScan).TotalSeconds < 5 && _sessions.Count > 0) return;
        _lastScan = DateTime.UtcNow;
        if (!Directory.Exists(_sessionsDir)) return;
        IEnumerable<string> dirs;
        try { dirs = Directory.EnumerateDirectories(_sessionsDir); } catch { return; }
        foreach (var dir in dirs)
        {
            if (_sessions.ContainsKey(dir)) continue;
            if (LockPid(dir) <= 0) continue;   // nobody has it open
            DateTimeOffset mtime;
            try { mtime = Directory.GetLastWriteTimeUtc(dir); } catch { continue; }
            _sessions[dir] = new Session { Dir = dir, Id = Path.GetFileName(dir), StartedAt = mtime, StateSince = mtime, LastActivity = mtime };
        }
    }

    /// <summary>The pid in inuse.&lt;pid&gt;.lock, or 0 when the session is not open.</summary>
    internal static int LockPid(string dir)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "inuse.*.lock"))
            {
                var name = Path.GetFileName(f);
                if (int.TryParse(name.AsSpan(6, name.Length - 11), out var pid) && pid > 0) return pid;
            }
        }
        catch { }
        return 0;
    }

    // ---- workspace.yaml ---------------------------------------------------------------------

    private static void ReadWorkspace(Session s)
    {
        var path = Path.Combine(s.Dir, "workspace.yaml");
        try
        {
            if (!File.Exists(path)) return;
            var mtime = File.GetLastWriteTimeUtc(path);
            if (mtime == s.WorkspaceReadAt) return;
            s.WorkspaceReadAt = mtime;
            var y = ParseYaml(File.ReadAllText(path));
            if (y.TryGetValue("cwd", out var cwd) && cwd.Length > 0) s.Cwd = cwd;
            if (y.TryGetValue("client_name", out var client)) s.Client = client;
            if (y.TryGetValue("name", out var name) && name.Length > 0) s.Name = name;
            if (y.TryGetValue("created_at", out var created) && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c)) s.StartedAt = c;
        }
        catch (Exception ex) { Log.Debug($"copilot workspace {path}: {ex.Message}"); }
    }

    /// <summary>Flat "key: value" YAML as Copilot writes it (double-quoted values may carry JSON-style escapes).</summary>
    internal static Dictionary<string, string> ParseYaml(string text)
    {
        var d = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            if (raw.Length == 0 || raw[0] is ' ' or '#' or '-') continue;
            var i = raw.IndexOf(':');
            if (i <= 0) continue;
            var key = raw[..i].Trim();
            var val = raw[(i + 1)..].Trim();
            if (val.Length >= 2 && val[0] == '"' && val[^1] == '"') val = Unescape(val[1..^1]);
            else if (val.Length >= 2 && val[0] == '\'' && val[^1] == '\'') val = val[1..^1].Replace("''", "'");
            d[key] = val;
        }
        return d;
    }

    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            i++;
            sb.Append(s[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', '"' => '"', '\\' => '\\', var c => c });
        }
        return sb.ToString();
    }

    // ---- events.jsonl ------------------------------------------------------------------------

    private void Tail(Session s)
    {
        var path = Path.Combine(s.Dir, "events.jsonl");
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < s.Offset) { s.Offset = 0; s.Partial = ""; }
            if (fs.Length == s.Offset) return;
            // a long-running session's log can be tens of MB of tool output; the recent tail is all the state needs
            const long window = 1024 * 1024;
            if (s.Offset == 0 && fs.Length > window) { s.Offset = fs.Length - window; s.SkipPartialFirst = true; }
            fs.Seek(s.Offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var text = s.Partial + sr.ReadToEnd();
            s.Offset = fs.Length;
            var lines = text.Split('\n');
            s.Partial = text.EndsWith('\n') ? "" : lines[^1];
            var count = text.EndsWith('\n') ? lines.Length : lines.Length - 1;
            var start = 0;
            if (s.SkipPartialFirst) { start = 1; s.SkipPartialFirst = false; }
            for (var i = start; i < count; i++)
            {
                var line = lines[i];
                if (line.Length < 2) continue;
                try { Apply(s, line); } catch (Exception ex) { Log.Debug($"copilot line: {ex.Message}"); }
            }
        }
        catch (FileNotFoundException) { }
        catch (Exception ex) { Log.Debug($"copilot tail {path}: {ex.Message}"); }
    }

    internal static void Apply(Session s, string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.Str("type") ?? "";
        var ts = ClaudeUsageClient.ParseResets(root.Prop("timestamp")) ?? s.LastActivity;
        var data = root.Obj("data") ?? default;

        switch (type)
        {
            case "session.start":
                s.StartedAt = ClaudeUsageClient.ParseResets(data.Prop("startTime")) ?? ts;
                if (data.Str("selectedModel") is { Length: > 0 } sel && sel != "auto") s.Model ??= sel;
                if (s.Cwd.Length == 0 && data.Obj("context")?.Str("cwd") is { Length: > 0 } cwd) s.Cwd = cwd;
                s.ShutDown = false;
                s.LastActivity = ts;
                break;

            case "user.message":
                // the prompt is in; the turn_start follows within milliseconds
                s.Title ??= data.Str("content") is { Length: > 0 } content ? Shorten(content, 40) : null;
                s.OpenPermissions.Clear(); s.OpenQuestion = null;
                s.State = AgentState.Working; s.StateSince = ts; s.Detail = null; s.LastActivity = ts;
                break;

            case "assistant.turn_start":
                s.OpenPermissions.Clear(); s.OpenQuestion = null;
                s.State = AgentState.Working; s.StateSince = ts; s.Detail = null; s.LastActivity = ts;
                break;

            case "assistant.turn_end":
            {
                s.OpenPermissions.Clear(); s.OpenQuestion = null;
                var status = data.Str("status") ?? data.Str("turnStatus") ?? "";
                var error = data.Obj("error") is { ValueKind: JsonValueKind.Object } eo ? eo.Str("message") : data.Str("error") ?? data.Str("errorMessage");
                if (error is not null || status is "error" or "failed")
                {
                    s.State = AgentState.Error; s.Detail = error ?? "turn failed";
                }
                else
                {
                    s.State = AgentState.Idle; s.Detail = status == "cancelled" ? "cancelled" : null;
                }
                s.StateSince = ts; s.LastActivity = ts;
                break;
            }

            case "assistant.message":
                s.Model = data.Str("model") ?? s.Model;
                s.LastActivity = ts;
                break;

            case "permission.requested":
            {
                var id = data.Str("requestId") ?? Guid.NewGuid().ToString("N");
                var summary = PermissionSummary(data);
                s.OpenPermissions[id] = summary;
                s.State = AgentState.Waiting; s.Detail = summary; s.StateSince = ts; s.LastActivity = ts;
                break;
            }

            case "permission.completed":
            {
                var id = data.Str("requestId") ?? "";
                s.OpenPermissions.Remove(id);
                s.LastActivity = ts;
                if (s.State == AgentState.Waiting && s.OpenPermissions.Count == 0 && s.OpenQuestion is null)
                {
                    s.State = AgentState.Working; s.StateSince = ts; s.Detail = null;
                }
                else if (s.OpenPermissions.Count > 0) s.Detail = s.OpenPermissions.Values.First();
                break;
            }

            case "tool.execution_start":
            {
                s.LastActivity = ts;
                var tool = data.Str("toolName") ?? "";
                if (IsQuestionTool(tool))
                {
                    s.OpenQuestion = data.Str("toolCallId") ?? tool;
                    s.State = AgentState.Waiting; s.Detail = "question"; s.StateSince = ts;
                }
                break;
            }

            case "tool.execution_complete":
                s.LastActivity = ts;
                if (s.OpenQuestion is not null && (data.Str("toolCallId") ?? "") == s.OpenQuestion)
                {
                    s.OpenQuestion = null;
                    if (s.OpenPermissions.Count == 0) { s.State = AgentState.Working; s.StateSince = ts; s.Detail = null; }
                }
                break;

            case "session.shutdown":
                s.ShutDown = true; s.LastActivity = ts;
                break;

            case "session.usage_checkpoint":
                // written after every turn (CLI ≥ 1.0.8x): promptCacheBreakState[].models[<model>].prompt_tokens is the
                // size of the last request on the main conversation — the context in use, minus the reply
                s.LastActivity = ts;
                if (PromptTokens(data, s.Model) is { } tokens) s.ContextTokens = tokens;
                break;

            case "hook.start":
                s.LastActivity = ts;
                if (data.Str("hookType") == "errorOccurred")
                {
                    var input = data.Obj("input");
                    var msg = input?.Obj("error") is { ValueKind: JsonValueKind.Object } e ? e.Str("message") : input?.Str("error") ?? input?.Str("message");
                    s.State = AgentState.Error; s.Detail = msg ?? "error"; s.StateSince = ts;
                }
                break;

            default:
                if (type.EndsWith(".error", StringComparison.Ordinal))
                {
                    var msg = data.Obj("error") is { ValueKind: JsonValueKind.Object } e ? e.Str("message") : data.Str("error") ?? data.Str("message");
                    s.State = AgentState.Error; s.Detail = msg ?? "error"; s.StateSince = ts;
                }
                s.LastActivity = ts;
                break;
        }
    }

    /// <summary>The main conversation's last prompt size from a usage checkpoint: the current model's entry, else the largest.</summary>
    internal static long? PromptTokens(JsonElement data, string? model)
    {
        if (data.Obj("promptCacheBreakState") is not { ValueKind: JsonValueKind.Array } convs) return null;
        long? best = null;
        foreach (var conv in convs.EnumerateArray())
        {
            if (conv.Str("conversation") is { } name && name != "main") continue;
            if (conv.Obj("models") is not { ValueKind: JsonValueKind.Object } models) continue;
            foreach (var m in models.EnumerateObject())
            {
                var tokens = m.Value.Long("prompt_tokens");
                if (tokens is null) continue;
                if (model is not null && (m.Name == model || m.Value.Str("model") == model)) return tokens;
                best = Math.Max(best ?? 0, tokens.Value);
            }
        }
        return best;
    }

    internal static bool IsQuestionTool(string tool) => tool is "ask_user" or "ask_user_question" or "askUser" or "AskUserQuestion";

    /// <summary>"shell: git push origin main", "write: Program.cs", "read: Search in directory: …" from a permission.requested record.</summary>
    internal static string PermissionSummary(JsonElement data)
    {
        var req = data.Obj("permissionRequest") ?? data.Obj("promptRequest") ?? data;
        var kind = req.Str("kind") ?? data.Str("kind") ?? "permission";
        var what = req.Str("fullCommandText") ?? req.Str("command") ?? req.Str("intention") ?? req.Str("path") ?? req.Str("url") ?? req.Str("toolName");
        if (what is not null && kind is "write" or "read" && what.Contains('/') && !what.Contains(' ')) what = Path.GetFileName(what);
        if (what is null) return kind;
        what = what.Replace('\n', ' ').Trim();
        if (what.Length > 60) what = what[..60] + "…";
        return $"{kind}: {what}";
    }

    private static string Shorten(string s, int max)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }
}
