using System.Diagnostics;
using System.Text.Json.Nodes;
using ForgeFlow.Agents;
using ForgeFlow.Core;
using ForgeFlow.Providers;

namespace ForgeFlow.Nodes.Builtin;

/// <summary>Shared helpers for nodes that call hosted model APIs and download whatever files come back.</summary>
public static class MediaHelpers
{
    static readonly string[] FileHosts = ["fal.media", "replicate.delivery", "tripo3d", "meshy.ai", "amazonaws.com", "storage.googleapis.com"];

    public static bool LooksLikeFile(string url)
    {
        if (url.StartsWith("data:")) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var ext = Path.GetExtension(u.AbsolutePath);
        if (!string.IsNullOrEmpty(ext)) return MediaKinds.IsKnownFileExtension(ext);
        return FileHosts.Any(h => u.Host.Contains(h));
    }

    public static async Task<List<AssetRef>> DownloadAllAsync(NodeContext ctx, JsonNode? result, string? nameHint = null, int max = 24)
    {
        var assets = new List<AssetRef>();
        var urls = Json.FindUrls(result).Distinct().Where(LooksLikeFile).Take(max).ToList();
        foreach (var (url, i) in urls.Select((u, i) => (u, i)))
        {
            try
            {
                var hint = nameHint is null ? null : urls.Count > 1 ? $"{Path.GetFileNameWithoutExtension(nameHint)}-{i}{Path.GetExtension(nameHint)}" : nameHint;
                // Only force the hint's extension when the URL has none.
                if (hint is not null && Uri.TryCreate(url, UriKind.Absolute, out var u) && Path.HasExtension(u.AbsolutePath)) hint = null;
                assets.Add(await ctx.DownloadAsync(url, hint));
            }
            catch (Exception ex)
            {
                ctx.Log($"download failed {url[..Math.Min(url.Length, 100)]}: {ex.Message}");
            }
        }
        ctx.Log($"saved {assets.Count} file(s)");
        return assets;
    }

    public static async Task<string> DataUriAsync(AssetRef a, CancellationToken ct) => await Storage.AssetStore.ToDataUriAsync(a, ct);

    /// <summary>Runs a model on fal or Replicate with either the user's JSON input template or the given defaults.</summary>
    public static async Task<JsonNode?> RunHostedAsync(NodeContext ctx, ProviderInfo provider, string model, JsonObject defaults)
    {
        var input = ctx.JsonParam("input") as JsonObject ?? defaults;
        foreach (var (k, v) in ctx.JsonObjectParam("extra")) input[k] = v?.DeepClone();
        ctx.Log($"{provider.Name} → {model}");
        return provider.Type switch
        {
            "fal" => await new FalClient(provider, ctx.Http).RunAsync(model, input, ctx.Progress, ctx.Log, ctx.Ct),
            "replicate" => await new ReplicateClient(provider, ctx.Http).RunAsync(model, input, ctx.Progress, ctx.Ct),
            _ => throw new NodeException($"Provider type {provider.Type} is not a hosted-model provider"),
        };
    }
}

