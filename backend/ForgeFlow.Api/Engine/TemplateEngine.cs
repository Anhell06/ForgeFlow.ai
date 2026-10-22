using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ForgeFlow.Core;

namespace ForgeFlow.Engine;

/// <summary>
/// Renders "{{ path | filter:arg }}" placeholders against a JSON scope
/// (inputs, params, workflow, run, node, response, ...).
/// Filters: json, escape, url, path, name, datauri, first, last, join:"sep", trim, upper, lower, default:"x", lines, base64.
/// </summary>
public static partial class TemplateEngine
{
    [GeneratedRegex(@"\{\{\s*(.+?)\s*\}\}", RegexOptions.Singleline)]
    private static partial Regex Placeholder();

    public static bool HasPlaceholders(string? text) => text is not null && Placeholder().IsMatch(text);

    public static string Render(string? template, JsonObject scope, Func<AssetRef, string>? dataUri = null)
    {
        if (string.IsNullOrEmpty(template)) return template ?? "";
        return Placeholder().Replace(template, m => Format(Evaluate(m.Groups[1].Value, scope), m.Groups[1].Value, dataUri));
    }

    /// <summary>When the whole template is a single placeholder, returns the raw JSON value instead of text.</summary>
    public static JsonNode? RenderValue(string template, JsonObject scope, Func<AssetRef, string>? dataUri = null)
    {
        var m = Placeholder().Match(template);
        if (m.Success && m.Index == 0 && m.Length == template.Length && !m.Groups[1].Value.Contains('|'))
            return Evaluate(m.Groups[1].Value, scope)?.DeepClone();
        return JsonValue.Create(Render(template, scope, dataUri));
    }

    static readonly Regex FilterSplit = new(@"\|(?=(?:[^""]*""[^""]*"")*[^""]*$)");

    // Prompts end up in JSON bodies for model APIs, not HTML, so keep quotes and non-ASCII readable.
    static readonly JsonSerializerOptions Relaxed = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static JsonNode? Evaluate(string expr, JsonObject scope)
    {
        var parts = FilterSplit.Split(expr);
        return Json.Select(scope, parts[0].Trim().Replace("[", ".").Replace("]", ""));
    }

    static string Format(JsonNode? value, string expr, Func<AssetRef, string>? dataUri)
    {
        var parts = FilterSplit.Split(expr).Skip(1).Select(p => p.Trim()).ToList();
        if (parts.Count == 0) return Json.AsText(value);

        object current = value!;
        foreach (var f in parts)
        {
            var colon = f.IndexOf(':');
            var name = colon < 0 ? f : f[..colon].Trim();
            var arg = colon < 0 ? null : Unquote(f[(colon + 1)..].Trim());
            current = Apply(name, arg, current, dataUri);
        }
        return current switch
        {
            string s => s,
            JsonNode n => Json.AsText(n),
            null => "",
            _ => current.ToString() ?? "",
        };
    }

    static object Apply(string filter, string? arg, object current, Func<AssetRef, string>? dataUri)
    {
        var node = current as JsonNode;
        var text = current is string s ? s : Json.AsText(node);
        switch (filter)
        {
            case "json":
                return current is string str ? JsonSerializer.Serialize(str, Relaxed) : node?.ToJsonString(Relaxed) ?? "null";
            case "escape":
                var encoded = JsonSerializer.Serialize(text, Relaxed);
                return encoded[1..^1];
            case "url":
                return AssetRef.From(node)?.Url ?? text;
            case "path":
                return AssetRef.From(node)?.Path ?? text;
            case "name":
                return AssetRef.From(node)?.Name ?? text;
            case "datauri":
                return AssetRef.From(node) is { } a && dataUri is not null ? dataUri(a) : text;
            case "first":
                return node is JsonArray fa ? fa.FirstOrDefault()?.DeepClone()! : current;
            case "last":
                return node is JsonArray la ? la.LastOrDefault()?.DeepClone()! : current;
            case "join":
                return node is JsonArray ja ? string.Join(arg ?? ", ", ja.Select(Json.AsText)) : text;
            case "lines":
                return node is JsonArray lines ? string.Join("\n", lines.Select(l => "- " + Json.AsText(l))) : text;
            case "trim":
                return text.Trim();
            case "upper":
                return text.ToUpperInvariant();
            case "lower":
                return text.ToLowerInvariant();
            case "base64":
                return Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
            case "default":
                return string.IsNullOrEmpty(text) ? arg ?? "" : current;
            default:
                throw new InvalidOperationException($"Unknown template filter '{filter}'");
        }
    }

    static string Unquote(string s) =>
        s.Length >= 2 && (s[0] == '"' && s[^1] == '"' || s[0] == '\'' && s[^1] == '\'') ? s[1..^1] : s;
}
