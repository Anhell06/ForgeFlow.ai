using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ForgeFlow.Core;
using ForgeFlow.Engine;
using ForgeFlow.Providers;

namespace ForgeFlow.Nodes.Builtin;

/// <summary>
/// Calls any HTTP endpoint — the generic way to plug in your own neural network server.
/// Supports JSON or multipart bodies, binary responses, async job polling and downloading result files.
/// </summary>
public class HttpRequestNode : NodeBase
{
    public static readonly ParamSpec[] HttpParams =
    [
        new("provider", "provider", "Provider (optional)", ProviderTypes: ["http", "openai", "comfyui", "fal", "replicate", "tripo", "meshy"],
            Help: "Gives {{provider.baseUrl}} and {{provider.apiKey}} to templates"),
        new("method", "select", "Method", JsonValue.Create("POST"), Options: ["GET", "POST", "PUT", "PATCH", "DELETE"]),
        new("url", "string", "URL", JsonValue.Create("http://localhost:8000/generate")),
        P.Json("headers", "Headers JSON", "e.g. {\"Authorization\":\"Bearer {{provider.apiKey}}\"}"),
        new("bodyMode", "select", "Body", JsonValue.Create("json"), Options: ["json", "multipart", "none"]),
        new("body", "code", "Body template", JsonValue.Create("{\n  \"prompt\": \"{{inputs.prompt | escape}}\"\n}"),
            Help: "JSON with templates. For multipart: JSON object of text fields."),
        new("files", "text", "Multipart files", JsonValue.Create(""), Help: "One per line: field=inputPort, e.g. image=image"),
        new("responseMode", "select", "Response", JsonValue.Create("json"), Options: ["json", "file", "text"]),
        new("pollUrl", "string", "Poll URL (async jobs)", Help: "e.g. http://localhost:8000/jobs/{{response.id}} — empty = no polling"),
        new("doneWhen", "string", "Done when", JsonValue.Create("status=completed"), Help: "path=value[|value2] checked on the poll response"),
        new("failWhen", "string", "Failed when", JsonValue.Create("status=failed|error")),
        new("resultUrl", "string", "Result URL", Help: "Optional final GET after polling, e.g. .../jobs/{{response.id}}/result"),
        new("resultPath", "string", "Result path", Help: "Dotted path to the useful part of the final JSON, e.g. output.files"),
        new("download", "bool", "Download file URLs found in the result", JsonValue.Create(true)),
        new("timeoutMin", "number", "Timeout (min)", JsonValue.Create(30)),
    ];

    public static readonly PortSpec[] HttpOutputs =
    [
        new("json", Kinds.Json), new("text", Kinds.Text), new("file", Kinds.Any), new("files", Kinds.File, List: true),
        new("image", Kinds.Image), new("model", Kinds.Model), new("video", Kinds.Video),
    ];

    public override NodeSpec Spec { get; } = new("http.request", "HTTP request (your model)", "Integrations",
        "Calls your own neural network server or any REST API. Templates: {{inputs.x}}, {{inputs.image | datauri}}, {{inputs.image | path}}. " +
        "Async APIs: set Poll URL using {{response.id}}.",
        [new("prompt", Kinds.Text), new("image", Kinds.Image), new("in", Kinds.Any)],
        HttpOutputs, HttpParams, "#64748b");

