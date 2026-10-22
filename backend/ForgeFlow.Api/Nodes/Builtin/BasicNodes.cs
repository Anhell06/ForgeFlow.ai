using System.Text.Json.Nodes;
using ForgeFlow.Core;

namespace ForgeFlow.Nodes.Builtin;

public abstract class NodeBase : INodeType
{
    public abstract NodeSpec Spec { get; }
    public abstract Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx);

    protected static Dictionary<string, JsonNode?> Out(params (string Port, JsonNode? Value)[] values) =>
        values.ToDictionary(v => v.Port, v => v.Value);

    protected static JsonArray ToArray(IEnumerable<AssetRef> assets) => new(assets.Select(a => (JsonNode)a.ToJson()).ToArray());

    protected static JsonNode? First(IEnumerable<AssetRef> assets, string? kind = null) =>
        assets.FirstOrDefault(a => kind is null || a.Kind == kind)?.ToJson();
}

public static class P
{
    public static ParamSpec Provider(params string[] types) =>
        new("provider", "provider", "Provider", ProviderTypes: types, Help: "Leave empty to use the first configured provider of a supported type.");

    public static ParamSpec Model(string? def = null, string? help = null) => new("model", "string", "Model", def is null ? null : JsonValue.Create(def), Help: help);

    public static ParamSpec Prompt(string def = "{{inputs.prompt}}", string label = "Prompt") =>
        new("prompt", "text", label, JsonValue.Create(def), Help: "Template: {{inputs.x}}, {{workflow.x}}, {{inputs.x | escape}}, …");

    public static ParamSpec Json(string name, string label, string? help = null, string? def = null) =>
        new(name, "json", label, def is null ? null : JsonValue.Create(def), Help: help);
}

public class TextInputNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("input.text", "Text input", "Input",
        "A pipeline input (e.g. the main prompt). Its name becomes a field in the Run dialog and in the API.",
        [],
        [new("text", Kinds.Text)],
        [
            new("name", "string", "Input name", JsonValue.Create("prompt")),
            new("value", "text", "Default value", JsonValue.Create("")),
        ], "#6366f1");

    public override Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var name = ctx.RawParam("name")?.GetValue<string>() ?? "prompt";
        var value = ctx.WorkflowInputs.TryGetPropertyValue(name, out var v) && v is not null ? Json.AsText(v) : ctx.Str("value") ?? "";
        return Task.FromResult(Out(("text", JsonValue.Create(value))));
    }
}

public class AssetInputNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("input.asset", "File input", "Input",
        "A file from the library (image, model, video…) or a URL. Can be overridden per run by name.",
        [],
        [new("file", Kinds.Any)],
        [
            new("name", "string", "Input name", JsonValue.Create("image")),
            new("asset", "asset", "Asset id or URL"),
        ], "#6366f1");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var name = ctx.RawParam("name")?.GetValue<string>() ?? "image";
        var value = ctx.WorkflowInputs.TryGetPropertyValue(name, out var v) && v is not null ? v : ctx.RawParam("asset");
        if (AssetRef.From(value) is { } direct)
        {
            // A bare {"$asset":"ast_…"} from the API has no path yet.
            var full = string.IsNullOrEmpty(direct.Path) ? await ctx.Assets.GetAsync(direct.Id, ctx.Ct) : direct;
            return Out(("file", (full ?? throw new NodeException($"Asset '{direct.Id}' not found")).ToJson()));
        }
        var text = Json.AsText(value).Trim();
        if (string.IsNullOrEmpty(text)) throw new NodeException($"No file given for input '{name}'");
        if (text.StartsWith("http") || text.StartsWith("data:"))
            return Out(("file", (await ctx.DownloadAsync(text)).ToJson()));
        if (File.Exists(text))
            return Out(("file", (await ctx.SaveFileAsync(text)).ToJson()));
        var asset = await ctx.Assets.GetAsync(text, ctx.Ct) ?? throw new NodeException($"Asset '{text}' not found");
        return Out(("file", asset.ToJson()));
    }
}

public class TextTemplateNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("text.template", "Text template", "Text",
        "Builds text from a template. Reference inputs as {{inputs.a}}, workflow inputs as {{workflow.prompt}}.",
        [new("a", Kinds.Any), new("b", Kinds.Any), new("c", Kinds.Any)],
        [new("text", Kinds.Text)],
        [new("template", "text", "Template", JsonValue.Create("{{inputs.a}}"))], "#0ea5e9");

    public override Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx) =>
        Task.FromResult(Out(("text", JsonValue.Create(ctx.Str("template") ?? ""))));
}

