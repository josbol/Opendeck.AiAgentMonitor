using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Collectors;
using Opendeck.AiAgentMonitor.Hooks;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

/// <summary>
/// GitHub Copilot sessions live in ~/.copilot/session-state/&lt;id&gt;/ (workspace.yaml, inuse.&lt;pid&gt;.lock, events.jsonl).
/// Record shapes below are trimmed from real files (Copilot CLI 1.0.82 and the JetBrains plugin's engine 1.0.78, 2026-09-02).
/// </summary>
public class CopilotCollectorTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("aimon-copilot-").FullName;
    private readonly CopilotSessionCollector _collector;

    public CopilotCollectorTests()
    {
        _collector = new CopilotSessionCollector(_home)
        {
            IsCopilotProcess = pid => pid == Environment.ProcessId,   // the lock names our own pid
            DetectHost = _ => "Term",
        };
    }

    private const string SessionStart = """{"type":"session.start","data":{"sessionId":"S","version":1,"producer":"copilot-agent","copilotVersion":"1.0.82","startTime":"2026-09-02T23:42:05.745Z","selectedModel":"auto","context":{"cwd":"/home/user/source/acme-api","branch":"dev"}},"id":"e1","timestamp":"2026-09-02T23:42:05.756Z","parentId":null}""";
    private const string UserMessage = """{"type":"user.message","data":{"content":"Push the fix to main","delivery":"idle"},"id":"e2","timestamp":"2026-09-02T23:42:07.784Z","parentId":null}""";
    private const string TurnStart = """{"type":"assistant.turn_start","data":{"turnId":"0","interactionId":"i1"},"id":"e3","timestamp":"2026-09-02T23:42:07.798Z","parentId":"e2"}""";
    private const string AssistantMessage = """{"type":"assistant.message","data":{"messageId":"m1","model":"gpt-5.4-mini","content":"Pushing.","toolRequests":[],"turnId":"0","phase":"final_answer"},"id":"e4","timestamp":"2026-09-02T23:42:09.968Z","parentId":"e3"}""";
    private const string PermissionRequested = """{"type":"permission.requested","data":{"requestId":"r1","permissionRequest":{"kind":"shell","toolCallId":"c1","intention":"Push the branch","command":"git push origin main"}},"id":"e5","timestamp":"2026-09-02T23:42:10.000Z","parentId":"e3"}""";
    private const string PermissionCompleted = """{"type":"permission.completed","data":{"requestId":"r1","toolCallId":"c1","result":{"kind":"approved"}},"id":"e6","timestamp":"2026-09-02T23:42:20.000Z","parentId":"e5"}""";
    private const string TurnEnd = """{"type":"assistant.turn_end","data":{"turnId":"0"},"id":"e7","timestamp":"2026-09-02T23:42:25.000Z","parentId":"e3"}""";

    [Fact]
    public void PermissionRequestWaitsUntilAnsweredAndTurnEndIdles()
    {
        var dir = NewSession("11111111-aaaa-bbbb-cccc-000000000001", "/home/user/source/acme-api", "github/cli", SessionStart, UserMessage, TurnStart, AssistantMessage, PermissionRequested);
        var now = new DateTimeOffset(2026, 9, 2, 23, 42, 30, TimeSpan.Zero);

        var a = Assert.Single(_collector.Collect(now));
        Assert.Equal("copilot:11111111-aaaa-bbbb-cccc-000000000001", a.Key);
        Assert.Equal(Provider.Copilot, a.Provider);
        Assert.Equal(AgentState.Waiting, a.State);
        Assert.Equal("shell: git push origin main", a.Detail);
        Assert.Equal("gpt-5.4-mini", a.Model);
        Assert.Equal("acme-api", a.ProjectName);
        Assert.Equal("Push the fix to main", a.Title);
        Assert.Equal(new DateTimeOffset(2026, 9, 2, 23, 42, 10, TimeSpan.Zero), a.StateSince);

        File.AppendAllText(Path.Combine(dir, "events.jsonl"), PermissionCompleted + "\n");
        a = Assert.Single(_collector.Collect(now));
        Assert.Equal(AgentState.Working, a.State);
        Assert.Null(a.Detail);

        File.AppendAllText(Path.Combine(dir, "events.jsonl"), TurnEnd + "\n");
        a = Assert.Single(_collector.Collect(now));
        Assert.Equal(AgentState.Idle, a.State);
    }

    [Fact]
    public void QuestionToolWaitsUntilItCompletes()
    {
        const string ask = """{"type":"tool.execution_start","data":{"toolCallId":"q1","toolName":"ask_user","arguments":{"question":"Which branch?"},"turnId":"0"},"id":"e8","timestamp":"2026-09-02T23:42:11.000Z","parentId":null}""";
        const string answered = """{"type":"tool.execution_complete","data":{"toolCallId":"q1","success":true,"result":{"content":"main"}},"id":"e9","timestamp":"2026-09-02T23:42:40.000Z","parentId":null}""";
        var dir = NewSession("11111111-aaaa-bbbb-cccc-000000000002", "/home/user/source/acme-api", "github/cli", SessionStart, UserMessage, TurnStart, ask);
        var now = new DateTimeOffset(2026, 9, 2, 23, 43, 0, TimeSpan.Zero);

        var a = Assert.Single(_collector.Collect(now));
        Assert.Equal(AgentState.Waiting, a.State);
        Assert.Equal("question", a.Detail);

        File.AppendAllText(Path.Combine(dir, "events.jsonl"), answered + "\n");
        Assert.Equal(AgentState.Working, Assert.Single(_collector.Collect(now)).State);
    }

    [Fact]
    public void FailedTurnBecomesError()
    {
        const string failed = """{"type":"assistant.turn_end","data":{"turnId":"0","status":"error","error":{"message":"The model is at capacity"}},"id":"e7","timestamp":"2026-09-02T23:42:25.000Z","parentId":"e3"}""";
        NewSession("11111111-aaaa-bbbb-cccc-000000000003", "/home/user/source/acme-api", "github/cli", SessionStart, UserMessage, TurnStart, failed);
        var a = Assert.Single(_collector.Collect(new DateTimeOffset(2026, 9, 2, 23, 43, 0, TimeSpan.Zero)));
        Assert.Equal(AgentState.Error, a.State);
        Assert.Equal("The model is at capacity", a.Detail);
    }

    [Fact]
    public void SessionWithoutALiveProcessIsIgnored()
    {
        NewSession("11111111-aaaa-bbbb-cccc-000000000004", "/home/user/source/acme-api", "github/cli", SessionStart, UserMessage, TurnStart);
        _collector.IsCopilotProcess = _ => false;
        Assert.Empty(_collector.Collect(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void SessionAfterShutdownIsIgnored()
    {
        const string shutdown = """{"type":"session.shutdown","data":{"shutdownType":"routine","totalPremiumRequests":1},"id":"e9","timestamp":"2026-09-02T23:42:10.054Z","parentId":null}""";
        NewSession("11111111-aaaa-bbbb-cccc-000000000005", "/home/user/source/acme-api", "github/cli", SessionStart, UserMessage, TurnStart, TurnEnd, shutdown);
        Assert.Empty(_collector.Collect(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void OneAgentPerProcessNewestWins()
    {
        // the JetBrains engine keeps the earlier sessions of a chat open under the same pid
        NewSession("11111111-aaaa-bbbb-cccc-000000000006", "/home/user/source/acme-api", "copilot-intellij", SessionStart, UserMessage, TurnStart, TurnEnd);
        NewSession("11111111-aaaa-bbbb-cccc-000000000007", "/home/user/source/acme-api", "copilot-intellij", SessionStart.Replace("23:42:05", "23:50:05"), UserMessage.Replace("23:42:07", "23:50:07"), TurnStart.Replace("23:42:07", "23:50:07"));
        var a = Assert.Single(_collector.Collect(new DateTimeOffset(2026, 9, 2, 23, 51, 0, TimeSpan.Zero)));
        Assert.Equal("copilot:11111111-aaaa-bbbb-cccc-000000000007", a.Key);
        Assert.Equal(AgentState.Working, a.State);
        Assert.Equal("Rider", a.Host);   // client_name copilot-intellij when the process tree says nothing
    }

    [Fact]
    public void StuckTurnFallsBackToIdle()
    {
        NewSession("11111111-aaaa-bbbb-cccc-000000000008", "/home/user/source/acme-api", "github/cli", SessionStart, UserMessage, TurnStart);
        var a = Assert.Single(_collector.Collect(new DateTimeOffset(2026, 9, 3, 1, 0, 0, TimeSpan.Zero)));
        Assert.Equal(AgentState.Idle, a.State);
    }

    [Fact]
    public void UsageCheckpointGivesTheContextSize()
    {
        // trimmed from a CLI 1.0.82 session: the main conversation's last request was 19 796 prompt tokens
        const string checkpoint = """{"type":"session.usage_checkpoint","data":{"totalNanoAiu":5035250000,"totalPremiumRequests":1,"promptCacheBreakState":[{"conversation":"main","models":{"gpt-5.6-terra":{"model":"gpt-5.6-terra","vendor":"openai","prompt_tokens":19796,"tool_tokens":11869,"initiator":"user"}}}]},"id":"e9","timestamp":"2026-09-03T00:53:24.668Z","parentId":null}""";
        NewSession("11111111-aaaa-bbbb-cccc-000000000009", "/home/user/source/acme-api", "github/cli", SessionStart, UserMessage, TurnStart, AssistantMessage, TurnEnd, checkpoint);
        var a = Assert.Single(_collector.Collect(new DateTimeOffset(2026, 9, 3, 0, 54, 0, TimeSpan.Zero)));
        Assert.Equal(19796, a.ContextTokens);
        Assert.Null(a.ContextPct);
    }

    [Fact]
    public void ParsesWorkspaceYaml()
    {
        var y = CopilotSessionCollector.ParseYaml("id: abc\ncwd: /home/user/source/acme-api\nclient_name: copilot-intellij\nname: \"at Foo.Bar()  \\n  at Baz()\"\nuser_named: false\ncreated_at: 2026-09-02T23:52:18.831Z\n");
        Assert.Equal("/home/user/source/acme-api", y["cwd"]);
        Assert.Equal("copilot-intellij", y["client_name"]);
        Assert.Equal("at Foo.Bar()  \n  at Baz()", y["name"]);
    }

    [Fact]
    public void SummarisesPermissionRequests()
    {
        var shell = JsonDocument.Parse("""{"requestId":"r","permissionRequest":{"kind":"shell","command":"rm -rf build","intention":"Clean"}}""").RootElement;
        Assert.Equal("shell: rm -rf build", CopilotSessionCollector.PermissionSummary(shell));
        var write = JsonDocument.Parse("""{"requestId":"r","permissionRequest":{"kind":"write","path":"/home/user/source/acme-api/src/Program.cs"}}""").RootElement;
        Assert.Equal("write: Program.cs", CopilotSessionCollector.PermissionSummary(write));
        var read = JsonDocument.Parse("""{"requestId":"r","permissionRequest":{"kind":"read","intention":"Search in directory: /x","path":"/x"}}""").RootElement;
        Assert.Equal("read: Search in directory: /x", CopilotSessionCollector.PermissionSummary(read));
    }

    private string NewSession(string id, string cwd, string client, params string[] events)
    {
        var dir = Path.Combine(_home, "session-state", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), $"id: {id}\ncwd: {cwd}\ngit_root: {cwd}\nbranch: dev\nclient_name: {client}\nname: Push the fix to main\nuser_named: false\ncreated_at: 2026-09-02T23:42:05.754Z\nupdated_at: 2026-09-02T23:42:07.479Z\n");
        File.WriteAllText(Path.Combine(dir, $"inuse.{Environment.ProcessId}.lock"), Environment.ProcessId + "\n");
        File.WriteAllText(Path.Combine(dir, "events.jsonl"), string.Join('\n', events) + "\n");
        return dir;
    }

    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }
}

public class CopilotHookTests
{
    [Fact]
    public void ShellRequestIsHeldAndAnsweredInCopilotShape()
    {
        var root = JsonDocument.Parse("""{"sessionId":"S1","timestamp":1788393142894,"cwd":"/home/user/source/acme-api","toolName":"shell","toolInput":{"kind":"shell","command":"git push origin main","intention":"Push"}}""").RootElement;
        var p = HookServer.ParseRequest(Provider.Copilot, root, out var passThrough);
        Assert.Null(passThrough);
        Assert.Equal("copilot:S1", p.AgentKey);
        Assert.Equal("shell", p.ToolName);
        Assert.Equal("shell: git push origin main", p.Summary);
        Assert.Equal("""{"behavior":"allow"}""", Util.Json.Serialize(HookServer.Decision(Provider.Copilot, "allow", null)));
        Assert.Equal("""{"behavior":"deny","message":"no"}""", Util.Json.Serialize(HookServer.Decision(Provider.Copilot, "deny", "no")));
        Assert.Contains("hookSpecificOutput", Util.Json.Serialize(HookServer.Decision(Provider.Claude, "allow", null)));
    }

    [Fact]
    public void ReadRequestsAndQuestionsPassThrough()
    {
        var read = JsonDocument.Parse("""{"sessionId":"S1","cwd":"/x","permissionRequest":{"kind":"read","path":"/etc/hosts","intention":"Read hosts"}}""").RootElement;
        HookServer.ParseRequest(Provider.Copilot, read, out var why);
        Assert.NotNull(why);
        var ask = JsonDocument.Parse("""{"sessionId":"S1","cwd":"/x","toolName":"ask_user","toolInput":{"question":"Which?"}}""").RootElement;
        HookServer.ParseRequest(Provider.Copilot, ask, out why);
        Assert.Equal("interactive", why);
        var claude = JsonDocument.Parse("""{"session_id":"C1","cwd":"/x","tool_name":"Bash","tool_input":{"command":"ls"}}""").RootElement;
        var p = HookServer.ParseRequest(Provider.Claude, claude, out why);
        Assert.Null(why);
        Assert.Equal("claude:C1", p.AgentKey);
        Assert.Equal("Bash: ls", p.Summary);
    }
}

public class CopilotUsageParsingTests
{
    [Fact]
    public void PremiumRequestSnapshotBecomesAMonthlyWindow()
    {
        var json = """{"copilot_plan":"individual","quota_reset_date":"2026-10-01","quota_snapshots":{"chat":{"unlimited":true},"completions":{"unlimited":true},"premium_interactions":{"entitlement":300,"remaining":262,"percent_remaining":87.33,"unlimited":false,"overage_permitted":false}}}""";
        using var doc = JsonDocument.Parse(json);
        var (windows, plan) = CopilotUsageClient.Parse(doc.RootElement);
        var w = Assert.Single(windows);
        Assert.Equal("month", w.Label);
        Assert.Equal(12.67, w.UsedPct, 2);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), w.ResetsAt);
        Assert.Equal("individual", plan);

        var q = new ProviderQuota { Provider = Provider.Copilot, Windows = windows, FetchedAt = DateTimeOffset.UtcNow };
        Assert.Same(w, q.Primary);
        Assert.Null(q.Secondary);
    }

    [Fact]
    public void UnlimitedPlansReportNoWindow()
    {
        using var doc = JsonDocument.Parse("""{"copilot_plan":"business","quota_snapshots":{"premium_interactions":{"unlimited":true}}}""");
        Assert.Empty(CopilotUsageClient.Parse(doc.RootElement).Windows);
    }
}
