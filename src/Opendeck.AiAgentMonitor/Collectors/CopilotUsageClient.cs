using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Collectors;

/// <summary>
/// Reads the GitHub Copilot plan budget — premium requests (or AI credits) per month — from GitHub's internal user
/// endpoint (GET https://api.github.com/copilot_internal/user, the call the IDE plugins make to show "premium requests
/// remaining"). Tokens, in order of preference: COPILOT_GITHUB_TOKEN / GH_TOKEN / GITHUB_TOKEN, the gh CLI's login
/// (`gh auth token` — what the Copilot CLI itself signs in with), and the oauth_token entries the IDE plugins keep in
/// ~/.config/github-copilot/apps.json (hosts.json on older versions), which go stale once a plugin moves to its
/// encrypted store. The first token the endpoint accepts is remembered and tried first next time.
/// Expected reply: { copilot_plan, quota_reset_date, quota_snapshots: { premium_interactions: { entitlement, remaining,
/// percent_remaining, unlimited } … } }; the parser is lenient about the names.
/// </summary>
public sealed class CopilotUsageClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _configDir;
    private readonly string _cachePath;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private int _consecutiveFailures;
    private string? _preferredSource;
    private string? _ghToken;
    private DateTime _ghTokenAt = DateTime.MinValue;

    public CopilotUsageClient(string? configDir = null)
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        _configDir = configDir ?? Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg, "github-copilot");
        _cachePath = Path.Combine(UsageCache.Dir, "copilot-usage.json");
        Last = UsageCache.Load(_cachePath, Provider.Copilot);
    }

    public ProviderQuota? Last { get; private set; }
    public DateTimeOffset NotBefore => _retryAfter;

    public async Task<ProviderQuota?> FetchAsync(TimeSpan baseInterval, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _retryAfter) return Last;
        var sources = ReadTokens();
        if (sources.Count == 0)
        {
            if (Last?.Error != "no login") Log.Warn($"Copilot usage: no token (gh auth token, {_configDir}/apps.json, GH_TOKEN)");
            return Last = Empty(now) with { Error = "no login" };
        }
        try
        {
            foreach (var (name, token) in sources.OrderBy(s => s.Name == _preferredSource ? 0 : 1))
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/copilot_internal/user");
                req.Headers.TryAddWithoutValidation("Authorization", "token " + token);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                // the endpoint is meant for the editor plugins; identify like one of them
                req.Headers.TryAddWithoutValidation("User-Agent", "GitHubCopilotChat/0.26.7");
                req.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.100.0");
                req.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
                using var resp = await Http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized)
                {
                    Log.Debug($"Copilot usage: token from {name} rejected (401)");
                    continue;   // stale token: try the next source
                }
                if (resp.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.Forbidden && resp.Headers.RetryAfter is not null)
                {
                    _consecutiveFailures++;
                    var wait = resp.Headers.RetryAfter?.Delta ?? ClaudeUsageClient.Backoff(_consecutiveFailures, baseInterval);
                    _retryAfter = now + wait;
                    Log.Warn($"Copilot usage: HTTP {(int)resp.StatusCode}, next attempt in {wait}");
                    return Last = (Last ?? Empty(now)) with { Error = "rate limited" };
                }
                if (!resp.IsSuccessStatusCode)
                {
                    _consecutiveFailures++;
                    _retryAfter = now + ClaudeUsageClient.Backoff(_consecutiveFailures, baseInterval);
                    Log.Warn($"Copilot usage: HTTP {(int)resp.StatusCode} {Clip(await resp.Content.ReadAsStringAsync(ct))}");
                    return Last = (Last ?? Empty(now)) with { Error = $"HTTP {(int)resp.StatusCode}" };
                }

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
                var (windows, plan) = Parse(doc.RootElement);
                _consecutiveFailures = 0;
                _retryAfter = DateTimeOffset.MinValue;
                if (_preferredSource != name) Log.Info($"Copilot usage: using the token from {name}");
                _preferredSource = name;
                if (windows.Count == 0)
                    // the reply shape is not documented: name what came back so the parser can be adjusted
                    Log.Warn($"Copilot usage: no quota in reply; keys: {string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))}; quota_snapshots: {Clip(doc.RootElement.Obj("quota_snapshots")?.GetRawText() ?? "-")}");
                else
                    Log.Info($"Copilot usage: plan {plan ?? "?"}, {string.Join(", ", windows.Select(w => $"{w.Label} {w.UsedPct:0}%{(w.Scope is null ? "" : " " + w.Scope)}"))}");
                Last = new ProviderQuota { Provider = Provider.Copilot, Windows = windows, FetchedAt = now, Plan = plan, Source = "api", Error = windows.Count == 0 ? "no quota in reply" : null };
                if (windows.Count > 0) UsageCache.Save(_cachePath, Last);
                return Last;
            }
            if (Last?.Error != "unauthorized") Log.Warn($"Copilot usage: every token was rejected (401): {string.Join(", ", sources.Select(s => s.Name))}");
            return Last = Empty(now) with { Error = "unauthorized" };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warn("Copilot usage: timeout");
            return Last = (Last ?? Empty(now)) with { Error = "timeout" };
        }
        catch (Exception ex)
        {
            Log.Warn($"Copilot usage fetch failed: {ex.Message}");
            return Last = (Last ?? Empty(now)) with { Error = "offline" };
        }
    }

    private static string Clip(string s) => s.Replace('\n', ' ')[..Math.Min(s.Length, 200)];
    private static ProviderQuota Empty(DateTimeOffset now) => new() { Provider = Provider.Copilot, Windows = Array.Empty<QuotaWindow>(), FetchedAt = now };

    /// <summary>One "month" window per metered budget (unlimited ones are skipped); the plan name alongside.</summary>
    internal static (List<QuotaWindow> Windows, string? Plan) Parse(JsonElement root)
    {
        var windows = new List<QuotaWindow>();
        var reset = ClaudeUsageClient.ParseResets(root.Prop("quota_reset_date"));
        void Add(JsonElement? q, string? scope = null)
        {
            if (q is null || q.Value.ValueKind != JsonValueKind.Object) return;
            if (q.Value.Bool("unlimited") == true) return;
            double? used = null;
            if (q.Value.Dbl("percent_remaining") is { } pr) used = 100 - pr;
            else if (q.Value.Dbl("entitlement") is { } ent && ent > 0 && (q.Value.Dbl("remaining") ?? q.Value.Dbl("quota_remaining")) is { } rem) used = 100.0 * (ent - rem) / ent;
            else if (q.Value.Dbl("used_percent") is { } up) used = up;
            else if (q.Value.Dbl("percent_used") is { } pu) used = pu;
            if (used is null) return;
            windows.Add(new QuotaWindow("month", Math.Clamp(used.Value, 0, 100), ClaudeUsageClient.ParseResets(q.Value.Prop("quota_reset_date")) ?? reset, scope));
        }
        var snaps = root.Obj("quota_snapshots");
        Add(snaps?.Obj("premium_interactions"));
        Add(snaps?.Obj("ai_credits"));
        if (windows.Count == 0 && snaps is { ValueKind: JsonValueKind.Object } s)
            foreach (var p in s.EnumerateObject())
                if (p.Name is not ("chat" or "completions")) Add(p.Value, p.Name);
        if (windows.Count == 0) Add(root.Obj("quota"));
        var plan = root.Str("copilot_plan") ?? root.Str("access_type_sku") ?? root.Str("plan");
        return (windows, plan);
    }

    // ---- tokens -----------------------------------------------------------------------------

    /// <summary>Candidate tokens by source name (never logged): env vars, the gh CLI login, then the plugins' oauth_token entries.</summary>
    internal List<(string Name, string Token)> ReadTokens()
    {
        var list = new List<(string, string)>();
        foreach (var v in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" })
            if (Environment.GetEnvironmentVariable(v) is { Length: > 0 } t) list.Add((v, t));
        if (GhToken() is { } gh) list.Add(("gh auth token", gh));
        foreach (var file in new[] { "apps.json", "hosts.json" })
        {
            var path = Path.Combine(_configDir, file);
            try
            {
                if (!File.Exists(path)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                foreach (var host in doc.RootElement.EnumerateObject())
                    if (host.Name.StartsWith("github.com", StringComparison.OrdinalIgnoreCase) && host.Value.Str("oauth_token") is { Length: > 0 } t)
                        list.Add(($"{file} {host.Name}", t));
            }
            catch (Exception ex) { Log.Warn($"copilot auth {path}: {ex.Message}"); }
        }
        return list;
    }

    /// <summary>`gh auth token -h github.com` (cached for 30 minutes; the Copilot CLI signs in through gh too).</summary>
    private string? GhToken()
    {
        if ((DateTime.UtcNow - _ghTokenAt).TotalMinutes < 30) return _ghToken;
        _ghTokenAt = DateTime.UtcNow;
        try
        {
            var psi = new ProcessStartInfo("gh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "auth", "token", "-h", "github.com" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            if (p is null) return _ghToken = null;
            var output = p.StandardOutput.ReadToEnd().Trim();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch { } return _ghToken = null; }
            return _ghToken = p.ExitCode == 0 && output.Length > 0 && !output.Contains(' ') ? output : null;
        }
        catch (Exception ex) { Log.Debug($"gh auth token: {ex.Message}"); return _ghToken = null; }
    }
}
