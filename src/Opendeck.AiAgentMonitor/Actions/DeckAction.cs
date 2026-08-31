using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Deck;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Actions;

/// <summary>One placed instance of an action on the deck.</summary>
public abstract class DeckAction
{
    public required string Context { get; init; }
    public required string ActionUuid { get; init; }
    public required PluginHost Host { get; init; }
    public string? Device { get; set; }
    public string? Controller { get; set; }
    public JsonElement Settings { get; set; }
    public string? LastImage { get; set; }

    public virtual Task OnAppearAsync() => Task.CompletedTask;
    public virtual Task OnKeyDownAsync(DeckEvent e) => Task.CompletedTask;
    public virtual Task OnKeyUpAsync(DeckEvent e) => Task.CompletedTask;
    public virtual Task OnDialRotateAsync(int ticks) => Task.CompletedTask;
    public virtual Task OnDialDownAsync() => Task.CompletedTask;
    public virtual Task OnDialUpAsync() => Task.CompletedTask;
    public virtual Task OnSendToPluginAsync(JsonElement payload) => Task.CompletedTask;

    /// <summary>Returns the image (data URL) for the current snapshot, or null to leave the key alone.</summary>
    public abstract string? Render(Snapshot snapshot, DateTimeOffset now);

    protected string SettingString(string name, string fallback) => Settings.Str(name) is { Length: > 0 } s ? s : fallback;
    protected int SettingInt(string name, int fallback) => (int)(Settings.Long(name) ?? fallback);
    protected Provider? SettingProvider(string name) => SettingString(name, "auto").ToLowerInvariant() switch { "claude" => Provider.Claude, "codex" => Provider.Codex, _ => null };
}

/// <summary>Shows one agent: the N-th in attention-first order (auto) or filtered by provider.</summary>
public sealed class AgentSlotAction : DeckAction
{
    private AgentInfo? _shown;

    public override string? Render(Snapshot s, DateTimeOffset now)
    {
        var slot = Math.Max(1, SettingInt("slot", 1));
        var list = s.Ordered(SettingProvider("provider"));
        _shown = slot <= list.Count ? list[slot - 1] : null;
        return _shown is null ? Host.Renderer.EmptySlot(slot, SettingProvider("provider")) : Host.Renderer.AgentKey(_shown, now);
    }

    public override async Task OnKeyUpAsync(DeckEvent e)
    {
        if (_shown is null) { Host.Deck.ShowAlert(Context); return; }
        if (!await Host.FocusAsync(_shown)) Host.Deck.ShowAlert(Context); else Host.Deck.ShowOk(Context);
    }
}

/// <summary>Rate-limit windows for one provider.</summary>
public sealed class QuotaAction : DeckAction
{
    public override string? Render(Snapshot s, DateTimeOffset now)
    {
        var p = SettingProvider("provider") ?? Provider.Claude;
        return Host.Renderer.QuotaKey(p, p == Provider.Claude ? s.Claude : s.Codex, now);
    }

    public override Task OnKeyUpAsync(DeckEvent e)
    {
        var p = SettingProvider("provider") ?? Provider.Claude;
        Host.Deck.OpenUrl(p == Provider.Claude ? "https://claude.ai/settings/usage" : "https://chatgpt.com/codex/settings/usage");
        Host.RequestUsageRefresh();
        return Task.CompletedTask;
    }
}

/// <summary>Counts of working / waiting / idle agents.</summary>
public sealed class OverviewAction : DeckAction
{
    public override string? Render(Snapshot s, DateTimeOffset now) => Host.Renderer.OverviewKey(s, now);

    public override async Task OnKeyUpAsync(DeckEvent e)
    {
        // jump to the agent that needs attention most, if any
        var target = Host.Monitor.Current.Ordered().FirstOrDefault();
        if (target is not null && target.NeedsAttention) { await Host.FocusAsync(target); return; }
        Host.RequestUsageRefresh();
        Host.Deck.ShowOk(Context);
    }
}

/// <summary>Small key for the main layout: shows how many agents need you; press switches to the monitor profile
/// (or back to the main profile when mode = "back").</summary>
public sealed class AttentionAction : DeckAction
{
    public override string? Render(Snapshot s, DateTimeOffset now) => Host.Renderer.AttentionKey(s, now, IsBack);
    private bool IsBack => SettingString("mode", "monitor") == "back";

    public override async Task OnKeyUpAsync(DeckEvent e)
    {
        var profile = SettingString("profile", IsBack ? Host.Settings.MainProfile : Host.Settings.MonitorProfile);
        var device = Device ?? e.Device;
        if (device is null) { Host.Deck.ShowAlert(Context); return; }
        var ok = await Host.SwitchProfileAsync(device, profile);
        if (!ok) Host.Deck.ShowAlert(Context);
    }
}

/// <summary>Shows the agent currently selected with the dial.</summary>
public sealed class SelectedAgentAction : DeckAction
{
    public override string? Render(Snapshot s, DateTimeOffset now)
    {
        var list = s.Ordered();
        if (list.Count == 0) return Host.Renderer.MessageKey("no agents", "nothing selected");
        var idx = Host.SelectedIndex(list);
        return Host.Renderer.AgentKey(list[idx], now, idx + 1, list.Count);
    }

    public override async Task OnKeyUpAsync(DeckEvent e)
    {
        var list = Host.Monitor.Current.Ordered();
        if (list.Count == 0) return;
        var a = list[Host.SelectedIndex(list)];
        if (!await Host.FocusAsync(a)) Host.Deck.ShowAlert(Context); else Host.Deck.ShowOk(Context);
    }
}

/// <summary>Encoder: rotate to move the selection, press to focus the selected agent's window.</summary>
public sealed class SelectDialAction : DeckAction
{
    public override string? Render(Snapshot s, DateTimeOffset now) => null; // dials on the D200X have no display

    public override Task OnDialRotateAsync(int ticks)
    {
        var list = Host.Monitor.Current.Ordered();
        if (list.Count == 0) return Task.CompletedTask;
        var idx = Host.SelectedIndex(list);
        idx = ((idx + ticks) % list.Count + list.Count) % list.Count;
        Host.Select(list[idx].Key);
        return Task.CompletedTask;
    }

    public override async Task OnDialUpAsync()
    {
        var list = Host.Monitor.Current.Ordered();
        if (list.Count == 0) return;
        await Host.FocusAsync(list[Host.SelectedIndex(list)]);
    }

    public override Task OnKeyUpAsync(DeckEvent e) => OnDialUpAsync();
}

/// <summary>Approve / Deny key: answers the permission request of the selected agent (or the most urgent one).</summary>
public sealed class DecisionAction : DeckAction
{
    public required bool Allow { get; init; }

    public override string? Render(Snapshot s, DateTimeOffset now)
    {
        var target = Host.ApprovalTarget();
        var agent = target is null ? null : s.Agents.FirstOrDefault(a => a.Key == target.AgentKey);
        var more = Math.Max(0, Host.Monitor.Approvals.Pending.Count - (target is null ? 0 : 1));
        return Host.Renderer.DecisionKey(target, agent, Allow, more, now);
    }

    public override Task OnKeyUpAsync(DeckEvent e)
    {
        if (Host.Decide(Allow ? ApprovalOutcome.Allow : ApprovalOutcome.Deny)) Host.Deck.ShowOk(Context); else Host.Deck.ShowAlert(Context);
        return Task.CompletedTask;
    }
}
