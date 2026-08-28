using Opendeck.AiAgentMonitor.Deck;
using Xunit;

namespace Opendeck.AiAgentMonitor.Tests;

public class DeckEventTests
{
    [Fact]
    public void ParsesKeyDownWithCoordinatesAndSettings()
    {
        var e = DeckEvent.Parse("""
            {"event":"keyDown","action":"com.josbol.aiagentmonitor.agent","context":"ulanzi-d200x.AI Agents.Keypad.5.0","device":"ulanzi-d200x",
             "payload":{"settings":{"slot":3,"provider":"codex"},"coordinates":{"row":1,"column":0},"controller":"Keypad","state":0,"isInMultiAction":false}}
            """);
        Assert.Equal("keyDown", e.Event);
        Assert.Equal("ulanzi-d200x.AI Agents.Keypad.5.0", e.Context);
        Assert.Equal("Keypad", e.Controller);
        Assert.Equal((1, 0), e.Coordinates);
        Assert.Equal(3, e.Settings.GetProperty("slot").GetInt32());
    }

    [Fact]
    public void ParsesDialRotateTicks()
    {
        var e = DeckEvent.Parse("""{"event":"dialRotate","action":"a","context":"c","device":"d","payload":{"settings":{},"coordinates":{"row":0,"column":1},"controller":"Encoder","ticks":-1,"pressed":false}}""");
        Assert.Equal(-1, e.Ticks);
        Assert.Equal("Encoder", e.Controller);
    }

    [Fact]
    public void ToleratesEventsWithoutPayload()
    {
        var e = DeckEvent.Parse("""{"event":"systemDidWakeUp"}""");
        Assert.Equal("systemDidWakeUp", e.Event);
        Assert.Null(e.Context);
        Assert.Null(e.Coordinates);
        Assert.Equal(0, e.Ticks);
    }
}
