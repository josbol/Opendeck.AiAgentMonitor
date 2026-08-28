using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Collectors;

/// <summary>
/// Discovers live Claude Code sessions from the session registry that Claude Code maintains at
/// ~/.claude/sessions/&lt;pid&gt;.json (status: busy | idle | waiting, plus waitingFor when waiting).
/// Context usage and model come from the session transcript under ~/.claude/projects.
/// </summary>
public sealed class ClaudeSessionCollector
{
    private readonly string _claudeHome;
    private readonly string _sessionsDir;
    private readonly Dictionary<int, string> _hostCache = new();
    private readonly Dictionary<string, (long UpdatedAt, TranscriptInfo Info)> _transcriptCache = new();

    public long ContextWindowOverride { get; set; }   // 0 = auto

    public ClaudeSessionCollector(string? claudeHome = null)
    {
        _claudeHome = claudeHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _sessionsDir = Path.Combine(_claudeHome, "sessions");
    }

    public string SessionsDir => _sessionsDir;

    public IReadOnlyList<AgentInfo> Collect(DateTimeOffset now)
    {
        var result = new List<AgentInfo>();
        if (!Directory.Exists(_sessionsDir)) return result;
        string[] files;
        try { files = Directory.GetFiles(_sessionsDir, "*.json"); } catch { return result; }

        foreach (var file in files)
        {
            try
            {
                var info = ParseSession(file, now);
                if (info is not null) result.Add(info);
            }
            catch (Exception ex) { Log.Debug($"claude session {file}: {ex.Message}"); }
        }
        return result;
    }

