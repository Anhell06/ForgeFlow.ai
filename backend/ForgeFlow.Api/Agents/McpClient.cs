using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ForgeFlow.Core;

namespace ForgeFlow.Agents;

public record McpToolInfo(string Name, string Description, JsonObject InputSchema);

public record McpContent(string Type, string? Text, string? Data, string? MimeType);

public record McpCallResult(IReadOnlyList<McpContent> Content, bool IsError);

/// <summary>
/// Minimal Model Context Protocol client (JSON-RPC 2.0) over stdio or Streamable HTTP.
/// Covers what agents need: initialize, tools/list, tools/call.
/// </summary>
public abstract class McpClient : IAsyncDisposable
{
    public const string ProtocolVersion = "2025-06-18";
    int _nextId;

    public string Name { get; protected init; } = "mcp";

    protected abstract Task<JsonNode?> RequestAsync(string method, JsonObject? @params, int id, CancellationToken ct);
    protected abstract Task NotifyAsync(string method, JsonObject? @params, CancellationToken ct);
    public abstract ValueTask DisposeAsync();

    protected int NextId() => Interlocked.Increment(ref _nextId);

    public async Task InitializeAsync(CancellationToken ct)
    {
        await RequestAsync("initialize", new JsonObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JsonObject(),
            ["clientInfo"] = new JsonObject { ["name"] = "ForgeFlow", ["version"] = "0.1.0" },
        }, NextId(), ct);
        await NotifyAsync("notifications/initialized", null, ct);
    }

    public async Task<List<McpToolInfo>> ListToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpToolInfo>();
        string? cursor = null;
        do
        {
            var p = cursor is null ? null : new JsonObject { ["cursor"] = cursor };
            var result = await RequestAsync("tools/list", p, NextId(), ct);
            foreach (var t in result?["tools"]?.AsArray() ?? [])
            {
                var schema = t!["inputSchema"] as JsonObject ?? new JsonObject { ["type"] = "object" };
                tools.Add(new McpToolInfo(t["name"]!.GetValue<string>(), t["description"]?.GetValue<string>() ?? "", (JsonObject)schema.DeepClone()));
            }
            cursor = result?["nextCursor"]?.GetValue<string>();
        } while (!string.IsNullOrEmpty(cursor));
        return tools;
    }

    public async Task<McpCallResult> CallToolAsync(string name, JsonObject args, CancellationToken ct)
    {
        var result = await RequestAsync("tools/call", new JsonObject { ["name"] = name, ["arguments"] = args.DeepClone() }, NextId(), ct);
        var content = new List<McpContent>();
        foreach (var c in result?["content"]?.AsArray() ?? [])
        {
            if (c is not JsonObject o) continue;
            var type = o.Str("type") ?? "text";
            content.Add(type switch
            {
                "text" => new McpContent("text", o.Str("text"), null, null),
                "image" or "audio" => new McpContent(type, null, o.Str("data"), o.Str("mimeType")),
                "resource" => new McpContent("text", o["resource"]?["text"]?.GetValue<string>() ?? o["resource"]?["uri"]?.GetValue<string>(), null, null),
                _ => new McpContent("text", o.ToJsonString(), null, null),
            });
        }
        if (content.Count == 0 && result?["structuredContent"] is { } sc)
            content.Add(new McpContent("text", sc.ToJsonString(), null, null));
        return new McpCallResult(content, result?["isError"]?.GetValue<bool>() ?? false);
    }

    protected static JsonNode? Unwrap(JsonNode msg, string method)
    {
        if (msg["error"] is JsonObject err)
            throw new InvalidOperationException($"MCP {method} failed: {err["message"]} ({err["code"]})");
        return msg["result"];
    }

    public static async Task<McpClient> ConnectAsync(Data.McpServerEntity cfg, Action<string> log, CancellationToken ct, HttpClient? http = null)
    {
        McpClient client = cfg.Transport == "http"
            ? new HttpMcpClient(cfg.Name, cfg.Url ?? throw new InvalidOperationException($"MCP server '{cfg.Name}' has no URL"),
                Json.ParseObject(cfg.Headers), http ?? new HttpClient())
            : StdioMcpClient.Start(cfg, log);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await client.InitializeAsync(timeout.Token);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }
}

public sealed class StdioMcpClient : McpClient
{
    readonly Process _proc;
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode>> _pending = new();
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly Action<string> _log;
    readonly Task _reader;

    StdioMcpClient(string name, Process proc, Action<string> log)
    {
        Name = name;
        _proc = proc;
        _log = log;
        _reader = Task.Run(ReadLoop);
        _ = Task.Run(async () =>
        {
            string? line;
            while ((line = await _proc.StandardError.ReadLineAsync()) is not null)
                if (!string.IsNullOrWhiteSpace(line)) _log($"[{Name}] {line}");
        });
    }

