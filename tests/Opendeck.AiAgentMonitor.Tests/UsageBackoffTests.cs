using Opendeck.AiAgentMonitor.Collectors;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

public class UsageBackoffTests
{
    [Fact]
    public void BackoffDoublesPerFailureAndCapsAtOneHour()
    {
        var b = TimeSpan.FromMinutes(5);
        Assert.Equal(TimeSpan.Zero, ClaudeUsageClient.Backoff(0, b));
        Assert.Equal(TimeSpan.FromMinutes(10), ClaudeUsageClient.Backoff(1, b));
        Assert.Equal(TimeSpan.FromMinutes(20), ClaudeUsageClient.Backoff(2, b));
        Assert.Equal(TimeSpan.FromMinutes(40), ClaudeUsageClient.Backoff(3, b));
        Assert.Equal(TimeSpan.FromHours(1), ClaudeUsageClient.Backoff(4, b));
        Assert.Equal(TimeSpan.FromHours(1), ClaudeUsageClient.Backoff(20, b));
    }
}
