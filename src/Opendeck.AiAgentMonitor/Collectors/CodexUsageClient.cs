using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Collectors;

/// <summary>
/// Reads the ChatGPT-plan Codex rate limits from https://chatgpt.com/backend-api/wham/usage
/// (the endpoint the Codex TUI polls) using ~/.codex/auth.json. Used when the rollout files
/// have nothing recent; rollouts remain the primary, network-free source.
/// </summary>
public sealed class CodexUsageClient
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) }) { Timeout = TimeSpan.FromSeconds(20) };
    private readonly string _authPath;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;

    public CodexUsageClient(string? codexHome = null)
    {
        var home = codexHome ?? Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        _authPath = Path.Combine(home, "auth.json");
    }

    public ProviderQuota? Last { get; private set; }

    public async Task<ProviderQuota?> FetchAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _retryAfter) return Last;
        var (token, account) = ReadAuth();
        if (token is null) return Last = new ProviderQuota { Provider = Provider.Codex, Windows = Array.Empty<QuotaWindow>(), FetchedAt = now, Error = "no login" };
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (account is not null) req.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", account);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "opendeck-aiagentmonitor");
            using var resp = await Http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _retryAfter = now + (resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
                return Last = (Last ?? Empty(now)) with { Error = "rate limited" };
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized) return Last = Empty(now) with { Error = "unauthorized" };
            if (!resp.IsSuccessStatusCode) return Last = (Last ?? Empty(now)) with { Error = $"HTTP {(int)resp.StatusCode}" };

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            var windows = new List<QuotaWindow>();
            void Add(JsonElement? w)
            {
                if (w is null || w.Value.ValueKind != JsonValueKind.Object) return;
                var pct = w.Value.Dbl("used_percent"); if (pct is null) return;
                var secs = w.Value.Long("limit_window_seconds") ?? 0;
                var label = secs switch { >= 600000 => "7d", >= 14400 and <= 21600 => "5h", > 0 => $"{secs / 3600}h", _ => "?" };
                DateTimeOffset? resets = w.Value.Long("reset_at") is { } ra ? DateTimeOffset.FromUnixTimeSeconds(ra)
                    : w.Value.Long("reset_after_seconds") is { } ras ? now.AddSeconds(ras) : null;
                windows.Add(new QuotaWindow(label, pct.Value, resets));
            }
            var rl = root.Obj("rate_limit");
            Add(rl?.Obj("primary_window"));
            Add(rl?.Obj("secondary_window"));
            return Last = new ProviderQuota { Provider = Provider.Codex, Windows = windows, FetchedAt = now, Plan = root.Str("plan_type"), Source = "api" };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return Last = (Last ?? Empty(now)) with { Error = "timeout" }; }
        catch (Exception ex)
        {
            Log.Warn($"Codex usage fetch failed: {ex.Message}");
            return Last = (Last ?? Empty(now)) with { Error = "offline" };
        }
    }

    private static ProviderQuota Empty(DateTimeOffset now) => new() { Provider = Provider.Codex, Windows = Array.Empty<QuotaWindow>(), FetchedAt = now };

    private (string? Token, string? Account) ReadAuth()
    {
        try
        {
            if (!File.Exists(_authPath)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(_authPath));
            var tokens = doc.RootElement.Obj("tokens");
            return (tokens?.Str("access_token"), tokens?.Str("account_id"));
        }
        catch (Exception ex) { Log.Warn($"codex auth: {ex.Message}"); return (null, null); }
    }
}