public class ImageGenerateNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("image.generate", "Generate image", "AI · Image",
        "Text (+ optional reference images) → image. OpenAI GPT Image, or any image model on fal / Replicate.",
        [new("prompt", Kinds.Text), new("reference", Kinds.Image, List: true)],
        [
            new("image", Kinds.Image, Label: "image (1st)", Help: "Only the first image"),
            new("images", Kinds.Image, List: true, Label: "images (all)", Help: "Every generated image (Images > 1)"),
        ],
        [
            P.Provider("openai", "fal", "replicate"),
            P.Model("gpt-image-1", "OpenAI: gpt-image-1 … · fal: fal-ai/flux/dev … · Replicate: black-forest-labs/flux-schnell"),
            P.Prompt(),
            new("size", "string", "Size", JsonValue.Create("1024x1024"), Help: "OpenAI: 1024x1024, 1536x1024, 1024x1536, auto"),
            new("count", "number", "Images", JsonValue.Create(1)),
            new("quality", "string", "Quality", Help: "OpenAI: low | medium | high"),
            P.Json("input", "Input JSON (fal/Replicate)", "Replaces the default {prompt, image_url}. Templates allowed: {{inputs.reference | first | datauri}}"),
            P.Json("extra", "Extra fields", "Merged into the request body"),
        ], "#f59e0b");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var provider = await ctx.Providers.ResolveAsync(ctx.Str("provider"), ["openai", "fal", "replicate"], ctx.Ct);
        var prompt = ctx.Str("prompt") ?? "";
        if (string.IsNullOrWhiteSpace(prompt)) throw new NodeException("Prompt is empty");
        var refs = ctx.InputAssets("reference").Where(a => a.Kind == "image").ToList();
        var count = Math.Clamp(ctx.Int("count", 1), 1, 8);
        List<AssetRef> images = [];

        if (provider.Type == "openai")
        {
            var model = ctx.StrOr("model", "gpt-image-1");
            var references = new List<(byte[], string, string)>();
            foreach (var r in refs) references.Add((await File.ReadAllBytesAsync(r.Path, ctx.Ct), r.Name, r.Mime));
            ctx.Log($"{provider.Name} {model}: {prompt[..Math.Min(prompt.Length, 200)]}");
            var bytes = await OpenAiImages.GenerateAsync(provider, ctx.Http, model, prompt, ctx.Str("size"), count, ctx.Str("quality"),
                references, ctx.JsonObjectParam("extra"), ctx.Ct);
            foreach (var (b, i) in bytes.Select((b, i) => (b, i)))
                images.Add(await ctx.SaveBytesAsync(b, $"image-{i}.png", "image/png"));
        }
        else
        {
            var model = ctx.Str("model") is { Length: > 0 } m && m != "gpt-image-1" ? m
                : provider.Type == "fal" ? "fal-ai/flux/dev" : "black-forest-labs/flux-schnell";
            var defaults = new JsonObject { ["prompt"] = prompt };
            if (provider.Type == "fal") defaults["num_images"] = count;
            if (refs.Count > 0) defaults[provider.Type == "fal" ? "image_url" : "image"] = await MediaHelpers.DataUriAsync(refs[0], ctx.Ct);
            var result = await MediaHelpers.RunHostedAsync(ctx, provider, model, defaults);
            images = (await MediaHelpers.DownloadAllAsync(ctx, result, "image.png")).Where(a => a.Kind == "image").ToList();
        }
        if (images.Count == 0) throw new NodeException("The model returned no images");
        return Out(("image", images[0].ToJson()), ("images", ToArray(images)));
    }
}

public class VideoGenerateNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("video.generate", "Generate video / animation", "AI · Video",
        "Image + prompt → video clip (Kling, Wan, Seedance, Hailuo… on fal or Replicate). Use it to animate a character before 3D.",
        [new("prompt", Kinds.Text), new("image", Kinds.Image), new("endImage", Kinds.Image)],
        [new("video", Kinds.Video), new("files", Kinds.File, List: true)],
        [
            P.Provider("fal", "replicate"),
            P.Model("fal-ai/kling-video/v2.1/standard/image-to-video", "Any image-to-video model id of the provider"),
            P.Prompt("{{inputs.prompt}}"),
            new("duration", "string", "Duration (s)", JsonValue.Create("5")),
            P.Json("input", "Input JSON", "Replaces the default {prompt, image_url, duration}. Example: {\"prompt\":\"{{inputs.prompt | escape}}\",\"image_url\":\"{{inputs.image | datauri}}\"}"),
            P.Json("extra", "Extra fields", "Merged into the input"),
        ], "#ef4444");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var provider = await ctx.Providers.ResolveAsync(ctx.Str("provider"), ["fal", "replicate"], ctx.Ct);
        var model = ctx.StrOr("model", "fal-ai/kling-video/v2.1/standard/image-to-video");
        var defaults = new JsonObject { ["prompt"] = ctx.Str("prompt") ?? "" };
        if (ctx.Str("duration") is { Length: > 0 } d) defaults["duration"] = d;
        if (ctx.InputAsset("image") is { } img)
            defaults[provider.Type == "fal" ? "image_url" : "start_image"] = await MediaHelpers.DataUriAsync(img, ctx.Ct);
        if (ctx.InputAsset("endImage") is { } end)
            defaults[provider.Type == "fal" ? "tail_image_url" : "end_image"] = await MediaHelpers.DataUriAsync(end, ctx.Ct);
        var result = await MediaHelpers.RunHostedAsync(ctx, provider, model, defaults);
        var files = await MediaHelpers.DownloadAllAsync(ctx, result, "video.mp4");
        var video = files.FirstOrDefault(f => f.Kind == "video") ?? throw new NodeException("The model returned no video");
        return Out(("video", video.ToJson()), ("files", ToArray(files)));
    }
}