    private AgentInfo? ParseSession(string file, DateTimeOffset now)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            root = doc.RootElement.Clone();
        }
        catch (JsonException) { return null; } // partially written; next tick

        var pid = (int)(root.Long("pid") ?? 0);
        var procStart = root.Str("procStart");
        if (pid <= 0 || !ProcUtil.IsAlive(pid, procStart)) return null; // stale registry entry

        var sessionId = root.Str("sessionId") ?? Path.GetFileNameWithoutExtension(file);
        var cwd = root.Str("cwd") ?? "";
        var status = root.Str("status") ?? "idle";
        var waitingFor = root.Str("waitingFor");
        var kind = root.Str("kind") ?? "interactive";
        var startedAt = Ms(root.Long("startedAt")) ?? now;
        var updatedAt = Ms(root.Long("updatedAt")) ?? startedAt;
        var statusUpdatedAt = Ms(root.Long("statusUpdatedAt")) ?? updatedAt;

        var state = status switch
        {
            "busy" or "shell" or "compacting" => AgentState.Working,
            "waiting" or "blocked" => AgentState.Waiting,
            "stopped" or "exited" => AgentState.Ended,
            _ => AgentState.Idle,
        };

        if (!_hostCache.TryGetValue(pid, out var host))
        {
            host = kind is "bg" or "daemon-worker" ? "BG" : ProcUtil.DetectHost(pid);
            _hostCache[pid] = host;
        }

        var transcript = ReadTranscript(sessionId, cwd, updatedAt.ToUnixTimeMilliseconds());

        return new AgentInfo
        {
            Key = $"claude:{sessionId}",
            Provider = Provider.Claude,
            Name = root.Str("name") ?? Path.GetFileName(cwd),
            Cwd = cwd,
            Host = host,
            State = state,
            StateSince = statusUpdatedAt,
            LastActivity = updatedAt,
            StartedAt = startedAt,
            Model = transcript?.Model,
            ContextTokens = transcript?.ContextTokens,
            ContextPct = transcript?.ContextPct,
            Detail = waitingFor ?? (status is "shell" ? "shell" : status is "compacting" ? "compacting" : null),
            Title = transcript?.Title,
            Pid = pid,
            SessionId = sessionId,
            Background = kind is not "interactive",
        };
    }

    private static DateTimeOffset? Ms(long? ms) => ms is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(ms.Value);

    // ---- transcript ---------------------------------------------------------------------

    private sealed record TranscriptInfo(string? Model, long? ContextTokens, double? ContextPct, string? Title);

    /// <summary>Claude Code stores transcripts at ~/.claude/projects/&lt;cwd with non-alnum → '-'&gt;/&lt;sessionId&gt;.jsonl</summary>
    public string TranscriptPath(string sessionId, string cwd)
    {
        var escaped = new string(cwd.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray());
        return Path.Combine(_claudeHome, "projects", escaped, sessionId + ".jsonl");
    }

    private TranscriptInfo? ReadTranscript(string sessionId, string cwd, long updatedAt)
    {
        if (_transcriptCache.TryGetValue(sessionId, out var cached) && cached.UpdatedAt == updatedAt) return cached.Info;
        var path = TranscriptPath(sessionId, cwd);
        if (!File.Exists(path)) return null;
        try
        {
            var info = ParseTranscriptTail(path);
            _transcriptCache[sessionId] = (updatedAt, info);
            return info;
        }
        catch (Exception ex) { Log.Debug($"transcript {path}: {ex.Message}"); return null; }
    }

    private TranscriptInfo ParseTranscriptTail(string path)
    {
        const int tail = 512 * 1024;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var len = fs.Length;
        var start = Math.Max(0, len - tail);
        fs.Seek(start, SeekOrigin.Begin);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        var first = start > 0; // first line may be partial
        while ((line = sr.ReadLine()) is not null)
        {
            if (first) { first = false; continue; }
            lines.Add(line);
        }

        string? model = null; long? ctx = null; string? title = null;
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var l = lines[i];
            if (ctx is null && l.Contains("\"usage\"", StringComparison.Ordinal) && l.Contains("\"assistant\"", StringComparison.Ordinal))
            {
                try
                {
                    using var doc = JsonDocument.Parse(l);
                    var msg = doc.RootElement.Obj("message");
                    var usage = msg?.Obj("usage");
                    if (usage is not null && doc.RootElement.Bool("isSidechain") != true)
                    {
                        var u = usage.Value;
                        ctx = (u.Long("input_tokens") ?? 0) + (u.Long("cache_creation_input_tokens") ?? 0) + (u.Long("cache_read_input_tokens") ?? 0);
                        model ??= msg?.Str("model");
                    }
                }
                catch { }
            }
            if (title is null && l.Contains("\"ai-title\"", StringComparison.Ordinal))
            {
                try { using var doc = JsonDocument.Parse(l); title = doc.RootElement.Str("title") ?? doc.RootElement.Str("aiTitle"); } catch { }
            }
            if (ctx is not null && title is not null) break;
        }

        var window = ContextWindowOverride > 0 ? ContextWindowOverride : GuessContextWindow(model);
        double? pct = ctx is null ? null : Math.Clamp(100.0 * ctx.Value / window, 0, 100);
        return new TranscriptInfo(model, ctx, pct, title);
    }

    private long _settingsWindowCache; private DateTime _settingsWindowAt;

    /// <summary>Claude Code 2.x defaults to 200k; a model configured as "...[1m]" in settings gets 1M.</summary>
    private long GuessContextWindow(string? model)
    {
        if ((DateTime.UtcNow - _settingsWindowAt).TotalMinutes > 5)
        {
            _settingsWindowAt = DateTime.UtcNow;
            _settingsWindowCache = 200_000;
            try
            {
                var settings = Path.Combine(_claudeHome, "settings.json");
                if (File.Exists(settings))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(settings));
                    var m = doc.RootElement.Str("model") ?? "";
                    if (m.Contains("[1m]", StringComparison.OrdinalIgnoreCase)) _settingsWindowCache = 1_000_000;
                }
            }
            catch { }
        }
        return _settingsWindowCache;
    }
}
