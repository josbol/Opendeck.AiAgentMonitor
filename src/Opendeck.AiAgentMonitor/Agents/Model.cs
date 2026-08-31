namespace Opendeck.AiAgentMonitor.Agents;

public enum Provider { Claude, Codex }

public enum AgentState
{
    /// <summary>The agent is executing a turn (thinking / running tools).</summary>
    Working,
    /// <summary>The agent is blocked on the user: permission prompt, question, dialog.</summary>
    Waiting,
    /// <summary>The last turn died on an API error (no capacity, rate limit, auth) and nothing has run since.</summary>
    Error,
    /// <summary>The agent finished its turn and is waiting for the next prompt.</summary>
    Idle,
    /// <summary>The session/process is gone (kept briefly for display).</summary>
    Ended,
}

public sealed record AgentInfo
{
    public required string Key { get; init; }              // stable id (provider:sessionId)
    public required Provider Provider { get; init; }
    public required string Name { get; init; }             // short human name (project dir or session name)
    public required string Cwd { get; init; }
    public required string Host { get; init; }             // Rider / Term / App / ...
    public required AgentState State { get; init; }
    public required DateTimeOffset StateSince { get; init; }
    public required DateTimeOffset LastActivity { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public string? Model { get; init; }
    public double? ContextPct { get; init; }               // 0..100 of the context window
    public long? ContextTokens { get; init; }
    public string? Detail { get; init; }                   // e.g. "permission prompt", "compacting"
    public string? Title { get; init; }                    // first prompt / session title
    public int? Pid { get; init; }
    public int SubAgents { get; init; }
    public string? SessionId { get; init; }
    public bool Background { get; init; }
    /// <summary>A permission request currently held open for this agent (answerable from the deck).</summary>
    public PendingApproval? Approval { get; init; }

    public string ProjectName => string.IsNullOrEmpty(Cwd) ? Name : Path.GetFileName(Cwd.TrimEnd('/')) is { Length: > 0 } n ? n : Cwd;

    /// <summary>True when the deck should pull the user in: blocked on input, or the turn died on an error.</summary>
    public bool NeedsAttention => State is AgentState.Waiting or AgentState.Error;
}

public sealed record QuotaWindow(string Label, double UsedPct, DateTimeOffset? ResetsAt, string? Scope = null)
{
    public TimeSpan? TimeToReset(DateTimeOffset now) => ResetsAt is null ? null : (ResetsAt.Value - now is var t && t < TimeSpan.Zero ? TimeSpan.Zero : t);
}

public sealed record ProviderQuota
{
    public required Provider Provider { get; init; }
    public required IReadOnlyList<QuotaWindow> Windows { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
    public string? Plan { get; init; }
    public string? Error { get; init; }
    public string? Source { get; init; }     // "api" | "rollout"

    public QuotaWindow? Short => Windows.FirstOrDefault(w => w.Label == "5h");
    public QuotaWindow? Long => Windows.FirstOrDefault(w => w.Label == "7d" && w.Scope is null) ?? Windows.FirstOrDefault(w => w.Label == "7d");
}

public sealed record Snapshot
{
    public required IReadOnlyList<AgentInfo> Agents { get; init; }
    public ProviderQuota? Claude { get; init; }
    public ProviderQuota? Codex { get; init; }
    public required DateTimeOffset At { get; init; }

    public static readonly Snapshot Empty = new() { Agents = Array.Empty<AgentInfo>(), At = DateTimeOffset.MinValue };

    public IEnumerable<AgentInfo> Live => Agents.Where(a => a.State != AgentState.Ended);
    public int Count(AgentState s) => Agents.Count(a => a.State == s);
    public int Count(Provider p) => Agents.Count(a => a.Provider == p && a.State != AgentState.Ended);

    /// <summary>Attention-first ordering used by the auto slots and the dial.</summary>
    public IReadOnlyList<AgentInfo> Ordered(Provider? filter = null)
    {
        static int Rank(AgentState s) => s switch { AgentState.Waiting => 0, AgentState.Error => 1, AgentState.Working => 2, AgentState.Idle => 3, _ => 4 };
        return Live
            .Where(a => filter is null || a.Provider == filter)
            .OrderBy(a => Rank(a.State))
            .ThenBy(a => a.NeedsAttention ? a.StateSince : DateTimeOffset.MaxValue) // longest-waiting first
            .ThenByDescending(a => a.LastActivity)
            .ToList();
    }
}
