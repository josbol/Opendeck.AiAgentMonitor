using System.Text.Json;
using System.Text.Json.Nodes;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Hooks;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

public class ApprovalTests
{
    private static JsonElement Input(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Theory]
    [InlineData("Bash", """{"command":"git push origin main","description":"Push"}""", "Bash: git push origin main")]
    [InlineData("Edit", """{"file_path":"/home/u/src/Program.cs","old_string":"a","new_string":"b"}""", "Edit: Program.cs")]
    [InlineData("Bash", """{"command":["/bin/bash","-lc","dotnet test"]}""", "Bash: /bin/bash -lc dotnet test")]
    [InlineData("WebFetch", """{"url":"https://example.org/x"}""", "WebFetch: https://example.org/x")]
    [InlineData("Mystery", """{}""", "Mystery")]
    public void SummarizesToolCalls(string tool, string input, string expected)
        => Assert.Equal(expected, PendingApproval.Summarize(tool, Input(input)));

    [Fact]
    public void SummaryIsCappedAndSingleLine()
    {
        var s = PendingApproval.Summarize("Bash", Input("{\"command\":\"" + new string('x', 200) + "\\nsecond line\"}"));
        Assert.DoesNotContain('\n', s);
        Assert.True(s.Length <= "Bash: ".Length + 61);
        Assert.EndsWith("…", s);
    }

    [Fact]
    public void FullTextKeepsTheWholeCommandAndDescription()
    {
        var p = new PendingApproval
        {
            Id = "t", Provider = Provider.Codex, AgentKey = "codex:t", SessionId = "t", Cwd = "/p",
            ToolName = "Bash", Summary = "x",
            ToolInput = Input("""{"command":"docker run --rm postgres:18 && dotnet test tests/Core","description":"Start db, run tests"}"""),
        };
        var text = ApprovalNotifier.FullText(p);
        Assert.Equal("Bash:\ndocker run --rm postgres:18 && dotnet test tests/Core\n— Start db, run tests", text);
    }

    [Fact]
    public void RegistryResolvesOnceAndReleasesPerAgent()
    {
        var reg = new ApprovalRegistry();
        PendingApproval Make(string id, string agent) => new() { Id = id, Provider = Provider.Claude, AgentKey = agent, SessionId = agent, Cwd = "/p", ToolName = "Bash", Summary = id };
        var a1 = Make("a1", "claude:a"); var a2 = Make("a2", "claude:a"); var b1 = Make("b1", "claude:b");
        reg.Add(a1); reg.Add(a2); reg.Add(b1);

        Assert.Equal(3, reg.Pending.Count);
        Assert.True(reg.Resolve("b1", ApprovalOutcome.Allow));
        Assert.False(reg.Resolve("b1", ApprovalOutcome.Deny));           // already decided
        Assert.Equal(ApprovalOutcome.Allow, b1.Outcome.Result);

        Assert.Equal(2, reg.ReleaseAgent("claude:a"));
        Assert.Empty(reg.Pending);
        Assert.Equal(ApprovalOutcome.Release, a1.Outcome.Result);
        Assert.Equal(ApprovalOutcome.Release, a2.Outcome.Result);
    }

    [Fact]
    public void CodexTrustHashMatchesCodexFingerprint()
    {
        // Expected value produced by the reference implementation of codex-rs hook_hash + version_for_toml
        // (sha256 of the canonical JSON of the normalised handler); verified against an entry Codex 0.148 wrote itself.
        var handler = new JsonObject
        {
            ["type"] = "command",
            ["command"] = "AIAGENTMONITOR_PORT=43117 AIAGENTMONITOR_HOLD=35 '/opt/aiagentmonitor/hooks/codex-hook.sh'",
            ["timeout"] = 40,
            ["statusMessage"] = "Waiting for the deck (approve / deny)…",
        };
        Assert.Equal("sha256:7595d90f9bee02da878e9235d4482a379e3aa8f23cb146cc0e170b35da0ea27b", HookInstaller.CodexHookTrustHash("permission_request", handler));
    }

    [Fact]
    public void CodexTrustEntryFindsOurHandlerAndGroupIndex()
    {
        var root = JsonNode.Parse("""
            {"hooks":{"PermissionRequest":[
              {"hooks":[{"type":"command","command":"/somewhere/else.sh","timeout":10}]},
              {"hooks":[{"type":"command","command":"AIAGENTMONITOR_PORT=1 '/x/aiagentmonitor.sdPlugin/hooks/codex-hook.sh'","timeout":40,"statusMessage":"w"}]}
            ]}}
            """)!.AsObject();
        var (key, hash) = HookInstaller.CodexTrustEntry(root);
        Assert.EndsWith(":permission_request:1:0", key);
        Assert.StartsWith("sha256:", hash);
    }
}