public class TextSplitNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("text.split", "Split to list", "Text",
        "Turns text (one item per line, or a JSON array) into a list. Nodes after a list run once per item.",
        [new("text", Kinds.Any)],
        [new("items", Kinds.Text, List: true)],
        [new("separator", "string", "Separator", JsonValue.Create("\\n"), Help: "\\n for lines")], "#0ea5e9");

    public override Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var input = ctx.Input("text");
        if (input is JsonArray arr) return Task.FromResult(Out(("items", arr.DeepClone())));
        var text = Json.AsText(input).Trim();
        if (text.StartsWith('[') && Json.Parse(text) is JsonArray parsed) return Task.FromResult(Out(("items", parsed)));
        var sep = (ctx.RawParam("separator")?.GetValue<string>() ?? "\\n").Replace("\\n", "\n");
        var items = text.Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.TrimStart('-', '*', '•', ' ').Trim())
            .Where(s => s.Length > 0)
            .Select(s => (JsonNode)JsonValue.Create(s)!);
        return Task.FromResult(Out(("items", new JsonArray(items.ToArray()))));
    }
}

public class ResultNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("output.result", "Result", "Output",
        "Marks a pipeline result. Results are returned by the API and shown on the run page.",
        [new("value", Kinds.Any)],
        [new("value", Kinds.Any)],
        [new("name", "string", "Result name", JsonValue.Create("result"))], "#10b981");

    public override Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx) =>
        Task.FromResult(Out(("value", ctx.Input("value")?.DeepClone())));
}

/// <summary>Produces placeholder files so pipelines can be tested without API keys or GPUs.</summary>
public class MockNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("debug.mock", "Mock generator", "Debug",
        "Fake generator for testing pipelines offline: writes a gradient image, a cube .glb, or echoes text.",
        [new("in", Kinds.Any)],
        [new("out", Kinds.Any)],
        [
            new("kind", "select", "Output kind", JsonValue.Create("image"), Options: ["image", "model", "text"]),
            new("delayMs", "number", "Delay (ms)", JsonValue.Create(300)),
            new("fail", "bool", "Fail on purpose", JsonValue.Create(false)),
        ], "#64748b");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var delay = ctx.Int("delayMs", 300);
        for (var i = 1; i <= 4; i++)
        {
            await Task.Delay(delay / 4, ctx.Ct);
            ctx.Progress(i / 4.0, $"step {i}/4");
        }
        if (ctx.Bool("fail")) throw new NodeException("Mock failure requested");
        var seed = Json.AsText(ctx.Input("in"));
        ctx.Log($"mock input: {(seed.Length > 120 ? seed[..120] + "…" : seed)}");
        switch (ctx.Str("kind"))
        {
            case "model":
                return Out(("out", (await ctx.SaveBytesAsync(GlbWriter.Cube(), "mock-cube.glb")).ToJson()));
            case "text":
                return Out(("out", JsonValue.Create($"mock: {seed}")));
            default:
                var hue = ((seed.GetHashCode() & 0x7fffffff) % 360) / 360f;
                var img = new RgbaImage(256, 256);
                for (var y = 0; y < img.Height; y++)
                    for (var x = 0; x < img.Width; x++)
                        img[x, y] = FromHsv(hue + x / 1024f, 0.6f, 0.4f + y / 512f);
                return Out(("out", (await ctx.SaveBytesAsync(img.ToPng(), "mock.png")).ToJson()));
        }
    }

    static Rgba FromHsv(float h, float s, float v)
    {
        h = (h % 1 + 1) % 1 * 6;
        var c = v * s;
        var x = c * (1 - Math.Abs(h % 2 - 1));
        var (r, g, b) = (int)h switch { 0 => (c, x, 0f), 1 => (x, c, 0f), 2 => (0f, c, x), 3 => (0f, x, c), 4 => (x, 0f, c), _ => (c, 0f, x) };
        var m = v - c;
        static byte B(float f) => (byte)Math.Clamp(f * 255, 0, 255);
        return new Rgba(B(r + m), B(g + m), B(b + m), 255);
    }
}
