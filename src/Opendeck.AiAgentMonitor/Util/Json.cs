using System.Text.Json;
using System.Text.Json.Serialization;

namespace Opendeck.AiAgentMonitor.Util;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    // ---- tolerant JsonElement accessors -------------------------------------------------

    public static string? Str(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static long? Long(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.TryGetInt64(out var l) ? l : (long?)p.GetDouble(),
            JsonValueKind.String => long.TryParse(p.GetString(), out var l) ? l : null,
            _ => null,
        };
    }

    public static double? Dbl(this JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var p)) return null;
        return p.ValueKind switch
        {
            JsonValueKind.Number => p.GetDouble(),
            JsonValueKind.String => double.TryParse(p.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null,
            _ => null,
        };
    }

    public static bool? Bool(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
            ? p.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    public static JsonElement? Obj(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.Object or JsonValueKind.Array ? p : null;

    public static JsonElement? Prop(this JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind != JsonValueKind.Null ? p : null;
}
