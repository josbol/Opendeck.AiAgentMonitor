using System.Diagnostics;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Deck;
using Opendeck.AiAgentMonitor.Hooks;
using Opendeck.AiAgentMonitor.Rendering;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Actions;

/// <summary>Owns the live action instances, routes deck events to them and re-renders when the snapshot changes.</summary>
public sealed class PluginHost : IAsyncDisposable
{
    public const string UuidPrefix = "com.josbol.aiagentmonitor.";

    public DeckClient Deck { get; }
    public AgentMonitor Monitor { get; }
    public KeyRenderer Renderer { get; } = new();
    public HookServer Hooks { get; }
    public ApprovalNotifier Notifier { get; }
    public GlobalSettings Settings { get; private set; } = new();

    private readonly Dictionary<string, DeckAction> _actions = new();
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private string? _selectedKey;
    private int _renderPending;

    public PluginHost(DeckClient deck, AgentMonitor monitor)
    {
        Deck = deck; Monitor = monitor;
        deck.EventReceived += OnDeckEventAsync;
        monitor.Changed += _ => RequestRender();
        monitor.AgentNeedsAttention += a => Log.Info($"Attention: {a.Provider} {a.ProjectName} → {a.Detail ?? "waiting"}");
        monitor.Approvals.Added += p =>
        {
            // the deck follows what needs you: select the agent unless the current selection also has a request
            var cur = _selectedKey is null ? null : monitor.Approvals.ForAgent(_selectedKey);
            if (cur is null) _selectedKey = p.AgentKey;
            RequestRender();
        };
        monitor.Approvals.Resolved += (_, _) => RequestRender();
        Hooks = new HookServer(monitor.Approvals)
        {
            HoldTime = () => TimeSpan.FromSeconds(Math.Max(5, Settings.ApprovalHoldSeconds)),
            SkipHold = async p =>
            {
                // Codex auto-review sessions (ChatGPT app): let Guardian screen the request first — it approves
                // the routine ones silently, and what it rejects comes back as a question in the app (deck alerts).
                if (p.Provider == Provider.Codex && Settings.CodexGuardianFirst && Monitor.CodexApprovalsReviewer(p.SessionId) == "auto_review")
                    return "auto-review session, Guardian decides; a rejection comes back as a question in the app";
                if (!Settings.HoldOnlyWhenUnfocused) return null;
                var agent = Monitor.Current.Agents.FirstOrDefault(a => a.Key == p.AgentKey);
                return agent is not null && await Focus.WindowFocuser.IsAgentWindowActiveAsync(agent) ? "window focused" : null;
            },
        };
        Hooks.Activity += () => Monitor.Poke();
        Hooks.Start(Settings.HookPort);
        Notifier = new ApprovalNotifier(monitor.Approvals) { Style = Settings.ApprovalPopup, Screen = Settings.PopupScreen, HoldSeconds = () => Settings.ApprovalHoldSeconds };
        _ = Task.Run(TickLoopAsync);
    }

    // ---- approvals ------------------------------------------------------------------------

    /// <summary>The request the Approve/Deny keys act on: the selected agent's, else the most urgent one.</summary>
    public PendingApproval? ApprovalTarget()
    {
        var pending = Monitor.Approvals.Pending;
        if (pending.Count == 0) return null;
        if (_selectedKey is not null && pending.FirstOrDefault(p => p.AgentKey == _selectedKey) is { } sel) return sel;
        foreach (var a in Monitor.Current.Ordered())
            if (pending.FirstOrDefault(p => p.AgentKey == a.Key) is { } p) return p;
        return pending[0];
    }

    public bool Decide(ApprovalOutcome outcome)
    {
        var target = ApprovalTarget();
        if (target is null) return false;
        return Monitor.Approvals.Resolve(target, outcome, outcome == ApprovalOutcome.Deny ? "Denied from the deck" : null);
    }

