using Opendeck.AiAgentMonitor.Collectors;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Agents;

/// <summary>Runs the collectors on a schedule and publishes a merged <see cref="Snapshot"/>.</summary>
public sealed class AgentMonitor : IDisposable
{
    private readonly ClaudeSessionCollector _claudeSessions = new();
    private readonly ClaudeUsageClient _claudeUsage = new();
    private readonly CodexRolloutCollector _codexRollouts = new();
    private readonly CodexUsageClient _codexUsage = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, AgentState> _lastStates = new();
    private FileSystemWatcher? _claudeWatcher, _codexWatcher;
    private DateTimeOffset _lastClaudeUsage = DateTimeOffset.MinValue, _lastCodexUsage = DateTimeOffset.MinValue;
    private int _dirty;

    public ApprovalRegistry Approvals { get; } = new();
    public Snapshot Current { get; private set; } = Snapshot.Empty;
    public event Action<Snapshot>? Changed;
    /// <summary>Raised when an agent enters the Waiting state (for alerts).</summary>
    public event Action<AgentInfo>? AgentNeedsAttention;

    // settings
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Base cadence for the usage endpoints; tripled while no Claude session is working (the numbers barely move then).</summary>
    public TimeSpan UsageInterval { get; set; } = TimeSpan.FromMinutes(5);
    private GlobalSettings? _appliedSettings;
    private DateTimeOffset _lastManualRefresh = DateTimeOffset.MinValue;
    public bool NetworkQuota { get; set; } = true;
    public bool CodexNetworkQuota { get; set; } = true;

