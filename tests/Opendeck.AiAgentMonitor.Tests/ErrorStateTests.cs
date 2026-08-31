using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Collectors;
using Opendeck.AiAgentMonitor.Util;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

/// <summary>
/// After an API error (no capacity, rate limit, auth) the session registry just says "idle";
/// the collector must pick the error up from the transcript tail and surface it as Error.
/// </summary>
public class ErrorStateTests : IDisposable
{
    private const string SessionId = "11111111-2222-3333-4444-555555555555";
    private const string Cwd = "/home/user/source/sample-project";

    // Trimmed transcript records in the shape Claude Code 2.1.x writes.
    private const string GoodAssistant = """{"type":"assistant","isSidechain":false,"timestamp":"2026-08-31T10:00:00.000Z","message":{"model":"claude-fable-5","role":"assistant","usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":890,"output_tokens":5},"content":[{"type":"text","text":"done"}]}}""";
    private const string CapacityError = """{"type":"assistant","isSidechain":false,"isApiErrorMessage":true,"error":"overloaded","apiErrorStatus":529,"timestamp":"2026-08-31T10:05:00.000Z","message":{"model":"<synthetic>","role":"assistant","usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0},"content":[{"type":"text","text":"The model does not currently have capacity available"}]}}""";
    private const string NewPrompt = """{"type":"user","isSidechain":false,"timestamp":"2026-08-31T10:10:00.000Z","message":{"role":"user","content":"try again"}}""";

    private readonly string _home = Directory.CreateTempSubdirectory("aimon-test-").FullName;
    private readonly ClaudeSessionCollector _collector;

    public ErrorStateTests() => _collector = new ClaudeSessionCollector(_home);

    [Fact]
    public void IdleSessionWithTrailingApiErrorBecomesError()
    {
        WriteTranscript(GoodAssistant, CapacityError);
        WriteSession("idle", updatedAtMs: 1000);

        var agent = Assert.Single(_collector.Collect(DateTimeOffset.UtcNow));
        Assert.Equal(AgentState.Error, agent.State);
        Assert.Equal("The model does not currently have capacity available", agent.Detail);
        Assert.Equal(new DateTimeOffset(2026, 8, 31, 10, 5, 0, TimeSpan.Zero), agent.StateSince);
        // the error record's synthetic all-zero usage must not clobber the context bar or the model
        Assert.Equal(1000, agent.ContextTokens);
        Assert.Equal("claude-fable-5", agent.Model);
    }

    [Fact]
    public void NewPromptAfterErrorClearsIt()
    {
        WriteTranscript(GoodAssistant, CapacityError, NewPrompt);
        WriteSession("idle", updatedAtMs: 2000);

        var agent = Assert.Single(_collector.Collect(DateTimeOffset.UtcNow));
        Assert.Equal(AgentState.Idle, agent.State);
        Assert.Null(agent.Detail);
    }

    [Fact]
    public void BusySessionKeepsWorkingWhileRetrying()
    {
        WriteTranscript(GoodAssistant, CapacityError);
        WriteSession("busy", updatedAtMs: 1000);

        var agent = Assert.Single(_collector.Collect(DateTimeOffset.UtcNow));
        Assert.Equal(AgentState.Working, agent.State);
    }

    [Fact]
    public void ErrorAgentsAlertAndOrderLikeWaiting()
    {
        var now = DateTimeOffset.UtcNow;
        AgentInfo Agent(string key, AgentState state) => new()
        {
            Key = key, Provider = Provider.Claude, Name = key, Cwd = "/x/" + key, Host = "Term",
            State = state, StateSince = now, LastActivity = now, StartedAt = now,
        };
        var snap = new Snapshot { Agents = new[] { Agent("a", AgentState.Idle), Agent("b", AgentState.Error), Agent("c", AgentState.Working) }, At = now };

        Assert.True(snap.Agents[1].NeedsAttention);
        Assert.Equal(new[] { "b", "c", "a" }, snap.Ordered().Select(a => a.Key));
    }

    // ---- Codex: a failed turn is a task_complete carrying terminal error details -------------
    // (shapes verified against real rollouts, Codex 0.148, 2026-08-31)

    private const string CodexStarted = """{"timestamp":"2026-08-30T17:25:58.129Z","type":"event_msg","payload":{"type":"task_started","turn_id":"01a053b4-e0eb-74f3-ae26-8ba4e8233795","started_at":1788110758,"model_context_window":258400}}""";
    private const string CodexFailed = """{"timestamp":"2026-08-30T17:26:00.951Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"01a053b4-e0eb-74f3-ae26-8ba4e8233795","last_agent_message":null,"error":{"message":"Selected model is at capacity. Please try a different model.","codex_error_info":"server_overloaded"},"started_at":1788110758,"completed_at":1788110760,"duration_ms":2822}}""";
    private const string CodexCompleted = """{"timestamp":"2026-08-30T17:41:18.813Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"01a053c2-a8e7-7dc2-9000-000000000000","last_agent_message":"done","started_at":1788111661,"completed_at":1788111678}}""";