    public override Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx) => RunAsync(ctx);

    public static async Task<Dictionary<string, JsonNode?>> RunAsync(NodeContext ctx)
    {
        if (ctx.RawParam("provider") is JsonValue pv && pv.TryGetValue(out string? pid) && !string.IsNullOrWhiteSpace(pid))
        {
            var p = await ctx.Providers.ResolveAsync(pid, [], ctx.Ct);
            ctx.ExtendScope("provider", new JsonObject { ["baseUrl"] = p.BaseUrl, ["apiKey"] = p.ApiKey, ["model"] = p.DefaultModel, ["extra"] = p.Extra.DeepClone() });
        }
        var timeout = TimeSpan.FromMinutes(Math.Max(1, ctx.Num("timeoutMin", 30)));
        var method = new HttpMethod(ctx.StrOr("method", "POST"));
        var url = ctx.Str("url") ?? throw new NodeException("URL is required");
        var headers = ctx.JsonObjectParam("headers");

        using var req = new HttpRequestMessage(method, url);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, Json.AsText(v));
        var bodyMode = ctx.Str("bodyMode") ?? "json";
        if (method != HttpMethod.Get && bodyMode != "none")
        {
            var bodyText = ctx.Str("body") ?? "";
            if (bodyMode == "multipart")
            {
                var form = new MultipartFormDataContent();
                foreach (var (k, v) in Json.ParseObject(bodyText)) form.Add(new StringContent(Json.AsText(v)), k);
                foreach (var line in ctx.StringList("files"))
                {
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    foreach (var a in ctx.InputAssets(line[(eq + 1)..].Trim()))
                    {
                        var content = new ByteArrayContent(await File.ReadAllBytesAsync(a.Path, ctx.Ct));
                        content.Headers.ContentType = new MediaTypeHeaderValue(a.Mime);
                        form.Add(content, line[..eq].Trim(), a.Name);
                    }
                }
                req.Content = form;
            }
            else
            {
                req.Content = new StringContent(bodyText, Encoding.UTF8, "application/json");
            }
        }

        ctx.Log($"{method} {url}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        cts.CancelAfter(timeout);
        using var resp = await ctx.Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var responseMode = ctx.Str("responseMode") ?? "json";
        if (!resp.IsSuccessStatusCode)
            throw new NodeException($"HTTP {(int)resp.StatusCode}: {Truncate(await resp.Content.ReadAsStringAsync(cts.Token))}");

        if (responseMode == "file")
        {
            var mime = resp.Content.Headers.ContentType?.MediaType;
            var name = resp.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "result" + (MediaKinds.ExtensionFromMime(mime) ?? ".bin");
            await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
            var asset = await ctx.Assets.SaveStreamAsync(stream, name, mime, ctx.Meta, ctx.Ct);
            return FileOutputs(new JsonObject { ["file"] = asset.ToJson() }, "", [asset]);
        }

        var raw = await resp.Content.ReadAsStringAsync(cts.Token);
        JsonNode? response = responseMode == "text" ? JsonValue.Create(raw) : Json.Parse(raw) ?? JsonValue.Create(raw);
        ctx.ExtendScope("response", response?.DeepClone());

        if (ctx.Str("pollUrl") is { Length: > 0 } pollUrl)
        {
            var done = ParseCondition(ctx.Str("doneWhen"));
            var fail = ParseCondition(ctx.Str("failWhen"));
            response = await Providers.Http.PollAsync<JsonNode>(async () =>
            {
                using var poll = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                foreach (var (k, v) in headers) poll.Headers.TryAddWithoutValidation(k, Json.AsText(v));
                var state = await Providers.Http.SendJsonAsync(ctx.Http, poll, "poll", ctx.Ct);
                ctx.Progress(state["progress"] is JsonValue pg && pg.TryGetValue(out double d) ? (d > 1 ? d / 100 : d) : null,
                    done is null ? null : Json.AsText(Json.Select(state, done.Value.Path)));
                if (fail is not null && Matches(state, fail.Value)) throw new NodeException($"Job failed: {Truncate(state.ToJsonString())}");
                return done is null || Matches(state, done.Value) ? state : null;
            }, TimeSpan.FromSeconds(2), timeout, ctx.Ct);
            ctx.ExtendScope("poll", response.DeepClone());
        }

        if (ctx.Str("resultUrl") is { Length: > 0 } resultUrl)
        {
            using var get = new HttpRequestMessage(HttpMethod.Get, resultUrl);
            foreach (var (k, v) in headers) get.Headers.TryAddWithoutValidation(k, Json.AsText(v));
            response = await Providers.Http.SendJsonAsync(ctx.Http, get, "result", ctx.Ct);
        }

        var result = ctx.Str("resultPath") is { Length: > 0 } path ? Json.Select(response, path) : response;
        var files = ctx.Bool("download", true) ? await MediaHelpers.DownloadAllAsync(ctx, result) : [];
        // Servers that write to a shared disk can return local paths instead of URLs.
        foreach (var s in StringsOf(result).Where(s => s.Length < 400 && Path.IsPathRooted(s) && File.Exists(s)))
            files.Add(await ctx.SaveFileAsync(s));
        return FileOutputs(result, Json.AsText(result), files);
    }

    static IEnumerable<string> StringsOf(JsonNode? node) => node switch
    {
        JsonValue v when v.TryGetValue(out string? s) => [s!],
        JsonObject o => o.SelectMany(kv => StringsOf(kv.Value)),
        JsonArray a => a.SelectMany(StringsOf),
        _ => [],
    };

    static Dictionary<string, JsonNode?> FileOutputs(JsonNode? json, string text, List<Core.AssetRef> files) => Out(
        ("json", json?.DeepClone()), ("text", JsonValue.Create(text)), ("file", First(files)), ("files", ToArray(files)),
        ("image", First(files, "image")), ("model", First(files, "model")), ("video", First(files, "video")));

    static (string Path, string[] Values)? ParseCondition(string? cond)
    {
        if (string.IsNullOrWhiteSpace(cond) || !cond.Contains('=')) return null;
        var eq = cond.IndexOf('=');
        return (cond[..eq].Trim(), cond[(eq + 1)..].Split('|', StringSplitOptions.TrimEntries));
    }

    static bool Matches(JsonNode state, (string Path, string[] Values) c) =>
        c.Values.Contains(Json.AsText(Json.Select(state, c.Path)), StringComparer.OrdinalIgnoreCase);

    static string Truncate(string s) => s.Length > 800 ? s[..800] + "…" : s;
}

