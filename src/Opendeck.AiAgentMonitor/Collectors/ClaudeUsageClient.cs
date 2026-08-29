using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Collectors;

/// <summary>
/// Reads the Claude.ai subscription rate-limit windows through the same endpoint Claude Code uses
/// (GET https://api.anthropic.com/api/oauth/usage) with the OAuth token from ~/.claude/.credentials.json.
/// The endpoint rate-limits clients aggressively (hour-long Retry-After), so: Claude Code's own User-Agent,
/// a slow cadence (see <see cref="AgentMonitor"/>), exponential backoff after a 429, and the last good answer
/// cached on disk so a restart shows stale numbers instead of nothing.
/// </summary>
public sealed class ClaudeUsageClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _credentialsPath;
    private readonly string _claudeHome;
    private readonly string _cachePath;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private string? _claudeVersion;

    public ClaudeUsageClient(string? claudeHome = null)
    {
        _claudeHome = claudeHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _credentialsPath = Path.Combine(_claudeHome, ".credentials.json");
        _cachePath = Path.Combine(UsageCache.Dir, "claude-usage.json");
        Last = UsageCache.Load(_cachePath, Provider.Claude);
    }

    public ProviderQuota? Last { get; private set; }

    /// <summary>When the next request may be made (after a 429 this is the server's Retry-After or the backoff, whichever is later).</summary>
    public DateTimeOffset NotBefore => _retryAfter;

    /// <summary>Extra wait after consecutive failures: base, 2×, 4× … capped at one hour.</summary>
    internal static TimeSpan Backoff(int consecutiveFailures, TimeSpan baseInterval)
    {
        if (consecutiveFailures <= 0) return TimeSpan.Zero;
        var factor = Math.Pow(2, Math.Min(consecutiveFailures, 6));
        var wait = TimeSpan.FromTicks((long)(baseInterval.Ticks * factor));
        return wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait;
    }

    public async Task<ProviderQuota?> FetchAsync(TimeSpan baseInterval, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _retryAfter) return Last;

        var (token, plan, expiresAt) = ReadCredentials();
        if (token is null)
            return Last = new ProviderQuota { Provider = Provider.Claude, Windows = Array.Empty<QuotaWindow>(), FetchedAt = now, Error = "no login" };

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", $"claude-cli/{ClaudeVersion()} (external, cli)");   // what Claude Code itself sends
            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _consecutiveFailures++;
                var serverWait = resp.Headers.RetryAfter?.Delta ?? (resp.Headers.RetryAfter?.Date is { } d ? d - now : null);
                var wait = Max(serverWait ?? TimeSpan.Zero, Backoff(_consecutiveFailures, baseInterval));
                _retryAfter = now + wait;
                Log.Warn($"Claude usage: 429 (server Retry-After {serverWait?.ToString() ?? "none"}); next attempt in {wait}");
                return Last = (Last ?? Empty(now)) with { Error = "rate limited" };
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                var expired = expiresAt is not null && expiresAt < now;
                return Last = (Last ?? Empty(now)) with { Error = expired ? "token expired" : "unauthorized", Plan = plan };
            }
            if (!resp.IsSuccessStatusCode)
            {
                _consecutiveFailures++;
                _retryAfter = now + Backoff(_consecutiveFailures, baseInterval);
                return Last = (Last ?? Empty(now)) with { Error = $"HTTP {(int)resp.StatusCode}" };
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var windows = ParseWindows(doc.RootElement);
            _consecutiveFailures = 0;
            _retryAfter = DateTimeOffset.MinValue;
            Last = new ProviderQuota { Provider = Provider.Claude, Windows = windows, FetchedAt = now, Plan = plan, Source = "api" };
            UsageCache.Save(_cachePath, Last);
            return Last;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Last = (Last ?? Empty(now)) with { Error = "timeout" };
        }
        catch (Exception ex)
        {
            Log.Warn($"Claude usage fetch failed: {ex.Message}");
            return Last = (Last ?? Empty(now)) with { Error = "offline" };
        }
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    private static ProviderQuota Empty(DateTimeOffset now) => new() { Provider = Provider.Claude, Windows = Array.Empty<QuotaWindow>(), FetchedAt = now };

    internal static List<QuotaWindow> ParseWindows(JsonElement root)
    {
        var list = new List<QuotaWindow>();
        void Add(string label, JsonElement? w, string? scope = null)
        {
            if (w is null || w.Value.ValueKind != JsonValueKind.Object) return;
            var util = w.Value.Dbl("utilization") ?? w.Value.Dbl("used_percentage") ?? w.Value.Dbl("percent");
            if (util is null) return;
            list.Add(new QuotaWindow(label, util.Value, ParseResets(w.Value.Prop("resets_at")), scope));
        }
        Add("5h", root.Obj("five_hour"));
        Add("7d", root.Obj("seven_day"));
        Add("7d", root.Obj("seven_day_opus"), "Opus");
        Add("7d", root.Obj("seven_day_sonnet"), "Sonnet");

        // Newer shape: limits[] with kind session / weekly_all / weekly_scoped
        if (root.Obj("limits") is { ValueKind: JsonValueKind.Array } limits)
        {
            foreach (var l in limits.EnumerateArray())
            {
                var kind = l.Str("kind") ?? "";
                var pct = l.Dbl("percent");
                if (pct is null) continue;
                var resets = ParseResets(l.Prop("resets_at"));
                var scopeName = l.Obj("scope")?.Obj("model")?.Str("display_name");
                switch (kind)
                {
                    case "session":
                        if (!list.Any(w => w.Label == "5h")) list.Add(new QuotaWindow("5h", pct.Value, resets)); break;
                    case "weekly_all":
                        if (!list.Any(w => w.Label == "7d" && w.Scope is null)) list.Add(new QuotaWindow("7d", pct.Value, resets)); break;
                    case "weekly_scoped":
                        if (scopeName is not null && !list.Any(w => w.Label == "7d" && w.Scope == scopeName)) list.Add(new QuotaWindow("7d", pct.Value, resets, scopeName)); break;
                }
            }
        }
        return list;
    }

    internal static DateTimeOffset? ParseResets(JsonElement? el)
    {
        if (el is null) return null;
        var e = el.Value;
        if (e.ValueKind == JsonValueKind.Number) return DateTimeOffset.FromUnixTimeSeconds((long)e.GetDouble());
        if (e.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var dto)) return dto;
        return null;
    }

    private (string? Token, string? Plan, DateTimeOffset? ExpiresAt) ReadCredentials()
    {
        try
        {
            if (!File.Exists(_credentialsPath)) return (null, null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(_credentialsPath));
            var oauth = doc.RootElement.Obj("claudeAiOauth");
            if (oauth is null) return (null, null, null);
            var exp = oauth.Value.Long("expiresAt");
            return (oauth.Value.Str("accessToken"), oauth.Value.Str("subscriptionType"), exp is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(exp.Value));
        }
        catch (Exception ex) { Log.Warn($"credentials: {ex.Message}"); return (null, null, null); }
    }

    private string ClaudeVersion()
    {
        if (_claudeVersion is not null) return _claudeVersion;
        try
        {
            // any live session file carries the version; fall back to the last update record
            var dir = Path.Combine(_claudeHome, "sessions");
            foreach (var f in Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : Array.Empty<string>())
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(f));
                var v = doc.RootElement.Str("version");
                if (v is not null) return _claudeVersion = v;
            }
            var upd = Path.Combine(_claudeHome, ".last-update-result.json");
            if (File.Exists(upd))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(upd));
                var v = doc.RootElement.Str("version_to");
                if (v is not null) return _claudeVersion = v;
            }
        }
        catch { }
        return _claudeVersion = "2.1.0";
    }
}

