using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Collectors;

/// <summary>Reads sanitized agy status-line observations. Process identity, not saved chat history, defines liveness.</summary>
public sealed class AntigravitySessionCollector
{
    public static string DefaultHome => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli");
    public string SessionsDir { get; }
    public Func<int, string, bool> IsProcessAlive { get; set; } = (pid, ticks) => ProcUtil.IsAlive(pid, ticks);
    public Func<int, string> DetectHost { get; set; } = ProcUtil.DetectHost;
    public string BootId { get; set; } = ReadBootId();
    public ProviderQuota? LatestQuota { get; private set; }

    public AntigravitySessionCollector(string? antigravityHome = null)
        => SessionsDir = Path.Combine(antigravityHome ?? DefaultHome, "aiagentmonitor", "sessions");

    public IReadOnlyList<AgentInfo> Collect(DateTimeOffset now)
    {
        var result = new List<AgentInfo>();
        string[] files;
        try { files = Directory.GetFiles(SessionsDir, "*.json"); }
        catch (IOException) { return result; }
        catch (UnauthorizedAccessException) { return result; }
        foreach (var file in files)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var observed = ClaudeUsageClient.ParseResets(root.Prop("observed_at"));
                if (observed is null || observed > now.AddMinutes(1)) continue;
                // Quota remains useful after the TUI exits. Keep its observation time so the renderer marks it stale.
                var quota = ParseQuota(root, observed.Value);
                if (quota.Windows.Count > 0 && (LatestQuota is null || quota.FetchedAt > LatestQuota.FetchedAt)) LatestQuota = quota;
                var id = root.Str("session_id");
                var pidValue = root.Long("pid") ?? 0;
                if (string.IsNullOrWhiteSpace(id) || pidValue <= 1 || pidValue > int.MaxValue) continue;
                var pid = (int)pidValue;
                var ticks = root.Str("start_ticks");
                if (string.IsNullOrEmpty(ticks) || root.Str("boot_id") != BootId || !IsProcessAlive(pid, ticks)) continue;
                var state = ParseState(root.Str("agent_state"), root.Bool("tool_confirmation_pending") == true);
                if (state is null) continue;
                var cwd = root.Str("cwd") ?? "";
                var tasks = root.Long("task_count") ?? 0;
                result.Add(new AgentInfo
                {
                    Key = $"antigravity:{id}", Provider = Provider.Antigravity, SessionId = id,
                    Name = Path.GetFileName(cwd.TrimEnd('/')) is { Length: > 0 } name ? name : "Antigravity",
                    Cwd = cwd, Pid = pid, Host = DetectHost(pid), State = state.Value,
                    Detail = state == AgentState.Waiting ? "confirmation" : tasks > 0 ? $"{tasks} background task{(tasks == 1 ? "" : "s")}" : null,
                    StateSince = ClaudeUsageClient.ParseResets(root.Prop("state_since")) ?? observed.Value,
                    LastActivity = observed.Value,
                    StartedAt = ClaudeUsageClient.ParseResets(root.Prop("started_at")) ?? observed.Value,
                    Model = root.Str("model"), ContextPct = Percentage(root.Dbl("context_pct")),
                    ContextTokens = root.Long("context_tokens") is >= 0 and var tokens ? tokens : null,
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException or OverflowException)
            { Log.Debug($"Antigravity status read failed: {ex.GetType().Name}"); }
        }
        return result.GroupBy(a => a.Pid).Select(g => g.OrderByDescending(a => a.LastActivity).First()).ToList();
    }

    internal static AgentState? ParseState(string? state, bool confirmation) => confirmation ? AgentState.Waiting : state switch
    {
        "idle" => AgentState.Idle,
        "thinking" or "working" or "tool_use" or "initializing" => AgentState.Working,
        _ => null, // unsupported state is not evidence of either an idle or a failed turn
    };

    internal static ProviderQuota ParseQuota(JsonElement root, DateTimeOffset observed)
    {
        var windows = new List<QuotaWindow>();
        if (root.Obj("quota") is { ValueKind: JsonValueKind.Object } quotas)
            foreach (var q in quotas.EnumerateObject())
                if (q.Value.Dbl("remaining_fraction") is { } remaining && double.IsFinite(remaining))
                    windows.Add(new QuotaWindow("quota", Math.Clamp((1 - remaining) * 100, 0, 100),
                        ClaudeUsageClient.ParseResets(q.Value.Prop("reset_time")), q.Name));
        return new ProviderQuota { Provider = Provider.Antigravity, Windows = windows.OrderByDescending(w => w.UsedPct).ToList(),
            Plan = root.Str("plan_tier"), FetchedAt = observed, Source = "statusline" };
    }

    private static double? Percentage(double? value) => value is { } n && double.IsFinite(n) ? Math.Clamp(n, 0, 100) : null;
    private static string ReadBootId()
    {
        try { return File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim(); }
        catch (IOException) { return ""; }
    }
}
