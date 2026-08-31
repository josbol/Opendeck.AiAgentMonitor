using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Collectors;

/// <summary>
/// Discovers Codex threads by tailing the rollout files under ~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl.
/// Per-turn state comes from task_started / task_complete / turn_aborted events, quota from the
/// rate_limits carried by token_count events, and liveness from the flock Codex holds on
/// ~/.codex/thread-writer-locks/&lt;thread&gt;.lock while a thread is loaded.
/// </summary>
public sealed class CodexRolloutCollector
{
    private readonly string _codexHome;
    private readonly string _sessionsDir;
    private readonly string _locksDir;
    private readonly Dictionary<string, Thread> _threads = new();   // by rollout path
    private DateTime _lastScan = DateTime.MinValue;

    /// <summary>Threads with no activity for longer than this are dropped (unless still locked).</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromHours(2);
    /// <summary>A turn that shows no writes for this long is assumed dead (process killed mid-turn).</summary>
    public TimeSpan StuckTurnTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public CodexRolloutCollector(string? codexHome = null)
    {
        _codexHome = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _sessionsDir = Path.Combine(_codexHome, "sessions");
        _locksDir = Path.Combine(_codexHome, "thread-writer-locks");
    }

    public string SessionsDir => _sessionsDir;
    public ProviderQuota? LatestQuota { get; private set; }

    internal sealed class Thread
    {
        public required string Path;
        public long Offset;
        public string? Id;
        public string Cwd = "";
        public string Originator = "";
        public string? ParentId;
        public bool IsSubagent;
        public string? Model;
        public string? Title;
        public DateTimeOffset StartedAt;
        public DateTimeOffset LastActivity;
        public DateTimeOffset StateSince;
        public AgentState State = AgentState.Idle;
        public string? Detail;
        public long? ContextTokens;
        public long? ContextWindow;
        public DateTimeOffset LastRateLimitsAt;
        public JsonElement? RateLimits;
        public long ContextWindowFromTask;
        public string Partial = "";
        /// <summary>Saw a task_started that is not an external-agent import (the desktop app mirrors Claude transcripts as threads).</summary>
        public bool HasRealTurn;
    }

    public IReadOnlyList<AgentInfo> Collect(DateTimeOffset now)
    {
        ScanFiles(now);
        foreach (var t in _threads.Values) Tail(t);

        // subagent counts by parent id
        var subCounts = new Dictionary<string, int>();
        foreach (var t in _threads.Values)
        {
            if (!t.IsSubagent || t.ParentId is null) continue;
            if (now - t.LastActivity > TimeSpan.FromMinutes(15)) continue;
            subCounts[t.ParentId] = subCounts.GetValueOrDefault(t.ParentId) + 1;
        }

        ScanLockOwners();
        var anyCodexProcess = ProcUtil.FindByComm("codex").Count > 0;
        var result = new List<AgentInfo>();
        var stale = new List<string>();
        foreach (var (path, t) in _threads)
        {
            if (t.Id is null) continue;
            var locked = IsLocked(t.Id);
            var owner = LockOwner(t.Id);
            var age = now - t.LastActivity;
            if (t.IsSubagent) { if (age > IdleTimeout) stale.Add(path); continue; }
            if (!t.HasRealTurn) { if (age > IdleTimeout) stale.Add(path); continue; }   // imported / never-run thread

            var state = t.State;
            if (state == AgentState.Working && age > StuckTurnTimeout) state = AgentState.Idle;
            if (!anyCodexProcess) state = AgentState.Ended;
            else if (!locked && Directory.Exists(_locksDir)) state = AgentState.Ended;    // Codex ≥ 0.147 locks every loaded thread: no lock → closed/unloaded
            else if (!locked && age > IdleTimeout) state = AgentState.Ended;              // older Codex without lock files: fall back to inactivity

            if (state == AgentState.Ended) { if (age > IdleTimeout + TimeSpan.FromHours(1)) stale.Add(path); continue; }

            result.Add(new AgentInfo
            {
                Key = $"codex:{t.Id}",
                Provider = Provider.Codex,
                Name = t.Title ?? System.IO.Path.GetFileName(t.Cwd.TrimEnd('/')),
                Cwd = t.Cwd,
                Host = owner is { } op ? HostFor(op, t.Originator) : HostFromOriginator(t.Originator),
                State = state,
                StateSince = t.StateSince,
                LastActivity = t.LastActivity,
                StartedAt = t.StartedAt,
                Model = t.Model,
                ContextTokens = t.ContextTokens,
                ContextPct = t.ContextTokens is not null && (t.ContextWindow ?? t.ContextWindowFromTask) > 0 ? Math.Clamp(100.0 * t.ContextTokens.Value / (t.ContextWindow ?? t.ContextWindowFromTask), 0, 100) : null,
                Detail = t.Detail,
                Title = t.Title,
                SessionId = t.Id,
                Pid = owner,
                SubAgents = subCounts.GetValueOrDefault(t.Id),
            });
        }
        foreach (var s in stale) _threads.Remove(s);

        // quota: most recent rate_limits across threads
        var best = _threads.Values.Where(t => t.RateLimits is not null).OrderByDescending(t => t.LastRateLimitsAt).FirstOrDefault();
        if (best?.RateLimits is not null) LatestQuota = ParseRateLimits(best.RateLimits.Value, best.LastRateLimitsAt);

        return result;
    }

