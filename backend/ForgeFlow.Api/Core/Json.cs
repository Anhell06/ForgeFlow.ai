using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ForgeFlow.Core;

public static class Json
{
    public static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Pretty = new(Opts) { WriteIndented = true };

    public static string Ser<T>(T value) => JsonSerializer.Serialize(value, Opts);

    public static T De<T>(string json) => JsonSerializer.Deserialize<T>(json, Opts)!;

    public static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json); } catch (JsonException) { return null; }
    }

    public static JsonObject ParseObject(string? json) => Parse(json) as JsonObject ?? new JsonObject();

    public static JsonArray ParseArray(string? json) => Parse(json) as JsonArray ?? new JsonArray();

    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, Opts);

    /// <summary>Human/agent friendly string form of a value: assets become local paths, arrays become lines.</summary>
    public static string AsText(JsonNode? node)
    {
        switch (node)
        {
            case null: return "";
            case JsonValue v when v.TryGetValue(out string? s): return s ?? "";
            case JsonValue v: return v.ToJsonString();
            case JsonObject o when AssetRef.From(o) is { } a: return a.Path;
            case JsonArray arr: return string.Join("\n", arr.Select(AsText));
            default: return node.ToJsonString();
        }
    }

    /// <summary>Walks a dotted path ("a.b.0.c") through objects and arrays.</summary>
    public static JsonNode? Select(JsonNode? root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return root;
        var cur = root;
        foreach (var raw in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var seg = raw.Trim();
            cur = cur switch
            {
                JsonObject o => o.TryGetPropertyValue(seg, out var n) ? n : null,
                JsonArray a when int.TryParse(seg, out var i) => i >= 0 && i < a.Count ? a[i] : i < 0 && a.Count + i >= 0 ? a[a.Count + i] : null,
                JsonArray a when seg == "length" => JsonValue.Create(a.Count),
                _ => null,
            };
            if (cur is null) return null;
        }
        return cur;
    }

    public static string? Str(this JsonObject o, string key) =>
        o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    /// <summary>Recursively collects every http(s) or data URL string in a JSON document.</summary>
    public static IEnumerable<string> FindUrls(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue v when v.TryGetValue(out string? s) && s is not null &&
                                  (s.StartsWith("http://") || s.StartsWith("https://") || s.StartsWith("data:")):
                yield return s;
                break;
            case JsonObject o:
                foreach (var (_, child) in o)
                    foreach (var u in FindUrls(child)) yield return u;
                break;
            case JsonArray a:
                foreach (var child in a)
                    foreach (var u in FindUrls(child)) yield return u;
                break;
        }
    }
}
