using System.Text.Json;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Agents;

public enum ApprovalOutcome { Allow, Deny, Release }

/// <summary>A PermissionRequest that a hook is holding open, waiting for a decision from the deck.</summary>
public sealed class PendingApproval
{
    private readonly TaskCompletionSource<ApprovalOutcome> _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public required string Id { get; init; }
    public required Provider Provider { get; init; }
    public required string AgentKey { get; init; }      // provider:sessionId
    public required string SessionId { get; init; }
    public required string Cwd { get; init; }
    public required string ToolName { get; init; }
    public required string Summary { get; init; }        // one-line description of what is being asked
    public JsonElement ToolInput { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
    public string? Message { get; private set; }

    public Task<ApprovalOutcome> Outcome => _tcs.Task;
    public bool IsResolved => _tcs.Task.IsCompleted;

    public bool Resolve(ApprovalOutcome outcome, string? message = null)
    {
        Message = message;
        return _tcs.TrySetResult(outcome);
    }

    /// <summary>Short text for a tool call, e.g. "Bash: git push origin main" or "Edit: Program.cs".</summary>
    public static string Summarize(string tool, JsonElement input)
    {
        string? s = null;
        if (input.ValueKind == JsonValueKind.Object)
        {
            s = input.Str("command") ?? input.Str("description") ?? input.Str("file_path") ?? input.Str("path") ?? input.Str("url") ?? input.Str("pattern") ?? input.Str("query");
            if (s is null && input.TryGetProperty("command", out var cmd) && cmd.ValueKind == JsonValueKind.Array)
                s = string.Join(' ', cmd.EnumerateArray().Select(e => e.ToString()));
            if (s is null)
                foreach (var p in input.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 } v) { s = v; break; }
        }
        if (s is not null && (tool is "Edit" or "Write" or "MultiEdit" or "Read" or "NotebookEdit") && s.Contains('/')) s = Path.GetFileName(s);
        if (s is not null && s.TrimStart().StartsWith("*** Begin Patch", StringComparison.Ordinal))
        {
            var files = PatchFiles(s);
            s = files.Count == 0 ? "patch" : string.Join(", ", files.Take(2)) + (files.Count > 2 ? $" (+{files.Count - 2} more)" : "");
        }
        s = s?.Replace('\n', ' ').Trim();
        if (s is { Length: > 60 }) s = s[..60] + "…";
        return s is null ? tool : $"{tool}: {s}";
    }

    /// <summary>"Update README.md", "Add src/Foo.cs", … from a Codex apply_patch body.</summary>
    public static List<string> PatchFiles(string patch)
    {
        var list = new List<string>();
        foreach (var raw in patch.Split('\n'))
        {
            var line = raw.Trim();
            foreach (var op in new[] { "*** Update File: ", "*** Add File: ", "*** Delete File: " })
                if (line.StartsWith(op, StringComparison.Ordinal))
                {
                    var path = line[op.Length..].Trim();
                    var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var shortPath = parts.Length > 2 ? string.Join('/', parts[^2..]) : path;
                    list.Add(op[4..^7] + " " + shortPath);   // "Update", "Add", "Delete"
                }
        }
        return list;
    }
}

/// <summary>Holds pending approvals; the hook server adds them, deck actions resolve them.</summary>
public sealed class ApprovalRegistry
{
    private readonly object _lock = new();
    private readonly List<PendingApproval> _pending = new();

    public event Action<PendingApproval>? Added;
    public event Action<PendingApproval, ApprovalOutcome>? Resolved;

    public IReadOnlyList<PendingApproval> Pending { get { lock (_lock) return _pending.Where(p => !p.IsResolved).OrderBy(p => p.ReceivedAt).ToList(); } }

    public PendingApproval? ForAgent(string agentKey) => Pending.FirstOrDefault(p => p.AgentKey == agentKey);

    public void Add(PendingApproval p)
    {
        lock (_lock) _pending.Add(p);
        Log.Info($"Approval requested: {p.Provider} {p.AgentKey} → {p.Summary}");
        Added?.Invoke(p);
    }

    public void Remove(PendingApproval p) { lock (_lock) _pending.Remove(p); }

    public bool Resolve(PendingApproval p, ApprovalOutcome outcome, string? message = null)
    {
        if (!p.Resolve(outcome, message)) return false;
        Log.Info($"Approval {outcome}: {p.AgentKey} → {p.Summary}");
        Resolved?.Invoke(p, outcome);
        return true;
    }

    public bool Resolve(string id, ApprovalOutcome outcome, string? message = null)
    {
        var p = Pending.FirstOrDefault(x => x.Id == id);
        return p is not null && Resolve(p, outcome, message);
    }

    /// <summary>Releases every pending request for an agent (no decision → the terminal dialog appears).</summary>
    public int ReleaseAgent(string agentKey)
    {
        var n = 0;
        foreach (var p in Pending.Where(p => p.AgentKey == agentKey)) if (Resolve(p, ApprovalOutcome.Release)) n++;
        return n;
    }
}
