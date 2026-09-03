using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Focus;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

public class WindowMatchingTests
{
    private static readonly string[] Names = { "api", "AcmeShopAPI" };

    [Theory]
    [InlineData("Terminal - AcmeShopAPI", 40)]                          // detached terminal of this project → best for Claude
    [InlineData("AcmeShopAPI", 20)]                                     // main IDE window
    [InlineData("AcmeShopAPI – ~/source/acme/api/README.md", 20)]
    [InlineData("Terminal - Other", 0)]
    [InlineData("Some window mentioning AcmeShopAPI", 10)]
    [InlineData("Codex - AcmeShopAPI", 30)]                             // another detached tool window
    public void TiersIdeWindowTitles(string title, int expected)
        => Assert.Equal(expected, WindowFocuser.TitleTier(title, "AcmeShopAPI", Provider.Claude));

    [Fact]
    public void DetachedTerminalBeatsMainWindowEvenWhenDirectoryNameDiffers()
    {
        var main = WindowFocuser.ScoreTitle("AcmeShopAPI", Names, Provider.Claude);
        var terminal = WindowFocuser.ScoreTitle("Terminal - AcmeShopAPI", Names, Provider.Claude);
        Assert.True(terminal > main);
    }

    [Fact]
    public void StructuralMatchOnLaterNameBeatsWeakMatchOnEarlierName()
    {
        // cwd basename "api" is generic; the IDE titles the window by the .idea project name.
        var mention = WindowFocuser.ScoreTitle("Some window mentioning api", Names, Provider.Claude);
        var terminal = WindowFocuser.ScoreTitle("Terminal - AcmeShopAPI", Names, Provider.Claude);
        Assert.True(terminal > mention);
    }

    [Fact]
    public void ProjectOwnNameOutranksAncestorDerivedName()
    {
        // cwd …/API/SiteVH.Api: the git root is the parent directory "API", which is also another
        // agent's project name. That other project's windows must not outrank this project's own —
        // neither its detached terminal (equal tier, resolved by name proximity) nor its main window.
        string[] names = { "SiteVH.Api", "API" };
        var ownTerminal = WindowFocuser.ScoreTitle("Terminal - SiteVH.Api", names, Provider.Claude);
        var ownMain = WindowFocuser.ScoreTitle("SiteVH.Api – local.settings.json", names, Provider.Claude);
        var otherTerminal = WindowFocuser.ScoreTitle("Terminal - API", names, Provider.Claude);
        Assert.True(ownTerminal > otherTerminal);
        Assert.True(ownMain > otherTerminal);
    }

    [Fact]
    public void ProjectNamesComeFromDirectoryIdeaSolutionAndGitRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aiam-names-" + Guid.NewGuid().ToString("N"));
        var cwd = Path.Combine(root, "src", "api");
        Directory.CreateDirectory(cwd);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(cwd, ".idea", ".idea.AcmeShopAPI"));
        File.WriteAllText(Path.Combine(cwd, "Portal.sln"), "");
        try
        {
            var names = WindowFocuser.ProjectNames(cwd);
            Assert.Equal("api", names[0]);
            Assert.Contains("AcmeShopAPI", names);
            Assert.Contains("Portal", names);
            Assert.Contains(Path.GetFileName(root), names);
        }
        finally { Directory.Delete(root, true); }
    }
}
