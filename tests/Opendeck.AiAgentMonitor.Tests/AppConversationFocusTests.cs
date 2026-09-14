using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Focus;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

public class AppConversationFocusTests
{
    private static AgentInfo AppAgent(string? sessionId) => new()
    {
        Key = $"codex:{sessionId}", Provider = Provider.Codex, Host = "App",
        SessionId = sessionId, Pid = 1234, Name = "Same project", Cwd = "/projects/demo",
        State = AgentState.Idle, StateSince = DateTimeOffset.UtcNow,
        LastActivity = DateTimeOffset.UtcNow, StartedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task ConversationsSharingWindowAndProjectDispatchDistinctLinksOnEveryPress()
    {
        var first = AppAgent("11111111-1111-4111-8111-111111111111");
        var second = AppAgent("22222222-2222-4222-8222-222222222222");
        var calls = new List<string>();
        Task<bool> Launch(string executable, string[] args)
        {
            Assert.Equal("chatgpt", executable);
            calls.Add(Assert.Single(args));
            return Task.FromResult(true);
        }

        // Switching back must dispatch again: the user may have changed chats inside the app.
        foreach (var agent in new[] { first, second, first })
            Assert.True(await WindowFocuser.SelectAppThreadAsync(agent, false, Launch));

        Assert.Equal(new[]
        {
            "codex://threads/11111111-1111-4111-8111-111111111111",
            "codex://threads/22222222-2222-4222-8222-222222222222",
            "codex://threads/11111111-1111-4111-8111-111111111111",
        }, calls);
    }

    [Fact]
    public async Task DryRunDoesNotLaunchOrNavigate()
    {
        Assert.True(await WindowFocuser.SelectAppThreadAsync(
            AppAgent("11111111-1111-4111-8111-111111111111"), true,
            (_, _) => throw new InvalidOperationException("Dry run launched the app")));
    }

    [Theory]
    [InlineData(Provider.Codex, "Term")]
    [InlineData(Provider.Codex, "Rider")]
    [InlineData(Provider.Codex, "VS Code")]
    [InlineData(Provider.Claude, "App")]
    [InlineData(Provider.Copilot, "Rider")]
    public async Task OtherHostsAndProvidersKeepTheirExistingFocusBehavior(Provider provider, string host)
    {
        Assert.True(await WindowFocuser.SelectAppThreadAsync(AppAgent(null) with { Provider = provider, Host = host },
            false, (_, _) => throw new InvalidOperationException("Unrelated agent opened in app")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("new")]
    [InlineData("../settings")]
    [InlineData("11111111-1111-4111-8111-111111111111?prompt=hello")]
    public async Task MissingOrMalformedSessionDoesNotOpenAnotherRoute(string? sessionId)
    {
        Assert.False(await WindowFocuser.SelectAppThreadAsync(AppAgent(sessionId), false,
            (_, _) => throw new InvalidOperationException("Invalid session launched the app")));
    }

    [Fact]
    public async Task FailedLaunchIsNotReportedAsSuccessfulSelection()
    {
        Assert.False(await WindowFocuser.SelectAppThreadAsync(
            AppAgent("11111111-1111-4111-8111-111111111111"), false, (_, _) => Task.FromResult(false)));
    }
}
