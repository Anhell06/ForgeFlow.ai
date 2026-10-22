using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ForgeFlow.Core;

namespace ForgeFlow.Providers;

public static class Http
{
    public static async Task<JsonNode> SendJsonAsync(HttpClient http, HttpRequestMessage req, string who, CancellationToken ct)
    {
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{who}: HTTP {(int)resp.StatusCode}: {(raw.Length > 800 ? raw[..800] + "…" : raw)}");
        return JsonNode.Parse(string.IsNullOrWhiteSpace(raw) ? "{}" : raw)!;
    }

    public static HttpRequestMessage Json(HttpMethod method, string url, JsonNode? body, string? bearer = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (bearer is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return req;
    }

    /// <summary>Polls until <paramref name="check"/> returns a non-null result.</summary>
    public static async Task<T> PollAsync<T>(Func<Task<T?>> check, TimeSpan interval, TimeSpan timeout, CancellationToken ct) where T : class
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await check() is { } done) return done;
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out after {timeout.TotalMinutes:0} min");
            await Task.Delay(interval, ct);
        }
    }
}

/// <summary>Tripo 3D OpenAPI v2: upload, create task, poll task.</summary>
public class TripoClient(ProviderInfo p, HttpClient http)
{
    string Base => p.BaseUrl ?? "https://api.tripo3d.ai/v2/openapi";

    public async Task<string> UploadAsync(byte[] bytes, string fileName, string mime, CancellationToken ct)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", fileName);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{Base}/upload") { Content = form };
        req.Headers.Authorization = new("Bearer", p.RequireKey());
        var json = await Http.SendJsonAsync(http, req, "Tripo upload", ct);
        return json["data"]?["image_token"]?.GetValue<string>() ?? throw new InvalidOperationException($"Tripo upload: no image_token in {json.ToJsonString()}");
    }

    public async Task<string> CreateTaskAsync(JsonObject body, CancellationToken ct)
    {
        using var req = Http.Json(HttpMethod.Post, $"{Base}/task", body, p.RequireKey());
        var json = await Http.SendJsonAsync(http, req, "Tripo task", ct);
        if (json["code"]?.GetValue<int>() is { } code && code != 0)
            throw new InvalidOperationException($"Tripo error {code}: {json["message"]}");
        return json["data"]?["task_id"]?.GetValue<string>() ?? throw new InvalidOperationException($"Tripo: no task_id in {json.ToJsonString()}");
    }

    public Task<JsonObject> WaitAsync(string taskId, Action<double?, string?> progress, CancellationToken ct, int timeoutMinutes = 30) =>
        Http.PollAsync<JsonObject>(async () =>
        {
            using var req = Http.Json(HttpMethod.Get, $"{Base}/task/{taskId}", null, p.RequireKey());
            var data = (await Http.SendJsonAsync(http, req, "Tripo poll", ct))["data"]!.AsObject();
            var status = data.Str("status");
            progress(data["progress"]?.GetValue<double>() / 100.0, status);
            return status switch
            {
                "success" => data,
                "failed" or "cancelled" or "banned" or "expired" or "unknown" => throw new InvalidOperationException($"Tripo task {taskId} {status}"),
                _ => null,
            };
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(timeoutMinutes), ct);
}

/// <summary>Meshy OpenAPI: image-to-3d and text-to-3d.</summary>
public class MeshyClient(ProviderInfo p, HttpClient http)
{
    string Base => p.BaseUrl ?? "https://api.meshy.ai";

    public async Task<string> CreateAsync(string endpoint, JsonObject body, CancellationToken ct)
    {
        using var req = Http.Json(HttpMethod.Post, $"{Base}{endpoint}", body, p.RequireKey());
        var json = await Http.SendJsonAsync(http, req, "Meshy", ct);
        return json["result"]?.GetValue<string>() ?? throw new InvalidOperationException($"Meshy: no task id in {json.ToJsonString()}");
    }

    public Task<JsonObject> WaitAsync(string endpoint, string id, Action<double?, string?> progress, CancellationToken ct) =>
        Http.PollAsync<JsonObject>(async () =>
        {
            using var req = Http.Json(HttpMethod.Get, $"{Base}{endpoint}/{id}", null, p.RequireKey());
            var task = (await Http.SendJsonAsync(http, req, "Meshy poll", ct)).AsObject();
            var status = task.Str("status");
            progress(task["progress"]?.GetValue<double>() / 100.0, status);
            return status switch
            {
                "SUCCEEDED" => task,
                "FAILED" or "CANCELED" or "EXPIRED" => throw new InvalidOperationException($"Meshy task {status}: {task["task_error"]?["message"]}"),
                _ => null,
            };
        }, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(30), ct);
}

/// <summary>fal.ai queue API: submit to any model id, poll status_url, fetch response_url.</summary>
public class FalClient(ProviderInfo p, HttpClient http)
{
    string Base => p.BaseUrl ?? "https://queue.fal.run";

    HttpRequestMessage Req(HttpMethod m, string url, JsonNode? body = null)
    {
        var req = Http.Json(m, url, body);
        req.Headers.TryAddWithoutValidation("Authorization", "Key " + p.RequireKey());
        return req;
    }

