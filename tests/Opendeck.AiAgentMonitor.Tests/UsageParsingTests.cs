using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Collectors;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

public class UsageParsingTests
{
    // Shape returned by GET https://api.anthropic.com/api/oauth/usage (August 2026), trimmed.
    private const string ClaudeUsage = """
        {
          "five_hour": {"utilization": 36.0, "resets_at": "2026-08-28T16:59:59.534114+00:00", "limit_dollars": null},
          "seven_day": {"utilization": 38.0, "resets_at": "2026-08-29T06:59:59.534137+00:00"},
          "seven_day_opus": null,
          "seven_day_sonnet": null,
          "nimbus_quill": {"utilization": 0.0, "resets_at": null},
          "limits": [
            {"kind": "session", "group": "session", "percent": 36, "severity": "normal", "resets_at": "2026-08-28T16:59:59+00:00", "scope": null},
            {"kind": "weekly_all", "group": "weekly", "percent": 38, "severity": "normal", "resets_at": "2026-08-29T06:59:59+00:00", "scope": null},
            {"kind": "weekly_scoped", "group": "weekly", "percent": 67, "severity": "normal", "resets_at": "2026-08-29T06:59:59+00:00",
             "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}, "is_active": true}
          ]
        }
        """;

    [Fact]
    public void ParsesClaudeWindowsIncludingScopedWeekly()
    {
        using var doc = JsonDocument.Parse(ClaudeUsage);
        var windows = ClaudeUsageClient.ParseWindows(doc.RootElement);

        var fiveHour = Assert.Single(windows, w => w.Label == "5h");
        Assert.Equal(36.0, fiveHour.UsedPct);
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 16, 59, 59, TimeSpan.Zero), fiveHour.ResetsAt!.Value.ToUniversalTime().AddTicks(-fiveHour.ResetsAt.Value.Ticks % TimeSpan.TicksPerSecond));

        var weekly = Assert.Single(windows, w => w.Label == "7d" && w.Scope is null);
        Assert.Equal(38.0, weekly.UsedPct);

        var scoped = Assert.Single(windows, w => w.Scope == "Fable");
        Assert.Equal("7d", scoped.Label);
        Assert.Equal(67.0, scoped.UsedPct);

        // unknown / codenamed keys must be ignored
        Assert.Equal(3, windows.Count);
    }

    [Fact]
    public void ParsesResetTimestampsInBothFormats()
    {
        using var iso = JsonDocument.Parse("\"2026-08-28T16:59:59+00:00\"");
        using var epoch = JsonDocument.Parse("1788452810");
        Assert.Equal(new DateTimeOffset(2026, 8, 28, 16, 59, 59, TimeSpan.Zero), ClaudeUsageClient.ParseResets(iso.RootElement));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788452810), ClaudeUsageClient.ParseResets(epoch.RootElement));
        Assert.Null(ClaudeUsageClient.ParseResets(null));
    }

    // rate_limits block of a token_count event in ~/.codex/sessions/**/rollout-*.jsonl
    private const string CodexRateLimits = """
        {"limit_id":"codex","limit_name":null,
         "primary":{"used_percent":69.0,"window_minutes":10080,"resets_at":1788452810},
         "secondary":{"used_percent":12.5,"window_minutes":300,"resets_in_seconds":3600},
         "credits":{"has_credits":false,"unlimited":false,"balance":"0"},"plan_type":"pro"}
        """;

    [Fact]
    public void ParsesCodexRateLimitsByWindowLength()
    {
        using var doc = JsonDocument.Parse(CodexRateLimits);
        var at = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero);
        var quota = CodexRolloutCollector.ParseRateLimits(doc.RootElement, at);

        Assert.Equal(Provider.Codex, quota.Provider);
        Assert.Equal("pro", quota.Plan);
        Assert.Equal("rollout", quota.Source);
        Assert.Equal(69.0, quota.Long!.UsedPct);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788452810), quota.Long.ResetsAt);
        Assert.Equal(12.5, quota.Short!.UsedPct);
        Assert.Equal(at.AddSeconds(3600), quota.Short.ResetsAt);
    }

    [Fact]
    public void SnapshotOrdersAttentionFirst()
    {
        var now = DateTimeOffset.UtcNow;
        AgentInfo Make(string key, AgentState state, DateTimeOffset since) => new()
        {
            Key = key, Provider = Provider.Claude, Name = key, Cwd = "/x/" + key, Host = "Term",
            State = state, StateSince = since, LastActivity = since, StartedAt = since,
        };
        var snap = new Snapshot
        {
            At = now,
            Agents = new[]
            {
                Make("idle", AgentState.Idle, now.AddMinutes(-1)),
                Make("working", AgentState.Working, now.AddMinutes(-2)),
                Make("waiting-new", AgentState.Waiting, now.AddMinutes(-1)),
                Make("waiting-old", AgentState.Waiting, now.AddMinutes(-10)),
                Make("ended", AgentState.Ended, now),
            },
        };
        Assert.Equal(new[] { "waiting-old", "waiting-new", "working", "idle" }, snap.Ordered().Select(a => a.Key));
        Assert.Equal(2, snap.Count(AgentState.Waiting));
    }
}
