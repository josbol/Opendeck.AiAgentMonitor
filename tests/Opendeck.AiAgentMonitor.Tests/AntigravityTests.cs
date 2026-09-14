using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Collectors;
using Opendeck.AiAgentMonitor.Focus;
using Opendeck.AiAgentMonitor.Hooks;
using Opendeck.AiAgentMonitor.Util;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

public sealed class AntigravityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aiam-agy-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "antigravity-cli");
    private readonly AntigravitySessionCollector _collector;
    private static readonly DateTimeOffset Now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    public AntigravityTests()
    {
        _collector = new AntigravitySessionCollector(Home) { DetectHost = _ => "Konsole" };
        Directory.CreateDirectory(_collector.SessionsDir);
    }

    [Theory]
    [InlineData("idle", false, AgentState.Idle)]
    [InlineData("thinking", false, AgentState.Working)]
    [InlineData("working", false, AgentState.Working)]
    [InlineData("tool_use", false, AgentState.Working)]
    [InlineData("initializing", false, AgentState.Working)]
    [InlineData("idle", true, AgentState.Waiting)]
    [InlineData("tool_use", true, AgentState.Waiting)]
    public void MapsDocumentedStatesAndConfirmationTakesPriority(string state, bool waiting, AgentState expected)
        => Assert.Equal(expected, AntigravitySessionCollector.ParseState(state, waiting));

    [Fact]
    public void UnsupportedStateIsNotAssumedIdleOrError()
        => Assert.Null(AntigravitySessionCollector.ParseState("new-unknown-state", false));

    [Fact]
    public void LiveRecordSuppliesModelContextAndFocusIdentity()
    {
        Save(State());
        var a = Assert.Single(_collector.Collect(Now));
        Assert.Equal("antigravity:conversation-1", a.Key);
        Assert.Equal(Provider.Antigravity, a.Provider);
        Assert.Equal("acme", a.ProjectName);
        Assert.Equal(AgentState.Waiting, a.State);
        Assert.Equal("confirmation", a.Detail);
        Assert.Equal(24.5, a.ContextPct);
        Assert.Equal(18000, a.ContextTokens);
        Assert.Equal("Gemini 3.5 Flash (High)", a.Model);
        Assert.Null(a.Approval);
        Assert.Equal(Environment.ProcessId, a.Pid);
        Assert.Equal(40, WindowFocuser.TitleTier("Terminal - acme", "acme", a.Provider));
    }

    [Theory]
    [InlineData("start_ticks", "reused-pid")]
    [InlineData("boot_id", "prior-boot")]
    [InlineData("session_id", "")]
    public void InvalidOwnerOrSessionIsIgnored(string field, string value)
    {
        var state = State(); state[field] = value; Save(state);
        Assert.Empty(_collector.Collect(Now));
    }

    [Fact]
    public void ExitRemovesAgentButRetainsQuotaObservationTime()
    {
        Save(State());
        Assert.Single(_collector.Collect(Now));
        _collector.IsProcessAlive = (_, _) => false;
        Assert.Empty(_collector.Collect(Now.AddHours(1)));
        Assert.Equal(Now, _collector.LatestQuota!.FetchedAt);
        Assert.Equal(70, _collector.LatestQuota.Primary!.UsedPct, 4);
    }

    [Fact]
    public void QuotaUsesMostConsumedBucketWithoutMixingOtherProviders()
    {
        Save(State()); _collector.Collect(Now);
        var q = _collector.LatestQuota!;
        Assert.Equal(Provider.Antigravity, q.Provider);
        Assert.Equal("gemini-weekly", q.Primary!.Scope);
        Assert.Equal(70, q.Primary.UsedPct, 4);
        Assert.Equal(Now.AddDays(2), q.Primary.ResetsAt);
        Assert.Equal("statusline", q.Source);
        var snap = new Snapshot { Agents = _collector.Collect(Now), At = Now, Antigravity = q };
        Assert.Same(q, snap.Quota(Provider.Antigravity));
        Assert.Null(snap.Quota(Provider.Copilot));
        Assert.Single(snap.Ordered(ProviderInfo.Parse("agy")));
        Assert.Equal(Provider.Antigravity, ProviderInfo.Parse("gemini")); // old key settings migrate
    }

    [Fact]
    public void MalformedStateDoesNotBreakOtherSessions()
    {
        Save(State()); File.WriteAllText(Path.Combine(_collector.SessionsDir, "broken.json"), "{");
        Assert.Single(_collector.Collect(Now));
    }

    [Fact]
    public void ContextAndQuotaPercentagesAreBounded()
    {
        var state = State(); state["context_pct"] = 120; state["context_tokens"] = -5;
        state["quota"] = JsonNode.Parse("""{"negative":{"remaining_fraction":-1},"overfull":{"remaining_fraction":2},"missing":{"reset_time":"later"}}""");
        Save(state);
        var a = Assert.Single(_collector.Collect(Now));
        Assert.Equal(100, a.ContextPct); Assert.Null(a.ContextTokens);
        Assert.Equal(new[] { 100d, 0d }, _collector.LatestQuota!.Windows.Select(w => w.UsedPct));
    }

    [Fact]
    public void InstallerPreservesSettingsAndRestoresOriginalStatusLine()
    {
        const string original = """{"colorScheme":"dark","statusLine":{"type":"command","command":"printf KEEP","padding":2,"enabled":true,"stack_with_default":true},"trustedWorkspaces":["/work/acme"]}""";
        var path = Path.Combine(Home, "settings.json"); File.WriteAllText(path, original);
        HookInstaller.InstallAntigravity(Home); HookInstaller.InstallAntigravity(Home);
        var installed = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal("dark", installed["colorScheme"]!.GetValue<string>());
        Assert.Equal(2, installed["statusLine"]!["padding"]!.GetValue<int>());
        Assert.True(installed["statusLine"]!["stack_with_default"]!.GetValue<bool>());
        var backup = JsonNode.Parse(File.ReadAllText(Path.Combine(Home, "aiagentmonitor", "previous-statusline.json")))!;
        Assert.Equal("printf KEEP", backup["statusLine"]!["command"]!.GetValue<string>());
        HookInstaller.UninstallAntigravity(Home);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(File.ReadAllText(path))));
    }

    [Fact]
    public void InstallerRemovesOnlyOldGeminiMonitorHooks()
    {
        var legacy = Path.Combine(_root, "settings.json");
        File.WriteAllText(legacy, """{"model":"keep","hooks":{"BeforeAgent":[{"hooks":[{"name":"aiagentmonitor-gemini","type":"command","command":"old"},{"name":"my-hook","type":"command","command":"keep"}]}]}}""");
        HookInstaller.InstallAntigravity(Home);
        var root = JsonNode.Parse(File.ReadAllText(legacy))!;
        Assert.Equal("keep", root["model"]!.GetValue<string>());
        Assert.Equal("my-hook", Assert.Single(root["hooks"]!["BeforeAgent"]![0]!["hooks"]!.AsArray())!["name"]!.GetValue<string>());
        Assert.NotEmpty(Directory.GetFiles(_root, "settings.json.bak-*"));
    }

    [Fact]
    public void UninstallDoesNotReplaceSubsequentUserChanges()
    {
        HookInstaller.InstallAntigravity(Home);
        var path = Path.Combine(Home, "settings.json");
        const string changed = """{"statusLine":{"command":"echo new choice"}}""";
        File.WriteAllText(path, changed);
        HookInstaller.UninstallAntigravity(Home);
        Assert.Equal(changed, File.ReadAllText(path));
    }

    [Fact]
    public async Task RealStatusCommandFindsAgyParentSanitizesPayloadAndChainsDisplay()
    {
        if (!OperatingSystem.IsLinux()) return;
        // A copied Python interpreter named agy models the native CLI's process tree (agy -> sh -> observer).
        // No real model invocation, login or external state is involved.
        var home = Path.Combine(_root, "space and ' quote", "antigravity-cli");
        Directory.CreateDirectory(home);
        File.WriteAllText(Path.Combine(home, "settings.json"), """{"statusLine":{"command":"printf KEEP"}}""");
        HookInstaller.InstallAntigravity(home);
        var command = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "settings.json")))!["statusLine"]!["command"]!.GetValue<string>();
        var fakeAgy = Path.Combine(_root, "agy");
        File.Copy(new FileInfo("/usr/bin/python3").ResolveLinkTarget(true)?.FullName ?? "/usr/bin/python3", fakeAgy);
        File.SetUnixFileMode(fakeAgy, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var payload = """{"conversation_id":"live-conversation","cwd":"/work/acme","agent_state":"tool_use","tool_confirmation_pending":true,"model":{"id":"gemini-3.5-flash"},"context_window":{"used_percentage":25,"current_usage":{"input_tokens":12000}},"quota":{"gemini-weekly":{"remaining_fraction":0.6}},"email":"PRIVATE_EMAIL","prompt":"PRIVATE_PROMPT","transcript_path":"PRIVATE_TRANSCRIPT"}""";
        var psi = new ProcessStartInfo(fakeAgy) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("import subprocess,sys,time; p=subprocess.run(sys.argv[1],shell=True,input=sys.argv[2],text=True,capture_output=True); print(p.stdout,flush=True); time.sleep(20)");
        psi.ArgumentList.Add(command); psi.ArgumentList.Add(payload);
        using var process = Process.Start(psi)!;
        try
        {
            Assert.Equal("KEEP", await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            var collector = new AntigravitySessionCollector(home);
            var a = Assert.Single(collector.Collect(DateTimeOffset.UtcNow));
            Assert.Equal(process.Id, a.Pid); Assert.Equal(AgentState.Waiting, a.State);
            Assert.Equal(25, a.ContextPct);
            var record = File.ReadAllText(Assert.Single(Directory.GetFiles(collector.SessionsDir, "*.json")));
            Assert.DoesNotContain("PRIVATE", record);
            Assert.Equal(40, collector.LatestQuota!.Primary!.UsedPct, 4);
            process.Kill(entireProcessTree: true); await process.WaitForExitAsync();
            Assert.Empty(collector.Collect(DateTimeOffset.UtcNow));
        }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
    }

    private JsonObject State() => new()
    {
        ["session_id"] = "conversation-1", ["pid"] = Environment.ProcessId,
        ["start_ticks"] = ProcUtil.ReadStat(Environment.ProcessId)!.Value.StartTicks, ["boot_id"] = _collector.BootId,
        ["cwd"] = "/work/acme", ["agent_state"] = "tool_use", ["tool_confirmation_pending"] = true,
        ["model"] = "Gemini 3.5 Flash (High)", ["context_pct"] = 24.5, ["context_tokens"] = 18000,
        ["observed_at"] = Now, ["started_at"] = Now.AddHours(-1), ["state_since"] = Now.AddMinutes(-1),
        ["plan_tier"] = "Pro", ["quota"] = new JsonObject
        {
            ["gemini-flash"] = new JsonObject { ["remaining_fraction"] = 0.8 },
            ["gemini-weekly"] = new JsonObject { ["remaining_fraction"] = 0.3, ["reset_time"] = Now.AddDays(2) },
        },
    };
    private void Save(JsonObject root) => File.WriteAllText(Path.Combine(_collector.SessionsDir, "state.json"), root.ToJsonString());
    public void Dispose() => Directory.Delete(_root, true);
}