public class Model3dGenerateNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("model.generate3d", "Generate 3D model", "AI · 3D",
        "Image, multi-view images or text → textured 3D model (.glb). Tripo, Meshy, or TRELLIS / Hunyuan3D / Rodin on fal & Replicate.",
        [new("image", Kinds.Image), new("views", Kinds.Image, List: true, Help: "front, left, back, right"), new("prompt", Kinds.Text)],
        [new("model", Kinds.Model), new("preview", Kinds.Image), new("files", Kinds.File, List: true), new("meta", Kinds.Json)],
        [
            P.Provider("tripo", "meshy", "fal", "replicate"),
            new("mode", "select", "Mode", JsonValue.Create("auto"), Options: ["auto", "image", "multiview", "text"]),
            P.Model(help: "fal/Replicate model id, e.g. fal-ai/trellis, fal-ai/hunyuan3d/v2. Tripo: model_version via options."),
            P.Prompt("{{inputs.prompt}}"),
            P.Json("options", "Options JSON", "Tripo/Meshy body fields, e.g. {\"texture\":true,\"pbr\":true,\"face_limit\":20000}"),
            P.Json("input", "Input JSON (fal/Replicate)", "Replaces the default {image_url}"),
            P.Json("extra", "Extra fields (fal/Replicate)"),
        ], "#14b8a6");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var provider = await ctx.Providers.ResolveAsync(ctx.Str("provider"), ["tripo", "meshy", "fal", "replicate"], ctx.Ct);
        var image = ctx.InputAsset("image");
        var views = ctx.InputAssets("views").Where(v => v.Kind == "image").ToList();
        var prompt = ctx.Str("prompt");
        var mode = ctx.Str("mode") ?? "auto";
        if (mode == "auto") mode = views.Count >= 2 ? "multiview" : image is not null ? "image" : "text";
        if (mode == "text" && string.IsNullOrWhiteSpace(prompt)) throw new NodeException("Connect an image or give a prompt");
        if (mode == "image" && image is null) throw new NodeException("Image mode needs an image input");
        var options = ctx.JsonObjectParam("options");

        return provider.Type switch
        {
            "tripo" => await TripoAsync(ctx, provider, mode, image, views, prompt, options),
            "meshy" => await MeshyAsync(ctx, provider, mode, image, prompt, options),
            _ => await HostedAsync(ctx, provider, image, views, prompt),
        };
    }

    async Task<Dictionary<string, JsonNode?>> TripoAsync(NodeContext ctx, ProviderInfo p, string mode, AssetRef? image, List<AssetRef> views,
        string? prompt, JsonObject options)
    {
        var tripo = new TripoClient(p, ctx.Http);
        async Task<JsonObject> FileRef(AssetRef a)
        {
            var token = await tripo.UploadAsync(await File.ReadAllBytesAsync(a.Path, ctx.Ct), a.Name, a.Mime, ctx.Ct);
            return new JsonObject { ["type"] = a.Mime == "image/png" ? "png" : a.Mime == "image/webp" ? "webp" : "jpg", ["file_token"] = token };
        }

        var body = new JsonObject();
        switch (mode)
        {
            case "text":
                body["type"] = "text_to_model";
                body["prompt"] = prompt;
                break;
            case "multiview":
                body["type"] = "multiview_to_model";
                var files = new JsonArray();
                foreach (var v in views.Take(4)) files.Add(await FileRef(v));
                body["files"] = files;
                break;
            default:
                body["type"] = "image_to_model";
                body["file"] = await FileRef(image!);
                break;
        }
        foreach (var (k, v) in options) body[k] = v?.DeepClone();
        var taskId = await tripo.CreateTaskAsync(body, ctx.Ct);
        ctx.Log($"Tripo task {taskId} ({body["type"]})");
        var data = await tripo.WaitAsync(taskId, ctx.Progress, ctx.Ct);
        return await TripoOutputs(ctx, taskId, data);
    }

    public static async Task<Dictionary<string, JsonNode?>> TripoOutputs(NodeContext ctx, string taskId, JsonObject data)
    {
        var output = data["output"] as JsonObject ?? new JsonObject();
        var files = new List<AssetRef>();
        AssetRef? model = null, preview = null;
        foreach (var key in new[] { "pbr_model", "model", "base_model" })
            if (output.Str(key) is { } url && model is null)
                files.Add(model = await ctx.DownloadAsync(url, $"tripo-{taskId}.glb"));
        if (output.Str("rendered_image") is { } img)
            files.Add(preview = await ctx.DownloadAsync(img, $"tripo-{taskId}.webp"));
        if (model is null) throw new NodeException($"Tripo task finished without a model: {output.ToJsonString()}");
        var meta = new JsonObject { ["provider"] = "tripo", ["taskId"] = taskId, ["output"] = output.DeepClone() };
        return Out(("model", model.ToJson()), ("preview", preview?.ToJson()), ("files", ToArray(files)), ("meta", meta));
    }

    async Task<Dictionary<string, JsonNode?>> MeshyAsync(NodeContext ctx, ProviderInfo p, string mode, AssetRef? image, string? prompt, JsonObject options)
    {
        var meshy = new MeshyClient(p, ctx.Http);
        string endpoint;
        var body = new JsonObject();
        if (mode == "text")
        {
            endpoint = "/openapi/v2/text-to-3d";
            body["mode"] = "preview";
            body["prompt"] = prompt;
        }
        else
        {
            endpoint = "/openapi/v1/image-to-3d";
            body["image_url"] = await MediaHelpers.DataUriAsync(image!, ctx.Ct);
        }
        foreach (var (k, v) in options) body[k] = v?.DeepClone();
        var id = await meshy.CreateAsync(endpoint, body, ctx.Ct);
        ctx.Log($"Meshy task {id}");
        var task = await meshy.WaitAsync(endpoint, id, ctx.Progress, ctx.Ct);
        var files = new List<AssetRef>();
        var urls = task["model_urls"] as JsonObject ?? new JsonObject();
        AssetRef? model = null;
        foreach (var fmt in new[] { "glb", "fbx", "obj", "usdz" })
            if (urls.Str(fmt) is { Length: > 0 } u)
            {
                var a = await ctx.DownloadAsync(u, $"meshy-{id}.{fmt}");
                files.Add(a);
                model ??= a;
            }
        AssetRef? preview = task.Str("thumbnail_url") is { Length: > 0 } t ? await ctx.DownloadAsync(t, $"meshy-{id}.png") : null;
        if (preview is not null) files.Add(preview);
        if (model is null) throw new NodeException("Meshy returned no model");
        return Out(("model", model.ToJson()), ("preview", preview?.ToJson()), ("files", ToArray(files)),
            ("meta", new JsonObject { ["provider"] = "meshy", ["taskId"] = id }));
    }

    async Task<Dictionary<string, JsonNode?>> HostedAsync(NodeContext ctx, ProviderInfo p, AssetRef? image, List<AssetRef> views, string? prompt)
    {
        var model = ctx.Str("model") is { Length: > 0 } m ? m : p.Type == "fal" ? "fal-ai/trellis" : throw new NodeException("Set a model id");
        var defaults = new JsonObject();
        if (image is not null) defaults[p.Type == "fal" ? "image_url" : "image"] = await MediaHelpers.DataUriAsync(image, ctx.Ct);
        if (views.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var v in views) arr.Add(await MediaHelpers.DataUriAsync(v, ctx.Ct));
            defaults[p.Type == "fal" ? "image_urls" : "images"] = arr;
        }
        if (!string.IsNullOrWhiteSpace(prompt)) defaults["prompt"] = prompt;
        var result = await MediaHelpers.RunHostedAsync(ctx, p, model, defaults);
        var files = await MediaHelpers.DownloadAllAsync(ctx, result, "model.glb");
        var mdl = files.FirstOrDefault(f => f.Kind == "model") ?? throw new NodeException("The model returned no 3D file");
        return Out(("model", mdl.ToJson()), ("preview", First(files, "image")), ("files", ToArray(files)),
            ("meta", new JsonObject { ["provider"] = p.Type, ["model"] = model, ["result"] = result?.DeepClone() }));
    }
}

