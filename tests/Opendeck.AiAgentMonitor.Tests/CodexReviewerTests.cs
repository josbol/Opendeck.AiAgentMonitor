using Opendeck.AiAgentMonitor.Collectors;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

/// <summary>
/// The hook server needs to know whether a Codex thread's permission requests are screened by the
/// Guardian auto-reviewer (ChatGPT app auto mode) or prompted to the user: turn_context records carry
/// approvals_reviewer, and the latest one wins (the app switches modes within a thread).
/// </summary>
public class CodexReviewerTests : IDisposable
{
    private const string ThreadId = "01a04b53-dbd7-7160-8b0e-818cb7d82636";

    private readonly string _home = Directory.CreateTempSubdirectory("aimon-codex-test-").FullName;
    private readonly CodexRolloutCollector _collector;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public CodexReviewerTests() => _collector = new CodexRolloutCollector(_home);

    private string Meta(int minutesAgo) =>
        $$$"""{"timestamp":"{{{Ts(minutesAgo)}}}","type":"session_meta","payload":{"id":"{{{ThreadId}}}","cwd":"/home/user/project","originator":"codex_desktop"}}""";

    private string TurnContext(int minutesAgo, string? reviewer)
    {
        var fields = reviewer is null ? "" : $"""
            "approval_policy":"on-request","approvals_reviewer":"{reviewer}",
            """.Trim();
        return $$$"""{"timestamp":"{{{Ts(minutesAgo)}}}","type":"turn_context","payload":{"cwd":"/home/user/project",{{{fields}}}"model":"gpt-5"}}""";
    }

    private string Ts(int minutesAgo) => _now.AddMinutes(-minutesAgo).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");

    [Fact]
    public void LatestTurnContextReviewerWins()
    {
        WriteRollout(Meta(30), TurnContext(20, "auto_review"), TurnContext(5, "user"));

        _collector.Collect(_now);
        Assert.Equal("user", _collector.ApprovalsReviewer(ThreadId));
    }

    [Fact]
    public void AutoReviewIsReportedAndUnknownThreadsAreNull()
    {
        WriteRollout(Meta(30), TurnContext(5, "auto_review"));

        _collector.Collect(_now);
        Assert.Equal("auto_review", _collector.ApprovalsReviewer(ThreadId));
        Assert.Null(_collector.ApprovalsReviewer("ffffffff-0000-0000-0000-000000000000"));
    }

    [Fact]
    public void ReviewerlessTurnContextKeepsTheLastKnownValue()
    {
        WriteRollout(Meta(30), TurnContext(20, "auto_review"), TurnContext(5, reviewer: null));

        _collector.Collect(_now);
        Assert.Equal("auto_review", _collector.ApprovalsReviewer(ThreadId));
    }

    private void WriteRollout(params string[] lines)
    {
        var dir = Path.Combine(_home, "sessions", _now.ToString("yyyy"), _now.ToString("MM"), _now.ToString("dd"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"rollout-{_now:yyyy-MM-dd}T01-00-00-{ThreadId}.jsonl"), string.Join("\n", lines) + "\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
    }
}