    [Fact]
    public void CodexTaskCompleteWithErrorBecomesError()
    {
        var t = new CodexRolloutCollector.Thread { Path = "x" };
        CodexRolloutCollector.Apply(t, CodexStarted);
        Assert.Equal(AgentState.Working, t.State);
        CodexRolloutCollector.Apply(t, CodexFailed);
        Assert.Equal(AgentState.Error, t.State);
        Assert.Equal("Selected model is at capacity. Please try a different model.", t.Detail);
        Assert.Equal(new DateTimeOffset(2026, 8, 30, 17, 26, 0, 951, TimeSpan.Zero), t.StateSince);
    }

    [Fact]
    public void CodexNextTurnClearsTheError()
    {
        var t = new CodexRolloutCollector.Thread { Path = "x" };
        CodexRolloutCollector.Apply(t, CodexStarted);
        CodexRolloutCollector.Apply(t, CodexFailed);
        CodexRolloutCollector.Apply(t, CodexStarted);
        Assert.Equal(AgentState.Working, t.State);
        Assert.Null(t.Detail);
        CodexRolloutCollector.Apply(t, CodexCompleted);
        Assert.Equal(AgentState.Idle, t.State);
    }

    // ---- Codex: plan-mode completions and question endings wait on the user ------------------

    private const string CodexPlanStarted = """{"timestamp":"2026-08-31T14:16:32.100Z","type":"event_msg","payload":{"type":"task_started","turn_id":"01a05820-0000-7000-8000-000000000001","started_at":1788185792,"model_context_window":258400,"collaboration_mode_kind":"plan"}}""";
    private const string CodexPlanDone = """{"timestamp":"2026-08-31T14:54:34.500Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"01a05820-0000-7000-8000-000000000001","last_agent_message":"","started_at":1788185792,"completed_at":1788188074}}""";
    private const string CodexUserMsg = """{"timestamp":"2026-08-31T14:58:07.000Z","type":"event_msg","payload":{"type":"user_message","message":"PLEASE IMPLEMENT THIS PLAN:\n# Plan"}}""";

    [Fact]
    public void CodexPlanModeCompletionWaitsForTheUser()
    {
        var t = new CodexRolloutCollector.Thread { Path = "x" };
        CodexRolloutCollector.Apply(t, CodexPlanStarted);
        CodexRolloutCollector.Apply(t, CodexPlanDone);
        Assert.Equal(AgentState.Waiting, t.State);
        Assert.Equal("plan ready", t.Detail);
        CodexRolloutCollector.Apply(t, CodexUserMsg);
        Assert.Equal(AgentState.Working, t.State);
    }

    [Fact]
    public void CodexQuestionEndingWaitsButPlainEndingIdles()
    {
        static CodexRolloutCollector.Thread Run(string lastMessage)
        {
            var t = new CodexRolloutCollector.Thread { Path = "x" };
            CodexRolloutCollector.Apply(t, CodexStarted);
            var msg = System.Text.Json.JsonSerializer.Serialize(lastMessage);
            CodexRolloutCollector.Apply(t, """{"timestamp":"2026-08-31T15:00:00.000Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"01a053b4-e0eb-74f3-ae26-8ba4e8233795","last_agent_message":""" + msg + "}}");
            return t;
        }
        Assert.Equal((AgentState.Waiting, "question"), (Run("Done.\n\nShould I also update the docs?").State, Run("x?").Detail));
        Assert.Equal(AgentState.Waiting, Run("Which one do you prefer?**").State);   // trailing markdown
        Assert.Equal(AgentState.Idle, Run("All tests pass.").State);
        Assert.Equal(AgentState.Idle, Run("Is it fast? Yes.\nDone.").State);
    }

    [Fact]
    public void EndsWithQuestionHandlesEdges()
    {
        Assert.True(CodexRolloutCollector.EndsWithQuestion("Proceed?\n\n"));
        Assert.False(CodexRolloutCollector.EndsWithQuestion(null));
        Assert.False(CodexRolloutCollector.EndsWithQuestion("  \n "));
    }

    private void WriteSession(string status, long updatedAtMs)
    {
        var pid = Environment.ProcessId;
        var start = ProcUtil.ReadStat(pid)!.Value.StartTicks;
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        File.WriteAllText(Path.Combine(_home, "sessions", pid + ".json"),
            $$"""{"pid":{{pid}},"procStart":"{{start}}","sessionId":"{{SessionId}}","cwd":"{{Cwd}}","status":"{{status}}","kind":"interactive","startedAt":500,"updatedAt":{{updatedAtMs}},"statusUpdatedAt":{{updatedAtMs}}}""");
    }

    private void WriteTranscript(params string[] lines)
    {
        var path = _collector.TranscriptPath(SessionId, Cwd);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }
}