public class TripoTaskNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("tripo.task", "Tripo task (rig / animate / convert)", "AI · 3D",
        "Runs any Tripo task on a model generated earlier: animate_rig, animate_retarget (preset:walk, preset:run…), convert_model, texture_model…",
        [new("meta", Kinds.Json, Help: "'meta' output of a Tripo Generate 3D node"), new("in", Kinds.Any)],
        [new("model", Kinds.Model), new("preview", Kinds.Image), new("files", Kinds.File, List: true), new("meta", Kinds.Json)],
        [
            P.Provider("tripo"),
            P.Json("body", "Task JSON",
                "Templates allowed. Rig: {\"type\":\"animate_rig\",\"original_model_task_id\":\"{{inputs.meta.taskId}}\",\"out_format\":\"glb\"}",
                "{\"type\":\"animate_rig\",\"original_model_task_id\":\"{{inputs.meta.taskId}}\",\"out_format\":\"glb\"}"),
        ], "#14b8a6");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var provider = await ctx.Providers.ResolveAsync(ctx.Str("provider"), ["tripo"], ctx.Ct);
        var body = ctx.JsonParam("body") as JsonObject ?? throw new NodeException("Task JSON is required");
        var tripo = new TripoClient(provider, ctx.Http);
        var taskId = await tripo.CreateTaskAsync(body, ctx.Ct);
        ctx.Log($"Tripo task {taskId} ({body["type"]})");
        var data = await tripo.WaitAsync(taskId, ctx.Progress, ctx.Ct);
        return await Model3dGenerateNode.TripoOutputs(ctx, taskId, data);
    }
}