    public void ApplySettings(GlobalSettings s)
    {
        var changed = _appliedSettings is null || _appliedSettings != s;
        _appliedSettings = s;
        UsageInterval = TimeSpan.FromSeconds(Math.Max(120, s.UsageRefreshSeconds));
        NetworkQuota = s.NetworkQuota;
        CodexNetworkQuota = s.NetworkQuota;
        _codexRollouts.IdleTimeout = TimeSpan.FromMinutes(Math.Max(5, s.CodexIdleMinutes));
        _claudeSessions.ContextWindowOverride = s.ContextWindow;
        if (changed) RequestUsageRefresh();   // OpenDeck re-sends unchanged settings on every willAppear: no refetch for those
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Fetch usage at the next pass — at most once a minute, and never inside a server-imposed backoff.</summary>
    public void RequestUsageRefresh()
    {
        var now = DateTimeOffset.UtcNow;
        if (now - _lastManualRefresh < TimeSpan.FromMinutes(1)) return;
        _lastManualRefresh = now;
        _lastClaudeUsage = _lastCodexUsage = DateTimeOffset.MinValue;
        Interlocked.Exchange(ref _dirty, 1);
    }

    /// <summary>Asks the loop to refresh soon (e.g. a hook event arrived).</summary>
    public void Poke() => Interlocked.Exchange(ref _dirty, 1);

    public void Start()
    {
        Approvals.Added += _ => Poke();
        Approvals.Resolved += (_, _) => Poke();
        TryWatch(_claudeSessions.SessionsDir, ref _claudeWatcher, false);
        TryWatch(_codexRollouts.SessionsDir, ref _codexWatcher, true);
        _ = Task.Run(LoopAsync);
    }

    private void TryWatch(string dir, ref FileSystemWatcher? watcher, bool recursive)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            watcher = new FileSystemWatcher(dir) { IncludeSubdirectories = recursive, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size };
            FileSystemEventHandler h = (_, _) => Interlocked.Exchange(ref _dirty, 1);
            watcher.Changed += h; watcher.Created += h; watcher.Deleted += h;
            watcher.Renamed += (_, _) => Interlocked.Exchange(ref _dirty, 1);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) { Log.Warn($"watch {dir}: {ex.Message}"); }
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await RefreshAsync(ct); }
            catch (Exception ex) { Log.Error("refresh failed", ex); }
            // wake early when a watcher flagged a change
            for (var waited = TimeSpan.Zero; waited < PollInterval; waited += TimeSpan.FromMilliseconds(250))
            {
                if (ct.IsCancellationRequested) return;
                await Task.Delay(250, ct).ContinueWith(_ => { });
                if (Interlocked.Exchange(ref _dirty, 0) == 1) { await Task.Delay(150, ct).ContinueWith(_ => { }); break; }
            }
        }
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct)) return;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var agents = new List<AgentInfo>();
            agents.AddRange(_claudeSessions.Collect(now));
            agents.AddRange(_codexRollouts.Collect(now));

            // permission requests held open by the hook server override the collectors' view
            var pending = Approvals.Pending;
            if (pending.Count > 0)
            {
                for (var i = 0; i < agents.Count; i++)
                {
                    var p = pending.FirstOrDefault(x => x.AgentKey == agents[i].Key);
                    if (p is not null)
                        agents[i] = agents[i] with { State = AgentState.Waiting, StateSince = p.ReceivedAt, Detail = p.Summary, Approval = p, LastActivity = now };
                }
                foreach (var p in pending.Where(p => agents.All(a => a.Key != p.AgentKey)))
                    agents.Add(new AgentInfo
                    {
                        Key = p.AgentKey, Provider = p.Provider, Name = Path.GetFileName(p.Cwd.TrimEnd('/')), Cwd = p.Cwd, Host = "?",
                        State = AgentState.Waiting, StateSince = p.ReceivedAt, LastActivity = now, StartedAt = p.ReceivedAt,
                        Detail = p.Summary, Approval = p, SessionId = p.SessionId,
                    });
            }

            // quotas: base cadence while an agent is active, 3× slower otherwise; the Claude endpoint hands out
            // hour-long Retry-Afters when polled too eagerly, so the client also backs off on its own
            var anyClaudeActive = agents.Any(a => a.Provider == Provider.Claude && a.State is AgentState.Working or AgentState.Waiting);
            var anyCodexActive = agents.Any(a => a.Provider == Provider.Codex && a.State is AgentState.Working or AgentState.Waiting);
            var claudeInterval = anyClaudeActive ? UsageInterval : UsageInterval * 3;
            var codexInterval = anyCodexActive ? UsageInterval : UsageInterval * 3;
            ProviderQuota? claude = _claudeUsage.Last;
            if (NetworkQuota && now - _lastClaudeUsage > claudeInterval && now >= _claudeUsage.NotBefore)
            {
                _lastClaudeUsage = now;
                claude = await _claudeUsage.FetchAsync(UsageInterval, ct);
            }
            ProviderQuota? codex = _codexRollouts.LatestQuota;
            if (CodexNetworkQuota && now - _lastCodexUsage > codexInterval)
            {
                _lastCodexUsage = now;
                var api = await _codexUsage.FetchAsync(ct);
                if (api is not null && api.Error is null) codex = api;
                else if (codex is null) codex = api;
                _codexApi = api;
            }
            else if (_codexApi is { Error: null } && (codex is null || _codexApi.FetchedAt > codex.FetchedAt)) codex = _codexApi;

            var snapshot = new Snapshot { Agents = agents, Claude = claude, Codex = codex, At = now };
            var changed = !Equivalent(Current, snapshot);
            Current = snapshot;
            foreach (var a in agents)
            {
                var prev = _lastStates.TryGetValue(a.Key, out var p) ? p : (AgentState?)null;
                if (a.State == AgentState.Waiting && prev != AgentState.Waiting) AgentNeedsAttention?.Invoke(a);
                _lastStates[a.Key] = a.State;
            }
            foreach (var k in _lastStates.Keys.Where(k => agents.All(a => a.Key != k)).ToList()) _lastStates.Remove(k);
            if (changed) Changed?.Invoke(snapshot);
        }
        finally { _gate.Release(); }
    }

    private ProviderQuota? _codexApi;

    private static bool Equivalent(Snapshot a, Snapshot b)
    {
        if (a.Agents.Count != b.Agents.Count) return false;
        if (!QuotaEq(a.Claude, b.Claude) || !QuotaEq(a.Codex, b.Codex)) return false;
        for (var i = 0; i < a.Agents.Count; i++)
        {
            var x = a.Agents[i]; var y = b.Agents[i];
            if (x.Key != y.Key || x.State != y.State || x.Detail != y.Detail || x.Name != y.Name || x.SubAgents != y.SubAgents || x.Approval?.Id != y.Approval?.Id
                || x.Model != y.Model || Math.Round(x.ContextPct ?? -1) != Math.Round(y.ContextPct ?? -1)
                || (x.LastActivity - y.LastActivity).Duration() > TimeSpan.FromSeconds(30)) return false;
        }
        return true;
    }

    private static bool QuotaEq(ProviderQuota? a, ProviderQuota? b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (a.Error != b.Error || a.Windows.Count != b.Windows.Count) return false;
        for (var i = 0; i < a.Windows.Count; i++)
            if (a.Windows[i].Label != b.Windows[i].Label || Math.Round(a.Windows[i].UsedPct) != Math.Round(b.Windows[i].UsedPct)) return false;
        return true;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _claudeWatcher?.Dispose(); _codexWatcher?.Dispose();
    }
}

