using System.Globalization;
using System.Text.Json.Nodes;
using ForgeFlow.Core;
using ForgeFlow.Engine;
using ForgeFlow.Providers;
using ForgeFlow.Storage;

namespace ForgeFlow.Nodes;

/// <summary>Everything a node needs while executing one item: resolved inputs, rendered params and services.</summary>
public sealed class NodeContext
{
    public required string RunId { get; init; }
    public required NodeDef Def { get; init; }
    public required NodeSpec Spec { get; init; }
    public required Dictionary<string, JsonNode?> Inputs { get; init; }
    public required JsonObject WorkflowInputs { get; init; }
    /// <summary>Scratch directory for this node (and item) inside the run folder.</summary>
    public required string WorkDir { get; init; }
    public required IServiceProvider Services { get; init; }
    public required AssetStore Assets { get; init; }
    public required ProviderRegistry Providers { get; init; }
    public required HttpClient Http { get; init; }
    public required CancellationToken Ct { get; init; }
    public required Action<string> Log { get; init; }
    public required Action<double?, string?> Progress { get; init; }
    public int? ItemIndex { get; init; }

    public string NodeId => Def.Id;
    public AssetMeta Meta => new(RunId, NodeId, Spec.Type);

    JsonObject? _scope;

    /// <summary>Template scope: inputs, params, workflow, run, node.</summary>
    public JsonObject Scope => _scope ??= BuildScope();

    JsonObject BuildScope()
    {
        var inputs = new JsonObject();
        foreach (var (k, v) in Inputs) inputs[k] = v?.DeepClone();
        var parms = new JsonObject();
        foreach (var p in Spec.Params) parms[p.Name] = RawParam(p.Name)?.DeepClone();
        return new JsonObject
        {
            ["inputs"] = inputs,
            ["params"] = parms,
            ["workflow"] = WorkflowInputs.DeepClone(),
            ["run"] = new JsonObject { ["id"] = RunId, ["dir"] = Path.GetDirectoryName(WorkDir) },
            ["node"] = new JsonObject { ["id"] = NodeId, ["dir"] = WorkDir, ["index"] = ItemIndex },
        };
    }

    public void ExtendScope(string key, JsonNode? value)
    {
        Scope[key] = value;
    }

    public JsonNode? RawParam(string name)
    {
        if (Def.Params.TryGetPropertyValue(name, out var v) && v is not null) return v;
        return Spec.Params.FirstOrDefault(p => p.Name == name)?.Default;
    }

    public string Render(string? template) =>
        TemplateEngine.Render(template, Scope, a => StorageDataUri(a));

    static string StorageDataUri(AssetRef a) =>
        $"data:{a.Mime};base64,{Convert.ToBase64String(File.ReadAllBytes(a.Path))}";

    /// <summary>String param with templates rendered.</summary>
    public string? Str(string name)
    {
        var raw = RawParam(name);
        if (raw is null) return null;
        var s = raw is JsonValue v && v.TryGetValue(out string? str) ? str : raw.ToJsonString();
        return Render(s);
    }

    public string StrOr(string name, string fallback) => Str(name) is { Length: > 0 } s ? s : fallback;

    public double Num(string name, double fallback)
    {
        var raw = RawParam(name);
        if (raw is JsonValue v)
        {
            if (v.TryGetValue(out double d)) return d;
            if (v.TryGetValue(out string? s) && double.TryParse(Render(s), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
        }
        return fallback;
    }

    public int Int(string name, int fallback) => (int)Num(name, fallback);

    public bool Bool(string name, bool fallback = false)
    {
        var raw = RawParam(name);
        if (raw is JsonValue v)
        {
            if (v.TryGetValue(out bool b)) return b;
            if (v.TryGetValue(out string? s)) return bool.TryParse(Render(s), out b) ? b : fallback;
        }
        return fallback;
    }

    /// <summary>JSON param: accepts an object directly or a templated string that parses to JSON.</summary>
    public JsonNode? JsonParam(string name)
    {
        var raw = RawParam(name);
        switch (raw)
        {
            case null: return null;
            case JsonValue v when v.TryGetValue(out string? s):
                if (string.IsNullOrWhiteSpace(s)) return null;
                var rendered = Render(s);
                return JsonNode.Parse(rendered) ?? throw new NodeException($"Param '{name}' is not valid JSON");
            default:
                return JsonNode.Parse(Render(raw.ToJsonString()));
        }
    }

    public JsonObject JsonObjectParam(string name) => JsonParam(name) as JsonObject ?? new JsonObject();

    public List<string> StringList(string name)
    {
        var raw = RawParam(name);
        if (raw is JsonArray arr) return arr.Select(x => Json.AsText(x)).ToList();
        var s = Str(name);
        return string.IsNullOrWhiteSpace(s) ? [] : s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    public JsonNode? Input(string name) => Inputs.TryGetValue(name, out var v) ? v : null;

    public string? InputText(string name) => Input(name) is { } v ? Json.AsText(v) : null;

    public AssetRef? InputAsset(string name) => AssetRef.All(Input(name)).FirstOrDefault();

    public List<AssetRef> InputAssets(string name) => AssetRef.All(Input(name)).ToList();

    public AssetRef RequireAsset(string name) =>
        InputAsset(name) ?? throw new NodeException($"Input '{name}' is required (connect a {Spec.Inputs.FirstOrDefault(i => i.Name == name)?.Kind ?? "file"})");

    public async Task<AssetRef> SaveBytesAsync(byte[] bytes, string fileName, string? mime = null) =>
        await Assets.SaveBytesAsync(bytes, fileName, mime, Meta, Ct);

    public async Task<AssetRef> DownloadAsync(string url, string? fileName = null, IReadOnlyDictionary<string, string>? headers = null) =>
        await Assets.DownloadAsync(url, fileName, Meta, Ct, headers);

    public async Task<AssetRef> SaveFileAsync(string path) => await Assets.SaveFileAsync(path, Meta, Ct);
}