public class VideoFramesNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("video.frames", "Video → frames", "Video",
        "Extracts frames with ffmpeg (must be on PATH). Useful to show an animation to a vision model or an agent.",
        [new("video", Kinds.Video)],
        [new("frames", Kinds.Image, List: true), new("image", Kinds.Image)],
        [
            new("fps", "number", "Frames per second", JsonValue.Create(4)),
            new("maxFrames", "number", "Max frames", JsonValue.Create(16)),
            new("ffmpeg", "string", "ffmpeg path", JsonValue.Create("ffmpeg")),
        ], "#ef4444");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var video = ctx.RequireAsset("video");
        var outDir = Directory.CreateDirectory(Path.Combine(ctx.WorkDir, "frames")).FullName;
        var psi = CommandResolver.Build(ctx.StrOr("ffmpeg", "ffmpeg"),
            ["-hide_banner", "-loglevel", "error", "-y", "-i", video.Path, "-vf", $"fps={ctx.Num("fps", 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
             "-frames:v", ctx.Int("maxFrames", 16).ToString(), Path.Combine(outDir, "frame_%03d.png")]);
        psi.RedirectStandardError = true;
        using var proc = Process.Start(psi) ?? throw new NodeException("Could not start ffmpeg");
        var err = await proc.StandardError.ReadToEndAsync(ctx.Ct);
        await proc.WaitForExitAsync(ctx.Ct);
        if (proc.ExitCode != 0) throw new NodeException($"ffmpeg failed: {err}");
        var frames = new List<AssetRef>();
        foreach (var f in Directory.GetFiles(outDir, "*.png").Order()) frames.Add(await ctx.SaveFileAsync(f));
        return Out(("frames", ToArray(frames)), ("image", First(frames)));
    }
}