/// <summary>Last successful usage answer per provider, kept in ~/.cache so a restart during a backoff still has numbers.</summary>
internal static class UsageCache
{
    public static string Dir => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } x ? x : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "opendeck-aiagentmonitor");

    public static void Save(string path, ProviderQuota q)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var payload = new { plan = q.Plan, fetchedAt = q.FetchedAt, windows = q.Windows.Select(w => new { w.Label, w.UsedPct, w.ResetsAt, w.Scope }) };
            File.WriteAllText(path, Json.Serialize(payload));
        }
        catch (Exception ex) { Log.Debug($"usage cache save: {ex.Message}"); }
    }

    public static ProviderQuota? Load(string path, Provider provider)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var fetchedAt = ParseTime(root.Prop("fetchedAt")) ?? DateTimeOffset.MinValue;
            if (DateTimeOffset.UtcNow - fetchedAt > TimeSpan.FromDays(2)) return null;   // too old to be worth showing
            var windows = new List<QuotaWindow>();
            if (root.Obj("windows") is { ValueKind: JsonValueKind.Array } arr)
                foreach (var w in arr.EnumerateArray())
                    if (w.Str("label") is { } label && w.Dbl("usedPct") is { } pct)
                        windows.Add(new QuotaWindow(label, pct, ParseTime(w.Prop("resetsAt")), w.Str("scope")));
            return windows.Count == 0 ? null : new ProviderQuota { Provider = provider, Windows = windows, FetchedAt = fetchedAt, Plan = root.Str("plan"), Source = "cache" };
        }
        catch (Exception ex) { Log.Debug($"usage cache load: {ex.Message}"); return null; }
    }

    private static DateTimeOffset? ParseTime(JsonElement? el)
        => el is { ValueKind: JsonValueKind.String } e && DateTimeOffset.TryParse(e.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
