using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeFlow.Core;

namespace ForgeFlow.Providers;

public record LlmTool(string Name, string Description, JsonObject InputSchema);
public record LlmToolCall(string Id, string Name, JsonObject Arguments);
public record LlmImage(string MediaType, string Base64);
public record LlmToolResult(string Id, string Content, bool IsError = false);
public record LlmTurn(string Text, IReadOnlyList<LlmToolCall> ToolCalls, string StopReason);

/// <summary>A stateful conversation with one model; agents drive it turn by turn.</summary>
public interface ILlmSession
{
    void AddUser(string text, IReadOnlyList<LlmImage>? images = null);
    Task<LlmTurn> NextAsync(CancellationToken ct);
    void AddToolResults(IReadOnlyList<LlmToolResult> results);
}

public record LlmOptions(string Model, string? System, IReadOnlyList<LlmTool> Tools, int MaxTokens = 16000, double? Temperature = null, bool JsonMode = false);

public static class LlmFactory
{
    public static ILlmSession Create(ProviderInfo p, LlmOptions o, HttpClient http) => p.Type switch
    {
        "openai" => new OpenAiChatSession(p, o, http),
        "anthropic" => new AnthropicChatSession(p, o),
        _ => throw new InvalidOperationException($"Provider type '{p.Type}' cannot run chat models"),
    };
}

/// <summary>Chat Completions API; works with OpenAI and OpenAI-compatible local servers (Ollama, LM Studio, vLLM).</summary>
public sealed class OpenAiChatSession : ILlmSession
{
    readonly ProviderInfo _p;
    readonly LlmOptions _o;
    readonly HttpClient _http;
    readonly JsonArray _messages = [];

    public OpenAiChatSession(ProviderInfo p, LlmOptions o, HttpClient http)
    {
        _p = p; _o = o; _http = http;
        if (!string.IsNullOrWhiteSpace(o.System))
            _messages.Add(new JsonObject { ["role"] = "system", ["content"] = o.System });
    }

    public void AddUser(string text, IReadOnlyList<LlmImage>? images = null)
    {
        if (images is not { Count: > 0 })
        {
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text });
            return;
        }
        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };
        foreach (var img in images)
            parts.Add(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = $"data:{img.MediaType};base64,{img.Base64}" },
            });
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
    }

    public void AddToolResults(IReadOnlyList<LlmToolResult> results)
    {
        foreach (var r in results)
            _messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.Id, ["content"] = r.IsError ? "ERROR: " + r.Content : r.Content });
    }

    public async Task<LlmTurn> NextAsync(CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["model"] = _o.Model,
            ["messages"] = _messages.DeepClone(),
        };
        if (_o.Temperature is { } t) body["temperature"] = t;
        if (_o.JsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        if (_o.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(_o.Tools.Select(tool => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema.DeepClone(),
                },
            }).ToArray());
        }

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_p.BaseUrl}/chat/completions") { Content = JsonContent.Create(body) };
        if (!string.IsNullOrEmpty(_p.ApiKey)) req.Headers.Authorization = new("Bearer", _p.ApiKey);
        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{_p.Name}: HTTP {(int)resp.StatusCode}: {Trim(raw)}");

        var json = JsonNode.Parse(raw)!;
        var choice = json["choices"]?[0] ?? throw new InvalidOperationException($"{_p.Name}: no choices in response");
        var message = choice["message"]!.AsObject();
        _messages.Add(message.DeepClone());

        var calls = new List<LlmToolCall>();
        if (message["tool_calls"] is JsonArray tcs)
        {
            foreach (var tc in tcs)
            {
                var fn = tc!["function"]!;
                var argsText = fn["arguments"]?.GetValue<string>() ?? "{}";
                JsonObject args;
                try { args = JsonNode.Parse(argsText) as JsonObject ?? new JsonObject(); }
                catch (JsonException) { args = new JsonObject { ["_raw"] = argsText }; }
                calls.Add(new LlmToolCall(tc["id"]?.GetValue<string>() ?? Guid.NewGuid().ToString("N"), fn["name"]!.GetValue<string>(), args));
            }
        }
        var text = message["content"] is JsonValue cv && cv.TryGetValue(out string? s) ? s : "";
        return new LlmTurn(text ?? "", calls, choice["finish_reason"]?.GetValue<string>() ?? "stop");
    }

    static string Trim(string s) => s.Length > 800 ? s[..800] + "…" : s;
}

/// <summary>Image generation/edit through the OpenAI Images API (gpt-image models, DALL·E, compatible servers).</summary>
public static class OpenAiImages
{
    public static async Task<List<byte[]>> GenerateAsync(ProviderInfo p, HttpClient http, string model, string prompt, string? size,
        int count, string? quality, IReadOnlyList<(byte[] Bytes, string Name, string Mime)> references, JsonObject extra, CancellationToken ct)
    {
        HttpRequestMessage req;
        if (references.Count == 0)
        {
            var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["n"] = count };
            if (!string.IsNullOrEmpty(size)) body["size"] = size;
            if (!string.IsNullOrEmpty(quality)) body["quality"] = quality;
            foreach (var (k, v) in extra) body[k] = v?.DeepClone();
            req = new HttpRequestMessage(HttpMethod.Post, $"{p.BaseUrl}/images/generations") { Content = JsonContent.Create(body) };
        }
        else
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(model), "model" },
                { new StringContent(prompt), "prompt" },
                { new StringContent(count.ToString()), "n" },
            };
            if (!string.IsNullOrEmpty(size)) form.Add(new StringContent(size), "size");
            if (!string.IsNullOrEmpty(quality)) form.Add(new StringContent(quality), "quality");
            foreach (var (k, v) in extra) form.Add(new StringContent(Json.AsText(v)), k);
            foreach (var r in references)
            {
                var file = new ByteArrayContent(r.Bytes);
                file.Headers.ContentType = new(r.Mime);
                form.Add(file, references.Count > 1 ? "image[]" : "image", r.Name);
            }
            req = new HttpRequestMessage(HttpMethod.Post, $"{p.BaseUrl}/images/edits") { Content = form };
        }
        req.Headers.Authorization = new("Bearer", p.RequireKey());
        using var resp = await http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"{p.Name} images: HTTP {(int)resp.StatusCode}: {raw[..Math.Min(raw.Length, 800)]}");

        var result = new List<byte[]>();
        foreach (var item in JsonNode.Parse(raw)!["data"]!.AsArray())
        {
            if (item?["b64_json"]?.GetValue<string>() is { } b64) result.Add(Convert.FromBase64String(b64));
            else if (item?["url"]?.GetValue<string>() is { } url) result.Add(await http.GetByteArrayAsync(url, ct));
        }
        return result;
    }
}
