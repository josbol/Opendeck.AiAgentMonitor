using System.Runtime.InteropServices;
using System.Text.Json;
using Opendeck.AiAgentMonitor.Actions;
using Opendeck.AiAgentMonitor.Agents;
using Opendeck.AiAgentMonitor.Deck;
using Opendeck.AiAgentMonitor.Rendering;
using Opendeck.AiAgentMonitor.Util;

// ---- developer/diagnostic modes (no OpenDeck needed) -------------------------------------------
if (args.Length > 0 && args[0].StartsWith("--"))
{
    // --offline: skip the usage endpoints (local files only) in the diagnostic modes
    var monitor = new AgentMonitor { NetworkQuota = !args.Contains("--offline"), CodexNetworkQuota = !args.Contains("--offline") };
    switch (args[0])
    {
        case "--dump":
        {
            await monitor.RefreshAsync(CancellationToken.None);
            var s = monitor.Current;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                at = s.At,
                agents = s.Agents.Select(a => new { a.Key, a.Provider, a.Name, a.ProjectName, a.Cwd, a.Host, a.State, a.Detail, a.Model, a.ContextTokens, a.ContextPct, a.Pid, a.SubAgents, a.Title, StateSince = a.StateSince.ToLocalTime(), LastActivity = a.LastActivity.ToLocalTime() }),
                claude = s.Claude, codex = s.Codex, copilot = s.Copilot,
            }, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            return 0;
        }
        case "--render":
        {
            var dir = args.Length > 1 ? args[1] : "render-out";
            Directory.CreateDirectory(dir);
            await monitor.RefreshAsync(CancellationToken.None);
            var s = monitor.Current; var now = DateTimeOffset.UtcNow; var r = new KeyRenderer();
            void Save(string name, string dataUrl) => File.WriteAllBytes(Path.Combine(dir, name + ".png"), Convert.FromBase64String(dataUrl[(dataUrl.IndexOf(',') + 1)..]));
            Save("quota-claude", r.QuotaKey(Provider.Claude, s.Claude, now));
            Save("quota-codex", r.QuotaKey(Provider.Codex, s.Codex, now));
            Save("quota-copilot", r.QuotaKey(Provider.Copilot, s.Copilot, now));
            Save("overview", r.OverviewKey(s, now));
            Save("attention", r.AttentionKey(s, now));
            Save("attention-back", r.OverviewKey(s, now, backGlyph: true));
            var ordered = s.Ordered();
            for (var i = 0; i < ordered.Count; i++) { Save($"agent-{i + 1}", r.AgentKey(ordered[i], now)); Save($"selected-{i + 1}", r.AgentKey(ordered[i], now, i + 1, ordered.Count)); }
            Save("empty-slot", r.EmptySlot(3, null));
            // synthetic samples so every state can be eyeballed
            var sample = new AgentInfo { Key = "x", Provider = Provider.Claude, Name = "demo", Cwd = "/home/dev/source/AcmeShop.API", Host = "Rider", State = AgentState.Waiting, StateSince = now.AddMinutes(-3), LastActivity = now, StartedAt = now.AddHours(-1), Model = "claude-fable-5", ContextPct = 37, Detail = "permission prompt", SubAgents = 2 };
            Save("sample-waiting", r.AgentKey(sample, now));
            Save("sample-working", r.AgentKey(sample with { Provider = Provider.Codex, State = AgentState.Working, Detail = null, Model = "gpt-5.6-sol", Host = "App", ContextPct = 82 }, now));
            Save("sample-idle", r.AgentKey(sample with { State = AgentState.Idle, Detail = null, Host = "Term", ContextPct = 95 }, now));
            Save("sample-copilot", r.AgentKey(sample with { Provider = Provider.Copilot, State = AgentState.Waiting, Detail = "shell: git push origin main", Model = "gpt-5.4-mini", Host = "Rider", ContextPct = null }, now));
            Save("sample-quota-copilot", r.QuotaKey(Provider.Copilot, new ProviderQuota { Provider = Provider.Copilot, FetchedAt = now, Plan = "pro", Windows = new[] { new QuotaWindow("month", 41, now.AddDays(12.5)) } }, now));
            var q = new ProviderQuota { Provider = Provider.Claude, FetchedAt = now, Plan = "max", Windows = new[] { new QuotaWindow("5h", 36, now.AddHours(2.3)), new QuotaWindow("7d", 67, now.AddDays(1)) } };
            Save("sample-quota", r.QuotaKey(Provider.Claude, q, now));
            Save("sample-quota-codex", r.QuotaKey(Provider.Codex, new ProviderQuota { Provider = Provider.Codex, FetchedAt = now, Plan = "pro", Windows = new[] { new QuotaWindow("5h", 12, now.AddHours(3.1)), new QuotaWindow("7d", 58, now.AddDays(4)) } }, now));
            var snap = new Snapshot { Agents = new[] { sample, sample with { Key = "y", State = AgentState.Working }, sample with { Key = "z", Provider = Provider.Codex, State = AgentState.Idle }, sample with { Key = "w", Provider = Provider.Copilot, State = AgentState.Working } }, At = now, Claude = q, Copilot = new ProviderQuota { Provider = Provider.Copilot, FetchedAt = now, Windows = new[] { new QuotaWindow("month", 41, now.AddDays(12)) } } };
            Save("sample-overview", r.OverviewKey(snap, now));
            var errAgent = sample with { Key = "e", State = AgentState.Error, Detail = "The model does not currently have capacity available", Host = "Term" };
            Save("sample-error", r.AgentKey(errAgent, now));
            Save("sample-selected-error", r.AgentKey(errAgent, now, 1, 3));
            var errSnap = new Snapshot { Agents = new[] { errAgent, sample with { Key = "y", State = AgentState.Working } }, At = now, Claude = q };
            Save("sample-attention-error", r.AttentionKey(errSnap, now));
            Save("sample-overview-error", r.OverviewKey(errSnap, now));
            var reqInput = JsonDocument.Parse("{\"command\":\"git push origin main --force-with-lease && dotnet test tests/AcmeShop.Core.Tests --no-build\",\"description\":\"Push and run the core tests\"}").RootElement.Clone();
            var req = new PendingApproval { Id = "t1", Provider = Provider.Claude, AgentKey = "x", SessionId = "x", Cwd = sample.Cwd, ToolName = "Bash", ToolInput = reqInput, Summary = PendingApproval.Summarize("Bash", reqInput) };
            Save("sample-approve", r.DecisionKey(req, sample, true, 1, now));
            Save("sample-deny", r.DecisionKey(req, sample, false, 0, now));
            Save("sample-approve-empty", r.DecisionKey(null, null, true, 0, now));
            Save("sample-agent-approval", r.AgentKey(sample with { Approval = req, Detail = req.Summary }, now));
            Save("sample-selected-approval", r.AgentKey(sample with { Approval = req, Detail = req.Summary }, now, 1, 3));
            Save("sample-attention", r.AttentionKey(snap, now));
            Save("sample-overview-back", r.OverviewKey(snap, now, backGlyph: true));
            Console.WriteLine($"wrote {Directory.GetFiles(dir).Length} images to {dir}");
            return 0;
        }
        case "--focus":
        {
            await monitor.RefreshAsync(CancellationToken.None);
            var dry = args.Contains("--dry");
            var sel = args.Skip(1).FirstOrDefault(a => a != "--dry");
            var targets = sel is null ? monitor.Current.Ordered() : monitor.Current.Agents.Where(a => a.Key.Contains(sel)).ToList();
            if (targets.Count == 0) { Console.WriteLine("no such agent"); return 1; }
            foreach (var t in dry ? targets : targets.Take(1))
                Console.WriteLine($"{t.Key}: " + (await Opendeck.AiAgentMonitor.Focus.WindowFocuser.FocusAsync(t, dry) ? (dry ? "window found" : "focused") : "not found"));
            return 0;
        }
        case "--install-hooks":
        case "--uninstall-hooks":
        {
            // port / hold come from the plugin's global settings (if it has been configured), else defaults
            var gs = new GlobalSettings();
            var settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "opendeck", "settings", "com.josbol.aiagentmonitor.sdPlugin.json");
            try { if (File.Exists(settingsFile)) gs = GlobalSettings.From(JsonDocument.Parse(File.ReadAllText(settingsFile)).RootElement); } catch { }
            var port = args.Length > 1 && int.TryParse(args[1], out var pp) ? pp : gs.HookPort;
            var hold = args.Length > 2 && int.TryParse(args[2], out var hh) ? hh : gs.ApprovalHoldSeconds;
            if (args[0] == "--install-hooks") Opendeck.AiAgentMonitor.Hooks.HookInstaller.Install(port, hold);
            else Opendeck.AiAgentMonitor.Hooks.HookInstaller.Uninstall();
            return 0;
        }
        case "--activate":
        {
            // diagnostic: run the full activation sequence on a window id (hex from `wmctrl -l` or decimal from xdotool)
            if (args.Length < 2) { Console.WriteLine("usage: --activate <window id>"); return 1; }
            Console.WriteLine(await Opendeck.AiAgentMonitor.Focus.WindowFocuser.ActivateByIdAsync(args[1]) ? "activated" : "not confirmed");
            return 0;
        }
        case "--codex-hook-hash":
        {
            // read-only: prints the config.toml trust key + hash the installer would write for the current ~/.codex/hooks.json
            var hp = Opendeck.AiAgentMonitor.Hooks.HookInstaller.CodexHooksPath;
            var rootNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(hp)) as System.Text.Json.Nodes.JsonObject ?? new();
            var (key, hash) = Opendeck.AiAgentMonitor.Hooks.HookInstaller.CodexTrustEntry(rootNode);
            Console.WriteLine($"[hooks.state.\"{key}\"]\ntrusted_hash = \"{hash}\"");
            return 0;
        }
        case "--version":
            Console.WriteLine(typeof(Program).Assembly.GetName().Version);
            return 0;
        default:
            Console.WriteLine("usage: opendeck-aiagentmonitor [--dump [--offline] | --render <dir> [--offline] | --focus [key] [--dry] | --install-hooks [port] [holdSeconds] | --uninstall-hooks] | -port N -pluginUUID id -registerEvent ev -info json");
            return 1;
    }
}

// ---- plugin mode --------------------------------------------------------------------------------
var deck = DeckClient.FromArgs(args);
if (deck is null)
{
    Console.Error.WriteLine("Missing -port/-pluginUUID; run with --dump for diagnostics.");
    return 2;
}

Log.Info($"AI Agent Monitor starting (pid {Environment.ProcessId})");
using var cts = new CancellationTokenSource();
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; cts.Cancel(); });
using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; cts.Cancel(); });

using var mon = new AgentMonitor();
await using var host = new PluginHost(deck, mon);
mon.Start();
try { await deck.RunAsync(cts.Token); }
catch (OperationCanceledException) { }
catch (Exception ex) { Log.Error("fatal", ex); return 1; }
Log.Info("exiting");
return 0;

public partial class Program { }
