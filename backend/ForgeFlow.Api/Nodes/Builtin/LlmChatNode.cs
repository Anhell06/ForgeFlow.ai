using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ForgeFlow.Core;
using ForgeFlow.Providers;

namespace ForgeFlow.Nodes.Builtin;

public partial class LlmChatNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("llm.chat", "LLM", "AI · Text",
        "Asks a chat model (GPT, Claude, a local model in Ollama/LM Studio). Can see images. " +
        "Output 'items' is a list (from a JSON array or one item per line) — connect it to fan a node out per item.",
        [new("prompt", Kinds.Text), new("images", Kinds.Image, List: true), new("context", Kinds.Any)],
        [new("text", Kinds.Text), new("json", Kinds.Json), new("items", Kinds.Text, List: true)],
        [
            P.Provider("openai", "anthropic"),
            P.Model(help: "Empty = provider default (e.g. gpt-5, claude-opus-5-5, gpt-oss:20b)"),
            new("system", "text", "System prompt", JsonValue.Create("")),
            P.Prompt(),
            new("format", "select", "Output format", JsonValue.Create("text"), Options: ["text", "json", "list"]),
            new("temperature", "string", "Temperature", Help: "Empty = model default. Some models reject it."),
        ], "#a855f7");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var provider = await ctx.Providers.ResolveAsync(ctx.Str("provider"), ["openai", "anthropic"], ctx.Ct);
        var model = ctx.Str("model") is { Length: > 0 } m ? m : provider.DefaultModel ?? throw new NodeException("Set a model");
        var format = ctx.Str("format") ?? "text";
        var system = ctx.Str("system") ?? "";
        if (format == "json") system += "\nRespond with a single valid JSON value and nothing else.";
        if (format == "list") system += "\nRespond with a JSON array of strings and nothing else.";
        double? temperature = double.TryParse(ctx.Str("temperature"), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : null;

        var session = LlmFactory.Create(provider,
            new LlmOptions(model, system.Trim(), [], Temperature: temperature, JsonMode: format == "json" && provider.Type == "openai"), ctx.Http);

        var images = new List<LlmImage>();
        foreach (var a in ctx.InputAssets("images").Where(a => a.Kind == "image").Take(16))
            images.Add(new LlmImage(a.Mime, Convert.ToBase64String(await File.ReadAllBytesAsync(a.Path, ctx.Ct))));
        var prompt = ctx.Str("prompt") ?? "";
        if (ctx.Input("context") is { } c) prompt += "\n\nContext:\n" + Json.AsText(c);

        ctx.Log($"{provider.Name} / {model}: {(prompt.Length > 200 ? prompt[..200] + "…" : prompt)}");
        session.AddUser(prompt, images);
        var turn = await session.NextAsync(ctx.Ct);
        ctx.Log($"answer: {(turn.Text.Length > 300 ? turn.Text[..300] + "…" : turn.Text)}");

        var json = ExtractJson(turn.Text);
        return Out(("text", JsonValue.Create(turn.Text)), ("json", json), ("items", ToItems(json, turn.Text)));
    }

    [GeneratedRegex(@"```(?:json)?\s*([\s\S]*?)```")]
    private static partial Regex Fence();

    public static JsonNode? ExtractJson(string text)
    {
        var t = text.Trim();
        if (Json.Parse(t) is { } direct) return direct;
        var fence = Fence().Match(t);
        if (fence.Success && Json.Parse(fence.Groups[1].Value) is { } fenced) return fenced;
        foreach (var (open, close) in new[] { ('[', ']'), ('{', '}') })
        {
            int s = t.IndexOf(open), e = t.LastIndexOf(close);
            if (s >= 0 && e > s && Json.Parse(t[s..(e + 1)]) is { } inner) return inner;
        }
        return null;
    }

    public static JsonArray ToItems(JsonNode? json, string text)
    {
        if (json is JsonArray arr) return (JsonArray)arr.DeepClone();
        if (json is JsonObject o && o.Select(kv => kv.Value).OfType<JsonArray>().FirstOrDefault() is { } inner) return (JsonArray)inner.DeepClone();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => Regex.Replace(l, @"^(\d+[\.\)]|[-*•])\s*", "").Trim())
            .Where(l => l.Length > 0);
        return new JsonArray(lines.Select(l => (JsonNode)JsonValue.Create(l)!).ToArray());
    }
}
