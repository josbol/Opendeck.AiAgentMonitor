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
    public void SummarizesCodexPatchesByTouchedFiles()
    {
        var patch = "*** Begin Patch\n*** Update File: /home/u/src/AcmeShop/Program.cs\n@@\n-a\n+b\n*** Add File: docs/notes.md\n+hi\n*** Delete File: old.txt\n*** End Patch";
        var input = Input(JsonSerializer.Serialize(new { input = patch }));
        Assert.Equal("apply_patch: Update AcmeShop/Program.cs, Add docs/notes.md (+1 more)", PendingApproval.Summarize("apply_patch", input));
        Assert.Equal(new[] { "Update AcmeShop/Program.cs", "Add docs/notes.md", "Delete old.txt" }, PendingApproval.PatchFiles(patch));
        var p = new PendingApproval { Id = "t", Provider = Provider.Codex, AgentKey = "codex:t", SessionId = "t", Cwd = "/p", ToolName = "apply_patch", Summary = "x", ToolInput = input };
        Assert.StartsWith("apply_patch:\nUpdate AcmeShop/Program.cs\nAdd docs/notes.md\nDelete old.txt\n\n*** Begin Patch", ApprovalNotifier.FullText(p));
    }

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
    public async Task InteractiveToolsPassStraightThroughWhileOthersAreHeld()
    {
        var reg = new ApprovalRegistry();
        using var server = new HookServer(reg) { HoldTime = () => TimeSpan.FromMilliseconds(400) };
        int port;
        using (var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
        {
            l.Start(); port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port; l.Stop();
        }
        server.Start(port);
        using var http = new HttpClient();
        for (var i = 0; ; i++)
        {
            try { await http.GetAsync($"http://127.0.0.1:{port}/health"); break; }
            catch when (i < 50) { await Task.Delay(100); }
        }

        // a question can only be answered in the terminal: not held, not registered
        var question = """{"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"/p","tool_name":"AskUserQuestion","tool_input":{"questions":[]}}""";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await http.PostAsync($"http://127.0.0.1:{port}/hooks/claude", new StringContent(question));
        sw.Stop();
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        Assert.Empty(await resp.Content.ReadAsStringAsync());   // empty reply → normal terminal dialog
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(300), $"question was held for {sw.Elapsed}");
        Assert.Empty(reg.Pending);

        // a Bash call is still held for the deck until the hold time passes
        var bash = """{"hook_event_name":"PermissionRequest","session_id":"s1","cwd":"/p","tool_name":"Bash","tool_input":{"command":"ls"}}""";
        sw.Restart();
        resp = await http.PostAsync($"http://127.0.0.1:{port}/hooks/claude", new StringContent(bash));
        sw.Stop();
        Assert.Empty(await resp.Content.ReadAsStringAsync());
        Assert.True(sw.Elapsed >= TimeSpan.FromMilliseconds(350), $"bash was released after only {sw.Elapsed}");
        Assert.Empty(reg.Pending);
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