    public async Task<JsonNode> RunAsync(string model, JsonObject input, Action<double?, string?> progress, Action<string> log, CancellationToken ct, int timeoutMinutes = 30)
    {
        using var submit = Req(HttpMethod.Post, $"{Base}/{model.Trim('/')}", input);
        var queued = await Http.SendJsonAsync(http, submit, $"fal {model}", ct);
        var statusUrl = queued["status_url"]?.GetValue<string>() ?? throw new InvalidOperationException($"fal: unexpected response {queued.ToJsonString()}");
        var responseUrl = queued["response_url"]!.GetValue<string>();
        log($"fal request {queued["request_id"]}");

        await Http.PollAsync<string>(async () =>
        {
            using var req = Req(HttpMethod.Get, statusUrl);
            var s = await Http.SendJsonAsync(http, req, "fal status", ct);
            var status = s["status"]?.GetValue<string>();
            progress(null, status);
            return status == "COMPLETED" ? status : null;
        }, TimeSpan.FromSeconds(3), TimeSpan.FromMinutes(timeoutMinutes), ct);

        using var result = Req(HttpMethod.Get, responseUrl);
        return await Http.SendJsonAsync(http, result, $"fal {model} result", ct);
    }
}

/// <summary>Replicate predictions API: "owner/name" (official models) or "owner/name:version".</summary>
public class ReplicateClient(ProviderInfo p, HttpClient http)
{
    string Base => p.BaseUrl ?? "https://api.replicate.com/v1";

    public async Task<JsonNode?> RunAsync(string model, JsonObject input, Action<double?, string?> progress, CancellationToken ct, int timeoutMinutes = 30)
    {
        HttpRequestMessage req;
        var colon = model.IndexOf(':');
        if (colon > 0)
            req = Http.Json(HttpMethod.Post, $"{Base}/predictions", new JsonObject { ["version"] = model[(colon + 1)..], ["input"] = input }, p.RequireKey());
        else
            req = Http.Json(HttpMethod.Post, $"{Base}/models/{model}/predictions", new JsonObject { ["input"] = input }, p.RequireKey());
        JsonNode prediction;
        using (req) prediction = await Http.SendJsonAsync(http, req, $"Replicate {model}", ct);
        var getUrl = prediction["urls"]?["get"]?.GetValue<string>() ?? $"{Base}/predictions/{prediction["id"]}";

        var done = await Http.PollAsync<JsonObject>(async () =>
        {
            using var poll = Http.Json(HttpMethod.Get, getUrl, null, p.RequireKey());
            var pr = (await Http.SendJsonAsync(http, poll, "Replicate poll", ct)).AsObject();
            var status = pr.Str("status");
            progress(null, status);
            return status switch
            {
                "succeeded" => pr,
                "failed" or "canceled" => throw new InvalidOperationException($"Replicate {status}: {pr["error"]}"),
                _ => null,
            };
        }, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(timeoutMinutes), ct);
        return done["output"];
    }
}

/// <summary>ComfyUI HTTP API: upload inputs, queue an API-format workflow, wait for history, list output files.</summary>
public class ComfyClient(ProviderInfo p, HttpClient http)
{
    string Base => p.BaseUrl ?? "http://127.0.0.1:8188";

    HttpRequestMessage Auth(HttpRequestMessage req)
    {
        if (!string.IsNullOrEmpty(p.ApiKey)) req.Headers.Authorization = new("Bearer", p.ApiKey);
        return req;
    }

    public async Task<string> UploadImageAsync(byte[] bytes, string fileName, CancellationToken ct)
    {
        var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "image", fileName }, { new StringContent("true"), "overwrite" } };
        using var req = Auth(new HttpRequestMessage(HttpMethod.Post, $"{Base}/upload/image") { Content = form });
        var json = await Http.SendJsonAsync(http, req, "ComfyUI upload", ct);
        var name = json["name"]!.GetValue<string>();
        var sub = json["subfolder"]?.GetValue<string>();
        return string.IsNullOrEmpty(sub) ? name : $"{sub}/{name}";
    }

    public async Task<List<(string Url, string FileName)>> RunAsync(JsonObject workflow, Action<double?, string?> progress, CancellationToken ct, int timeoutMinutes = 60)
    {
        using var req = Auth(Http.Json(HttpMethod.Post, $"{Base}/prompt", new JsonObject { ["prompt"] = workflow, ["client_id"] = "forgeflow" }));
        var queued = await Http.SendJsonAsync(http, req, "ComfyUI queue", ct);
        if (queued["node_errors"] is JsonObject errs && errs.Count > 0)
            throw new InvalidOperationException($"ComfyUI rejected the workflow: {errs.ToJsonString()}");
        var promptId = queued["prompt_id"]!.GetValue<string>();

        var entry = await Http.PollAsync<JsonObject>(async () =>
        {
            using var h = Auth(new HttpRequestMessage(HttpMethod.Get, $"{Base}/history/{promptId}"));
            var hist = await Http.SendJsonAsync(http, h, "ComfyUI history", ct);
            progress(null, "running");
            if (hist[promptId] is not JsonObject e) return null;
            if (e["status"]?["status_str"]?.GetValue<string>() == "error")
                throw new InvalidOperationException($"ComfyUI execution error: {e["status"]?["messages"]?.ToJsonString()}");
            return e["outputs"] is JsonObject { Count: > 0 } ? e : null;
        }, TimeSpan.FromSeconds(2), TimeSpan.FromMinutes(timeoutMinutes), ct);

        var files = new List<(string, string)>();
        foreach (var (_, nodeOut) in entry["outputs"]!.AsObject())
        {
            if (nodeOut is not JsonObject o) continue;
            foreach (var (_, list) in o)
            {
                if (list is not JsonArray arr) continue;
                foreach (var item in arr.OfType<JsonObject>())
                {
                    if (item.Str("filename") is not { } fn) continue;
                    var url = $"{Base}/view?filename={Uri.EscapeDataString(fn)}&subfolder={Uri.EscapeDataString(item.Str("subfolder") ?? "")}&type={item.Str("type") ?? "output"}";
                    files.Add((url, fn));
                }
            }
        }
        return files;
    }
}
