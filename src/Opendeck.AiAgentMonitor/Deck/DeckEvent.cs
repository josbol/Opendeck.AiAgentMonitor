using System.Text.Json;
using Opendeck.AiAgentMonitor.Util;

namespace Opendeck.AiAgentMonitor.Deck;

/// <summary>An event received from OpenDeck (Elgato Stream Deck wire format).</summary>
public sealed record DeckEvent(string Event, string? Action, string? Context, string? Device, JsonElement Payload, JsonElement Raw)
{
    public static DeckEvent Parse(string json)
    {
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var payload = root.TryGetProperty("payload", out var p) ? p : default;
        return new DeckEvent(root.Str("event") ?? "", root.Str("action"), root.Str("context"), root.Str("device"), payload, root);
    }

    public JsonElement Settings => Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty("settings", out var s) ? s : default;
    public string? Controller => Payload.Str("controller");
    public int State => (int)(Payload.Long("state") ?? 0);
    public int Ticks => (int)(Payload.Long("ticks") ?? 0);
    public (int Row, int Column)? Coordinates
    {
        get
        {
            var c = Payload.Obj("coordinates");
            if (c is null) return null;
            return ((int)(c.Value.Long("row") ?? 0), (int)(c.Value.Long("column") ?? 0));
        }
    }
}
