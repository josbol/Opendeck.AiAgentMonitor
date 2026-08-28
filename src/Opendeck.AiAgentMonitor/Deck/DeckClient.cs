using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Deck;

/// <summary>
/// WebSocket client for the OpenDeck / Stream Deck plugin protocol.
/// OpenDeck launches the plugin with: -port N -pluginUUID id -registerEvent registerPlugin -info {json}
/// </summary>
public sealed class DeckClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Channel<string> _outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public string PluginUuid { get; }
    public string RegisterEvent { get; }
    public JsonElement Info { get; }

    public event Func<DeckEvent, Task>? EventReceived;
    public event Action? Disconnected;

    public DeckClient(int port, string pluginUuid, string registerEvent, JsonElement info)
    {
        Port = port; PluginUuid = pluginUuid; RegisterEvent = registerEvent; Info = info;
    }

    public static DeckClient? FromArgs(string[] args)
    {
        string? Get(string key)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
        var port = Get("-port"); var uuid = Get("-pluginUUID"); var reg = Get("-registerEvent") ?? "registerPlugin"; var info = Get("-info") ?? "{}";
        if (port is null || uuid is null) return null;
        JsonElement infoEl;
        try { infoEl = JsonDocument.Parse(info).RootElement.Clone(); } catch { infoEl = JsonDocument.Parse("{}").RootElement.Clone(); }
        return new DeckClient(int.Parse(port), uuid, reg, infoEl);
    }

    /// <summary>Connects, registers, and pumps messages until the socket closes or the token is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        var token = linked.Token;
        await _ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Port}"), token);
        Log.Info($"Connected to OpenDeck on port {Port} as {PluginUuid}");
        await SendRawAsync(Json.Serialize(new { @event = RegisterEvent, uuid = PluginUuid }), token);

        var sender = Task.Run(() => SendLoopAsync(token), token);
        try
        {
            await ReceiveLoopAsync(token);
        }
        finally
        {
            _outbox.Writer.TryComplete();
            try { await sender; } catch { /* ignore */ }
            Disconnected?.Invoke();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested && _ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try { result = await _ws.ReceiveAsync(buffer, ct); }
            catch (OperationCanceledException) { break; }
            catch (WebSocketException ex) { Log.Warn($"WebSocket closed: {ex.Message}"); break; }

            if (result.MessageType == WebSocketMessageType.Close) { Log.Info("OpenDeck closed the connection"); break; }
            sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            if (!result.EndOfMessage) continue;
            var text = sb.ToString(); sb.Clear();
            DeckEvent evt;
            try { evt = DeckEvent.Parse(text); }
            catch (Exception ex) { Log.Warn($"Bad event JSON: {ex.Message}: {Truncate(text)}"); continue; }
            Log.Debug($"<- {evt.Event} {evt.Action} {evt.Context}");
            if (EventReceived is not null)
            {
                try { await EventReceived(evt); }
                catch (Exception ex) { Log.Error($"Handler for {evt.Event} failed", ex); }
            }
        }
    }

    private async Task SendLoopAsync(CancellationToken ct)
    {
        await foreach (var msg in _outbox.Reader.ReadAllAsync(ct))
        {
            if (_ws.State != WebSocketState.Open) break;
            try { await SendRawAsync(msg, ct); }
            catch (Exception ex) { Log.Warn($"Send failed: {ex.Message}"); break; }
        }
    }

    private Task SendRawAsync(string json, CancellationToken ct)
        => _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    public void Send(object message)
    {
        var json = Json.Serialize(message);
        if (Log.Verbose && !json.Contains("\"image\"")) Log.Debug($"-> {Truncate(json)}");
        _outbox.Writer.TryWrite(json);
    }

    // ---- convenience wrappers ----------------------------------------------------------

    public void SetImage(string context, string? dataUrl, int? state = null)
        => Send(new { @event = "setImage", context, payload = new { image = dataUrl ?? "", state, target = 0 } });

    public void SetTitle(string context, string title, int? state = null)
        => Send(new { @event = "setTitle", context, payload = new { title, state, target = 0 } });

    public void SetState(string context, int state)
        => Send(new { @event = "setState", context, payload = new { state } });

    public void ShowAlert(string context) => Send(new { @event = "showAlert", context });
    public void ShowOk(string context) => Send(new { @event = "showOk", context });
    public void OpenUrl(string url) => Send(new { @event = "openUrl", payload = new { url } });
    public void LogMessage(string message) => Send(new { @event = "logMessage", payload = new { message } });
    public void SetSettings(string context, object settings) => Send(new { @event = "setSettings", context, payload = settings });
    public void GetSettings(string context) => Send(new { @event = "getSettings", context });
    public void SetGlobalSettings(object settings) => Send(new { @event = "setGlobalSettings", context = PluginUuid, payload = settings });
    public void GetGlobalSettings() => Send(new { @event = "getGlobalSettings", context = PluginUuid });
    public void SendToPropertyInspector(string context, object payload) => Send(new { @event = "sendToPropertyInspector", context, payload });

    private static string Truncate(string s) => s.Length > 300 ? s[..300] + "…" : s;

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { if (_ws.State == WebSocketState.Open) await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        _ws.Dispose();
    }
}