    public static StdioMcpClient Start(Data.McpServerEntity cfg, Action<string> log)
    {
        var args = Json.ParseArray(cfg.Args).Select(a => Json.AsText(a)).ToList();
        var command = cfg.Command ?? throw new InvalidOperationException($"MCP server '{cfg.Name}' has no command");
        if (CommandResolver.Resolve(command) is null)
            throw new InvalidOperationException(
                $"MCP server '{cfg.Name}': command '{command}' was not found on PATH. Install it (uvx comes with uv: https://docs.astral.sh/uv/) or put the full path in Command.");
        var psi = CommandResolver.Build(command, args);
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(cfg.Cwd)) psi.WorkingDirectory = cfg.Cwd;
        foreach (var (k, v) in Json.ParseObject(cfg.Env)) psi.Environment[k] = Providers.ProviderRegistry.ResolveSecret(Json.AsText(v));
        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start MCP server '{cfg.Name}'");
        return new StdioMcpClient(cfg.Name, proc, log);
    }

    async Task ReadLoop()
    {
        try
        {
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch { _log($"[{Name}] {line}"); continue; }
                if (msg is not JsonObject o) continue;

                if (o["method"] is not null && o["id"] is not null)
                {
                    // Server-initiated request (ping, roots/list, sampling...). Answer minimally so the server does not hang.
                    var method = o.Str("method");
                    var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = o["id"]!.DeepClone() };
                    if (method == "ping") reply["result"] = new JsonObject();
                    else if (method == "roots/list") reply["result"] = new JsonObject { ["roots"] = new JsonArray() };
                    else reply["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Not supported by ForgeFlow" };
                    await WriteAsync(reply, CancellationToken.None);
                }
                else if (o["id"] is JsonValue idv && idv.TryGetValue(out int id) && _pending.TryRemove(id, out var tcs))
                {
                    tcs.TrySetResult(o);
                }
            }
        }
        finally
        {
            foreach (var (_, tcs) in _pending) tcs.TrySetException(new InvalidOperationException($"MCP server '{Name}' exited"));
        }
    }

    async Task WriteAsync(JsonObject msg, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            await _proc.StandardInput.WriteLineAsync(msg.ToJsonString().AsMemory(), ct);
            await _proc.StandardInput.FlushAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    protected override async Task<JsonNode?> RequestAsync(string method, JsonObject? @params, int id, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) msg["params"] = @params;
        await WriteAsync(msg, ct);
        await using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
        return Unwrap(await tcs.Task, method);
    }

    protected override Task NotifyAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) msg["params"] = @params;
        return WriteAsync(msg, ct);
    }

    public override async ValueTask DisposeAsync()
    {
        try { _proc.StandardInput.Close(); } catch { /* already gone */ }
        try
        {
            if (!_proc.WaitForExit(2000)) _proc.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _proc.Dispose();
    }
}

/// <summary>Streamable HTTP transport: POST JSON-RPC, reply is JSON or an SSE stream.</summary>
public sealed class HttpMcpClient(string name, string url, JsonObject headers, HttpClient http) : McpClient
{
    string? _sessionId;

    HttpRequestMessage Build(JsonObject msg)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(msg.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        req.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
        if (_sessionId is not null) req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, Providers.ProviderRegistry.ResolveSecret(Json.AsText(v)));
        return req;
    }

    protected override async Task<JsonNode?> RequestAsync(string method, JsonObject? @params, int id, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (@params is not null) msg["params"] = @params;
        using var req = Build(msg);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var sid)) _sessionId = sid.First();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"MCP {name} {method}: HTTP {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync(ct)}");

        if (resp.Content.Headers.ContentType?.MediaType == "text/event-stream")
        {
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
            var data = new StringBuilder();
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) is not null)
            {
                if (line.StartsWith("data:")) data.Append(line[5..].TrimStart());
                else if (line.Length == 0 && data.Length > 0)
                {
                    var evt = JsonNode.Parse(data.ToString());
                    data.Clear();
                    if (evt?["id"] is JsonValue v && v.TryGetValue(out int rid) && rid == id) return Unwrap(evt, method);
                }
            }
            throw new InvalidOperationException($"MCP {name} {method}: stream ended without a response");
        }
        return Unwrap(JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!, method);
    }

    protected override async Task NotifyAsync(string method, JsonObject? @params, CancellationToken ct)
    {
        var msg = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (@params is not null) msg["params"] = @params;
        using var req = Build(msg);
        using var _ = await http.SendAsync(req, ct);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_sessionId is null) return;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Delete, url);
            req.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
            using var _ = await http.SendAsync(req);
        }
        catch { /* best effort */ }
    }
}