/// <summary>
/// A user-defined node backed by an HTTP call, loaded from DataDir/nodes/*.json:
/// { "type": "custom.my-net", "title": "...", "category": "...", "description": "...",
///   "inputs": [{"name":"prompt","kind":"text"}], "params": [{"name":"steps","kind":"number","default":30}],
///   "http": { "url": "...", "method": "POST", "body": "...", "pollUrl": "...", ... } }
/// </summary>
public class CustomHttpNode(NodeSpec spec, JsonObject http) : NodeBase
{
    public override NodeSpec Spec { get; } = spec;

    public static CustomHttpNode FromDefinition(JsonObject def)
    {
        var type = def.Str("type") ?? throw new InvalidOperationException("Custom node needs a 'type'");
        if (!type.StartsWith("custom.")) type = "custom." + type;
        var http = def["http"] as JsonObject ?? throw new InvalidOperationException("Custom node needs an 'http' section");
        var inputs = (def["inputs"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(i => new PortSpec(i.Str("name")!, i.Str("kind") ?? Kinds.Any, i["list"]?.GetValue<bool>() ?? false, Label: i.Str("label")))
            .ToArray();
        var userParams = (def["params"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(p => new ParamSpec(p.Str("name")!, p.Str("kind") ?? "string", p.Str("label"), p["default"]?.DeepClone(),
                (p["options"] as JsonArray)?.Select(o => Json.AsText(o)).ToArray(), Help: p.Str("help")))
            .ToArray();
        var spec = new NodeSpec(type, def.Str("title") ?? type, def.Str("category") ?? "Custom", def.Str("description") ?? "",
            inputs, HttpRequestNode.HttpOutputs, userParams, def.Str("color") ?? "#8b5cf6", Custom: true);
        return new CustomHttpNode(spec, (JsonObject)http.DeepClone());
    }

    public override Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        // Run the generic HTTP node with the definition's fixed http settings; user params are visible as {{params.x}}.
        var def = new NodeDef { Id = ctx.Def.Id, Type = ctx.Def.Type, Title = ctx.Def.Title, Params = (JsonObject)http.DeepClone() };
        var merged = new NodeSpec(Spec.Type, Spec.Title, Spec.Category, Spec.Description, Spec.Inputs, Spec.Outputs,
            [.. HttpRequestNode.HttpParams, .. Spec.Params], Spec.Color, true);
        foreach (var p in Spec.Params) def.Params[p.Name] = ctx.RawParam(p.Name)?.DeepClone();
        var inner = new NodeContext
        {
            RunId = ctx.RunId, Def = def, Spec = merged, Inputs = ctx.Inputs, WorkflowInputs = ctx.WorkflowInputs, WorkDir = ctx.WorkDir,
            Services = ctx.Services, Assets = ctx.Assets, Providers = ctx.Providers, Http = ctx.Http, Ct = ctx.Ct, Log = ctx.Log,
            Progress = ctx.Progress, ItemIndex = ctx.ItemIndex,
        };
        return HttpRequestNode.RunAsync(inner);
    }
}

public class ComfyWorkflowNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("comfyui.workflow", "ComfyUI workflow", "Integrations",
        "Runs a ComfyUI workflow exported in API format (Workflow → Export (API)). Connected images are uploaded first; " +
        "reference them as {{comfy.image}}, text as {{inputs.prompt | escape}}. Works for image, video and 3D (Hunyuan3D, TRELLIS) workflows.",
        [new("prompt", Kinds.Text), new("image", Kinds.Image), new("images", Kinds.Image, List: true), new("in", Kinds.Any)],
        [
            new("image", Kinds.Image, Label: "image (1st)", Help: "Only the first image"),
            new("images", Kinds.Image, List: true, Label: "images (all)", Help: "Every image the workflow saved"),
            new("model", Kinds.Model), new("video", Kinds.Video), new("files", Kinds.File, List: true),
        ],
        [
            P.Provider("comfyui"),
            new("workflow", "code", "Workflow (API JSON)", JsonValue.Create("{}")),
            new("seed", "string", "Seed", JsonValue.Create(""), Help: "Available as {{params.seed}}; empty = random → {{comfy.seed}}"),
        ], "#22c55e");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var provider = await ctx.Providers.ResolveAsync(ctx.Str("provider"), ["comfyui"], ctx.Ct);
        var comfy = new ComfyClient(provider, ctx.Http);
        var uploaded = new JsonObject();
        if (ctx.InputAsset("image") is { } img)
            uploaded["image"] = await comfy.UploadImageAsync(await File.ReadAllBytesAsync(img.Path, ctx.Ct), img.Name, ctx.Ct);
        var list = new JsonArray();
        foreach (var a in ctx.InputAssets("images"))
            list.Add(await comfy.UploadImageAsync(await File.ReadAllBytesAsync(a.Path, ctx.Ct), a.Name, ctx.Ct));
        uploaded["images"] = list;
        uploaded["seed"] = long.TryParse(ctx.Str("seed"), out var seed) ? seed : Random.Shared.NextInt64(1, 1L << 48);
        ctx.ExtendScope("comfy", uploaded);

        var rendered = ctx.Str("workflow") ?? "{}";
        var workflow = Json.Parse(rendered) as JsonObject ?? throw new NodeException("Workflow is not valid JSON after templating");
        if (workflow.Count == 0) throw new NodeException("Paste a ComfyUI workflow in API format");
        var files = new List<Core.AssetRef>();
        foreach (var (url, name) in await comfy.RunAsync(workflow, ctx.Progress, ctx.Ct))
            files.Add(await ctx.DownloadAsync(url, name));
        var images = files.Where(f => f.Kind == "image").ToList();
        return Out(("image", First(images)), ("images", ToArray(images)), ("model", First(files, "model")),
            ("video", First(files, "video")), ("files", ToArray(files)));
    }
}