/// <summary>Plugin-wide settings (stored via setGlobalSettings).</summary>
public sealed record GlobalSettings
{
    public int UsageRefreshSeconds { get; init; } = 300;
    public bool NetworkQuota { get; init; } = true;
    public int CodexIdleMinutes { get; init; } = 120;
    public long ContextWindow { get; init; } = 0;          // 0 = auto
    public string MonitorProfile { get; init; } = "AI Agents";
    public string MainProfile { get; init; } = "Default";
    public int TickSeconds { get; init; } = 30;
    public int HookPort { get; init; } = 43117;
    public int ApprovalHoldSeconds { get; init; } = 30;
    public bool HoldOnlyWhenUnfocused { get; init; } = false;
    public bool NotifyOnApproval { get; init; } = true;
    /// <summary>auto | dialog | notification | none — how a held permission request is shown on screen.</summary>
    public string ApprovalPopup { get; init; } = "auto";
    /// <summary>center | primary | mouse — which monitor the popup opens on.</summary>
    public string PopupScreen { get; init; } = "center";

    public static GlobalSettings From(System.Text.Json.JsonElement e)
    {
        if (e.ValueKind != System.Text.Json.JsonValueKind.Object) return new GlobalSettings();
        var d = new GlobalSettings();
        return new GlobalSettings
        {
            UsageRefreshSeconds = (int)(e.Long("usageRefreshSeconds") ?? d.UsageRefreshSeconds),
            NetworkQuota = e.Bool("networkQuota") ?? d.NetworkQuota,
            CodexIdleMinutes = (int)(e.Long("codexIdleMinutes") ?? d.CodexIdleMinutes),
            ContextWindow = e.Long("contextWindow") ?? d.ContextWindow,
            MonitorProfile = e.Str("monitorProfile") is { Length: > 0 } mp ? mp : d.MonitorProfile,
            MainProfile = e.Str("mainProfile") is { Length: > 0 } mn ? mn : d.MainProfile,
            TickSeconds = (int)(e.Long("tickSeconds") ?? d.TickSeconds),
            HookPort = (int)(e.Long("hookPort") ?? d.HookPort),
            ApprovalHoldSeconds = (int)(e.Long("approvalHoldSeconds") ?? d.ApprovalHoldSeconds),
            HoldOnlyWhenUnfocused = e.Bool("holdOnlyWhenUnfocused") ?? d.HoldOnlyWhenUnfocused,
            NotifyOnApproval = e.Bool("notifyOnApproval") ?? d.NotifyOnApproval,
            ApprovalPopup = e.Str("approvalPopup") is { Length: > 0 } ap ? ap : (e.Bool("notifyOnApproval") == false ? "none" : d.ApprovalPopup),
            PopupScreen = e.Str("popupScreen") is { Length: > 0 } ps ? ps : d.PopupScreen,
        };
    }
}