    /// <summary>Brings the agent's window to the front; a held permission request is released first so the terminal shows its dialog.</summary>
    public async Task<bool> FocusAsync(AgentInfo agent)
    {
        Select(agent.Key);
        var released = Monitor.Approvals.ReleaseAgent(agent.Key);
        if (released > 0) Log.Info($"Released {released} request(s) for {agent.Key} to the terminal");
        return await Focus.WindowFocuser.FocusAsync(agent);
    }

    // ---- selection (shared by the dial and the "selected agent" key) -----------------------

    public void Select(string key) { _selectedKey = key; RequestRender(); }

    public int SelectedIndex(IReadOnlyList<AgentInfo> ordered)
    {
        if (ordered.Count == 0) return 0;
        var idx = _selectedKey is null ? -1 : ordered.ToList().FindIndex(a => a.Key == _selectedKey);
        return idx < 0 ? 0 : idx;
    }

    public void RequestUsageRefresh() => Monitor.RequestUsageRefresh();

    // ---- profile switching via the OpenDeck CLI (plugins may not send switchProfile themselves) ----

    public async Task<bool> SwitchProfileAsync(string device, string profile)
    {
        var message = Json.Serialize(new { @event = "switchProfile", device, profile });
        foreach (var (file, prefix) in new[] { ("opendeck", Array.Empty<string>()), ("/usr/bin/opendeck", Array.Empty<string>()), ("flatpak", new[] { "run", "me.amankhanna.opendeck" }) })
        {
            try
            {
                var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in prefix) psi.ArgumentList.Add(a);
                psi.ArgumentList.Add("--process-message"); psi.ArgumentList.Add(message);
                using var p = Process.Start(psi);
                if (p is null) continue;
                await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Log.Info($"switchProfile {device} → '{profile}' via {file} (exit {p.ExitCode})");
                return true;
            }
            catch (Exception ex) { Log.Debug($"{file}: {ex.Message}"); }
        }
        Log.Warn("Could not run the opendeck CLI to switch profiles");
        return false;
    }

    // ---- deck events ---------------------------------------------------------------------

    private async Task OnDeckEventAsync(DeckEvent e)
    {
        switch (e.Event)
        {
            case "willAppear":
            {
                if (e.Context is null || e.Action is null) return;
                var action = Create(e.Action, e.Context);
                if (action is null) { Log.Warn($"Unknown action {e.Action}"); return; }
                action.Device = e.Device; action.Controller = e.Controller; action.Settings = e.Settings.Clone();
                lock (_lock) _actions[e.Context] = action;
                Log.Info($"willAppear {e.Action} @ {e.Context} ({e.Controller})");
                await action.OnAppearAsync();
                RenderOne(action, Monitor.Current, DateTimeOffset.UtcNow);
                if (_actions.Count == 1) Deck.GetGlobalSettings();
                break;
            }
            case "willDisappear":
                if (e.Context is not null) lock (_lock) _actions.Remove(e.Context);
                Log.Debug($"willDisappear {e.Context}");
                break;
            case "didReceiveSettings":
                if (e.Context is not null && Get(e.Context) is { } a1) { a1.Settings = e.Settings.Clone(); RenderOne(a1, Monitor.Current, DateTimeOffset.UtcNow); }
                break;
            case "didReceiveGlobalSettings":
                Settings = GlobalSettings.From(e.Settings);
                Monitor.ApplySettings(Settings);
                if (Settings.HookPort != Hooks.Port) Log.Warn($"Hook port changed to {Settings.HookPort}; reload the plugin and re-run --install-hooks");
                Notifier.Style = Settings.ApprovalPopup; Notifier.Screen = Settings.PopupScreen;
                Log.Info($"Global settings: {Json.Serialize(Settings)}");
                RequestRender();
                break;
            case "keyDown": if (Get(e.Context) is { } kd) await kd.OnKeyDownAsync(e); break;
            case "keyUp": if (Get(e.Context) is { } ku) await ku.OnKeyUpAsync(e); break;
            case "dialRotate": if (Get(e.Context) is { } dr) await dr.OnDialRotateAsync(e.Ticks); break;
            case "dialDown": if (Get(e.Context) is { } dd) await dd.OnDialDownAsync(); break;
            case "dialUp": if (Get(e.Context) is { } du) await du.OnDialUpAsync(); break;
            case "touchTap": if (Get(e.Context) is { } tt) await tt.OnDialUpAsync(); break;
            case "sendToPlugin":
                if (e.Payload.Str("command") == "refresh") { RequestUsageRefresh(); RequestRender(); }
                else if (e.Payload.Str("command") == "setGlobalSettings" && e.Payload.Obj("settings") is { } gs) { Deck.SetGlobalSettings(JsonSerializer.Deserialize<JsonElement>(gs.GetRawText())); Settings = GlobalSettings.From(gs); Monitor.ApplySettings(Settings); RequestRender(); }
                if (Get(e.Context) is { } sp) await sp.OnSendToPluginAsync(e.Payload);
                break;
            case "propertyInspectorDidAppear":
                if (e.Context is not null) Deck.SendToPropertyInspector(e.Context, new { globalSettings = Settings, snapshot = Describe(Monitor.Current) });
                break;
            case "systemDidWakeUp":
                RequestUsageRefresh(); RequestRender();
                break;
        }
    }

    private DeckAction? Get(string? context)
    {
        if (context is null) return null;
        lock (_lock) return _actions.GetValueOrDefault(context);
    }

    private DeckAction? Create(string uuid, string context)
    {
        var name = uuid.StartsWith(UuidPrefix) ? uuid[UuidPrefix.Length..] : uuid;
        return name switch
        {
            "agent" => new AgentSlotAction { Context = context, ActionUuid = uuid, Host = this },
            "quota" => new QuotaAction { Context = context, ActionUuid = uuid, Host = this },
            "overview" => new OverviewAction { Context = context, ActionUuid = uuid, Host = this },
            "attention" => new AttentionAction { Context = context, ActionUuid = uuid, Host = this },
            "selected" => new SelectedAgentAction { Context = context, ActionUuid = uuid, Host = this },
            "dial" => new SelectDialAction { Context = context, ActionUuid = uuid, Host = this },
            "approve" => new DecisionAction { Context = context, ActionUuid = uuid, Host = this, Allow = true },
            "deny" => new DecisionAction { Context = context, ActionUuid = uuid, Host = this, Allow = false },
            _ => null,
        };
    }

    // ---- rendering -----------------------------------------------------------------------

    public void RequestRender()
    {
        if (Interlocked.Exchange(ref _renderPending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(120); // coalesce bursts
            Interlocked.Exchange(ref _renderPending, 0);
            RenderAll();
        });
    }

    private void RenderAll()
    {
        List<DeckAction> actions;
        lock (_lock) actions = _actions.Values.ToList();
        var snap = Monitor.Current; var now = DateTimeOffset.UtcNow;
        foreach (var a in actions) RenderOne(a, snap, now);
    }

    private void RenderOne(DeckAction a, Snapshot snap, DateTimeOffset now)
    {
        try
        {
            var img = a.Render(snap, now);
            if (img is null || img == a.LastImage) return;
            a.LastImage = img;
            Deck.SetImage(a.Context, img);
        }
        catch (Exception ex) { Log.Error($"render {a.ActionUuid} failed", ex); }
    }

    private async Task TickLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Max(10, Settings.TickSeconds)), _cts.Token); } catch { return; }
            RenderAll(); // refresh elapsed times / countdowns
        }
    }

    private static object Describe(Snapshot s) => new
    {
        agents = s.Agents.Select(a => new { a.Key, provider = a.Provider.ToString(), a.Name, a.Cwd, a.Host, state = a.State.ToString(), a.Detail, a.Model, a.ContextPct, a.SubAgents, approval = a.Approval?.Summary }),
        claude = s.Claude is null ? null : new { s.Claude.Plan, s.Claude.Error, s.Claude.Source, windows = s.Claude.Windows },
        codex = s.Codex is null ? null : new { s.Codex.Plan, s.Codex.Error, s.Codex.Source, windows = s.Codex.Windows },
    };

    public ValueTask DisposeAsync() { _cts.Cancel(); Hooks.Dispose(); return ValueTask.CompletedTask; }
}
