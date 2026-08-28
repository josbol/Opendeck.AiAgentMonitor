using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Collectors;

/// <summary>
/// Reads the Claude.ai subscription rate-limit windows through the same endpoint Claude Code uses
/// (GET https://api.anthropic.com/api/oauth/usage) with the OAuth token from ~/.claude/.credentials.json.
/// </summary>
public sealed class ClaudeUsageClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _credentialsPath;
    private readonly string _claudeHome;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private string? _claudeVersion;

    public ClaudeUsageClient(string? claudeHome = null)
    {
        _claudeHome = claudeHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        _credentialsPath = Path.Combine(_claudeHome, ".credentials.json");
    }

    public ProviderQuota? Last { get; private set; }

    public async Task<ProviderQuota?> FetchAsync(CancellationToken ct)
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
            req.Headers.TryAddWithoutValidation("User-Agent", $"claude-code/{ClaudeVersion()}");
            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var ra = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5);
                _retryAfter = now + ra;
                Log.Warn($"Claude usage: 429, retrying after {ra}");
                return Last = (Last ?? Empty(now)) with { Error = "rate limited" };
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                var expired = expiresAt is not null && expiresAt < now;
                return Last = Empty(now) with { Error = expired ? "token expired" : "unauthorized", Plan = plan };
            }
            if (!resp.IsSuccessStatusCode)
                return Last = (Last ?? Empty(now)) with { Error = $"HTTP {(int)resp.StatusCode}" };

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var windows = ParseWindows(doc.RootElement);
            return Last = new ProviderQuota { Provider = Provider.Claude, Windows = windows, FetchedAt = now, Plan = plan, Source = "api" };
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
            // any live session file carries the version; fall back to a plausible one
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