    private readonly Dictionary<int, string> _hostByPid = new();
    private string HostFor(int pid, string originator)
    {
        if (_hostByPid.TryGetValue(pid, out var h)) return h;
        h = ProcUtil.DetectHost(pid);
        if (h == "Term") h = HostFromOriginator(originator) is var o && o != "Codex" ? o : h;
        return _hostByPid[pid] = h;
    }

    private static string HostFromOriginator(string o)
    {
        if (o.Contains("Rider", StringComparison.OrdinalIgnoreCase)) return "Rider";
        if (o.Contains("JetBrains", StringComparison.OrdinalIgnoreCase)) return "IDE";
        if (o.Contains("Desktop", StringComparison.OrdinalIgnoreCase)) return "App";
        if (o.Contains("vscode", StringComparison.OrdinalIgnoreCase)) return "VS Code";
        if (o.Contains("cli", StringComparison.OrdinalIgnoreCase)) return "Term";
        return o.Length == 0 ? "Codex" : o;
    }

    private void ScanFiles(DateTimeOffset now)
    {
        if ((DateTime.UtcNow - _lastScan).TotalSeconds < 5 && _threads.Count > 0) return;
        _lastScan = DateTime.UtcNow;
        if (!Directory.Exists(_sessionsDir)) return;
        var cutoff = now - IdleTimeout - TimeSpan.FromHours(1);
        // Only the last few day directories can hold recent files; desktop-app collab threads
        // stay loaded (and written) for days, so look well past the idle cutoff.
        for (var d = 0; d < 8; d++)
        {
            var day = now.AddDays(-d);
            var dir = Path.Combine(_sessionsDir, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "rollout-*.jsonl"))
            {
                if (_threads.ContainsKey(f)) continue;
                DateTimeOffset mtime;
                try { mtime = File.GetLastWriteTimeUtc(f); } catch { continue; }
                if (mtime < cutoff) continue;
                _threads[f] = new Thread { Path = f, LastActivity = mtime, StartedAt = mtime, StateSince = mtime };
            }
        }
    }

    private void Tail(Thread t)
    {
        try
        {
            using var fs = new FileStream(t.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < t.Offset) { t.Offset = 0; t.Partial = ""; }
            if (fs.Length == t.Offset) return;
            fs.Seek(t.Offset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs);
            var text = t.Partial + sr.ReadToEnd();
            t.Offset = fs.Length;
            var lines = text.Split('\n');
            t.Partial = text.EndsWith('\n') ? "" : lines[^1];
            var count = text.EndsWith('\n') ? lines.Length : lines.Length - 1;
            for (var i = 0; i < count; i++)
            {
                var line = lines[i];
                if (line.Length < 2) continue;
                try { Apply(t, line); } catch (Exception ex) { Log.Debug($"codex line: {ex.Message}"); }
            }
        }
        catch (FileNotFoundException) { }
        catch (Exception ex) { Log.Debug($"codex tail {t.Path}: {ex.Message}"); }
    }

    internal static void Apply(Thread t, string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.Str("type");
        var ts = ClaudeUsageClient.ParseResets(root.Prop("timestamp")) ?? t.LastActivity;
        var payload = root.Obj("payload");

        switch (type)
        {
            case "session_meta":
                if (payload is null) break;
                t.Id = payload.Value.Str("id") ?? payload.Value.Str("session_id");
                t.Cwd = payload.Value.Str("cwd") ?? t.Cwd;
                t.Originator = payload.Value.Str("originator") ?? "";
                t.StartedAt = ClaudeUsageClient.ParseResets(payload.Value.Prop("timestamp")) ?? ts;
                if (payload.Value.Prop("source") is { ValueKind: JsonValueKind.Object } src && src.Obj("subagent") is { } sub)
                {
                    t.IsSubagent = true;
                    var spawn = sub.Obj("thread_spawn") ?? sub;
                    t.ParentId = spawn.Str("parent_thread_id");
                }
                t.LastActivity = ts;
                break;

            case "turn_context":
                if (payload is null) break;
                t.Model = payload.Value.Str("model") ?? t.Model;
                t.Cwd = payload.Value.Str("cwd") ?? t.Cwd;
                break;

            case "event_msg":
                if (payload is null) break;
                var ev = payload.Value.Str("type");
                switch (ev)
                {
                    case "task_started":
                        if (payload.Value.Str("turn_id") is { } turnId && turnId.StartsWith("external-import", StringComparison.Ordinal)) break;
                        t.HasRealTurn = true;
                        t.State = AgentState.Working; t.StateSince = ts; t.Detail = null; t.LastActivity = ts;
                        t.ContextWindowFromTask = payload.Value.Long("model_context_window") ?? t.ContextWindowFromTask;
                        break;
                    case "task_complete":
                        // a failed turn is also a task_complete, with terminal error details attached
                        // (EventMsg::Error itself is never persisted to the rollout)
                        if (payload.Value.Obj("error") is { } err)
                        {
                            t.State = AgentState.Error;
                            t.Detail = err.Str("message") ?? "turn failed";
                        }
                        else { t.State = AgentState.Idle; t.Detail = null; }
                        t.StateSince = ts; t.LastActivity = ts; break;
                    case "turn_aborted":
                        t.State = AgentState.Idle; t.StateSince = ts; t.Detail = "aborted"; t.LastActivity = ts; break;
                    case "user_message":
                        t.LastActivity = ts;
                        if (t.Title is null)
                        {
                            var m = payload.Value.Str("message");
                            if (!string.IsNullOrWhiteSpace(m)) t.Title = Shorten(m, 24);
                        }
                        if (t.State is AgentState.Waiting or AgentState.Error) { t.State = AgentState.Working; t.StateSince = ts; t.Detail = null; }
                        break;
                    case "token_count":
                        t.LastActivity = ts;
                        if (t.Detail == "compacted") t.Detail = null;
                        var info = payload.Value.Obj("info");
                        if (info is not null)
                        {
                            var last = info.Value.Obj("last_token_usage") ?? info.Value.Obj("total_token_usage");
                            if (last is not null)
                                t.ContextTokens = (last.Value.Long("input_tokens") ?? 0) + (last.Value.Long("output_tokens") ?? 0);
                            t.ContextWindow = info.Value.Long("model_context_window") ?? t.ContextWindow;
                        }
                        if (payload.Value.Obj("rate_limits") is { } rl)
                        {
                            t.RateLimits = rl.Clone();
                            t.LastRateLimitsAt = ts;
                        }
                        break;
                    case "context_compacted":
                        t.Detail = "compacted"; t.LastActivity = ts; break;
                    case "agent_message":
                        if (t.Detail == "compacted") t.Detail = null;
                        t.LastActivity = ts; break;
                    case "agent_reasoning":
                    case "patch_apply_end":
                    case "mcp_tool_call_end":
                    case "web_search_end":
                    case "sub_agent_activity":
                    case "item_completed":
                        t.LastActivity = ts; break;
                    default:
                        if (ev is not null && (ev.Contains("approval_request") || ev.Contains("request_user_input") || ev.Contains("elicitation")))
                        {
                            t.State = AgentState.Waiting; t.StateSince = ts; t.LastActivity = ts;
                            t.Detail = ev.Contains("approval") ? "approval" : "input needed";
                        }
                        break;
                }
                break;

            case "response_item":
                t.LastActivity = ts;
                break;
        }
    }

    private static string Shorten(string s, int max)
    {
        s = s.Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "…";
    }

    // ---- quota --------------------------------------------------------------------------

    internal static ProviderQuota ParseRateLimits(JsonElement rl, DateTimeOffset at)
    {
        var windows = new List<QuotaWindow>();
        void Add(JsonElement? w)
        {
            if (w is null || w.Value.ValueKind != JsonValueKind.Object) return;
            var pct = w.Value.Dbl("used_percent") ?? w.Value.Dbl("usedPercent");
            if (pct is null) return;
            var minutes = w.Value.Long("window_minutes") ?? w.Value.Long("windowDurationMins") ?? 0;
            var label = minutes switch { >= 10000 => "7d", >= 240 and <= 360 => "5h", > 0 => $"{minutes / 60}h", _ => "?" };
            DateTimeOffset? resets = null;
            if (w.Value.Long("resets_at") is { } ra) resets = DateTimeOffset.FromUnixTimeSeconds(ra);
            else if (w.Value.Long("resets_in_seconds") is { } ris) resets = at.AddSeconds(ris);
            windows.Add(new QuotaWindow(label, pct.Value, resets));
        }
        Add(rl.Obj("primary"));
        Add(rl.Obj("secondary"));
        return new ProviderQuota { Provider = Provider.Codex, Windows = windows, FetchedAt = at, Plan = rl.Str("plan_type"), Source = "rollout" };
    }

    // ---- liveness: which codex process holds the thread's writer lock ------------------------
    // Codex keeps ~/.codex/thread-writer-locks/<thread>.lock open (flock) while a thread is loaded,
    // so the owner shows up in /proc/<pid>/fd. Refreshed once per Collect.

    private Dictionary<string, int> _lockOwners = new();
    private DateTime _lockScanAt = DateTime.MinValue;

    private void ScanLockOwners()
    {
        if ((DateTime.UtcNow - _lockScanAt).TotalSeconds < 2) return;
        _lockScanAt = DateTime.UtcNow;
        var owners = new Dictionary<string, int>();
        foreach (var pid in ProcUtil.FindByComm("codex", "codex-cli", "codex-app-server"))
        {
            IEnumerable<string> fds;
            try { fds = Directory.EnumerateFiles($"/proc/{pid}/fd"); } catch { continue; }
            foreach (var fd in fds)
            {
                string? target;
                try { target = new FileInfo(fd).LinkTarget; } catch { continue; }
                if (target is null || !target.Contains("thread-writer-locks", StringComparison.Ordinal) || !target.EndsWith(".lock", StringComparison.Ordinal)) continue;
                var id = System.IO.Path.GetFileNameWithoutExtension(target);
                owners.TryAdd(id, pid);
            }
        }
        _lockOwners = owners;
    }

    private int? LockOwner(string threadId) => _lockOwners.TryGetValue(threadId, out var pid) ? pid : null;
    private bool IsLocked(string threadId) => _lockOwners.ContainsKey(threadId);
}
