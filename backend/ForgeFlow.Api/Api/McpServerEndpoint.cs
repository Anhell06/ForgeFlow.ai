using System.Text.Json.Nodes;
using ForgeFlow.Core;
using ForgeFlow.Data;
using ForgeFlow.Engine;
using ForgeFlow.Nodes;
using Microsoft.EntityFrameworkCore;

namespace ForgeFlow.Api;

/// <summary>
/// ForgeFlow itself as an MCP server (Streamable HTTP, JSON responses) so Claude Code, Cursor or Codex can
/// list and run pipelines: claude mcp add --transport http forgeflow http://localhost:5180/mcp
/// </summary>
public static class McpServerEndpoint
{
    public static void MapForgeMcp(this WebApplication app)
    {
        app.MapPost("/mcp", async (HttpContext http, IServiceProvider sp, CancellationToken ct) =>
        {
            var msg = await JsonNode.ParseAsync(http.Request.Body, cancellationToken: ct) as JsonObject;
            if (msg is null) return Results.BadRequest();
            var id = msg["id"]?.DeepClone();
            var method = msg.Str("method") ?? "";
            if (id is null) return Results.Accepted(); // notification

            try
            {
                JsonNode result = method switch
                {
                    "initialize" => new JsonObject
                    {
                        ["protocolVersion"] = msg["params"]?["protocolVersion"]?.GetValue<string>() ?? Agents.McpClient.ProtocolVersion,
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                        ["serverInfo"] = new JsonObject { ["name"] = "forgeflow", ["version"] = "0.1.0" },
                        ["instructions"] = "ForgeFlow runs node-based asset pipelines (prompt → image → video → 3D → engine). " +
                                           "Use list_pipelines, then run_pipeline with its inputs; results include local file paths.",
                    },
                    "ping" => new JsonObject(),
                    "tools/list" => new JsonObject { ["tools"] = Tools() },
                    "tools/call" => await CallAsync(msg["params"] as JsonObject ?? new JsonObject(), sp, ct),
                    _ => throw new MissingMethodException(method),
                };
                return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            }
            catch (MissingMethodException)
            {
                return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"Unknown method {method}" } });
            }
        });
        app.MapGet("/mcp", () => Results.StatusCode(405));
    }

    static JsonObject Schema(JsonObject props, params string[] required) => new()
    {
        ["type"] = "object",
        ["properties"] = props,
        ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
    };

    static JsonArray Tools() =>
    [
        new JsonObject
        {
            ["name"] = "list_pipelines", ["description"] = "List saved pipelines with their input names.",
            ["inputSchema"] = Schema(new JsonObject()),
        },
        new JsonObject
        {
            ["name"] = "run_pipeline",
            ["description"] = "Run a saved pipeline. Waits up to wait_seconds and returns status, results and file paths.",
            ["inputSchema"] = Schema(new JsonObject
            {
                ["pipeline"] = new JsonObject { ["type"] = "string", ["description"] = "Pipeline id or exact name" },
                ["inputs"] = new JsonObject { ["type"] = "object", ["description"] = "Values for the pipeline's inputs, e.g. {\"prompt\":\"...\"}" },
                ["wait_seconds"] = new JsonObject { ["type"] = "integer", ["description"] = "Default 600" },
            }, "pipeline"),
        },
        new JsonObject
        {
            ["name"] = "get_run", ["description"] = "Status, per-node state and results of a run.",
            ["inputSchema"] = Schema(new JsonObject { ["run_id"] = new JsonObject { ["type"] = "string" } }, "run_id"),
        },
        new JsonObject
        {
            ["name"] = "list_node_types", ["description"] = "List available node types (tools) with their ports and params.",
            ["inputSchema"] = Schema(new JsonObject()),
        },
        new JsonObject
        {
            ["name"] = "run_node",
            ["description"] = "Run a single node type as a tool (e.g. image.generate, model.generate3d).",
            ["inputSchema"] = Schema(new JsonObject
            {
                ["type"] = new JsonObject { ["type"] = "string" },
                ["inputs"] = new JsonObject { ["type"] = "object", ["description"] = "Port values. Files: {\"$asset\":\"ast_...\"}" },
                ["params"] = new JsonObject { ["type"] = "object" },
                ["wait_seconds"] = new JsonObject { ["type"] = "integer" },
            }, "type"),
        },
    ];

    static async Task<JsonNode> CallAsync(JsonObject p, IServiceProvider sp, CancellationToken ct)
    {
        var name = p.Str("name");
        var args = p["arguments"] as JsonObject ?? new JsonObject();
        var dbf = sp.GetRequiredService<IDbContextFactory<AppDb>>();
        var runs = sp.GetRequiredService<RunService>();
        try
        {
            JsonNode payload;
            switch (name)
            {
                case "list_pipelines":
                {
                    await using var db = await dbf.CreateDbContextAsync(ct);
                    payload = Json.ToNode((await db.Workflows.ToListAsync(ct)).Select(w => Endpoints.WorkflowView(w, false)))!;
                    break;
                }
                case "run_pipeline":
                {
                    var key = args.Str("pipeline") ?? "";
                    WorkflowEntity? w;
                    await using (var db = await dbf.CreateDbContextAsync(ct))
                        w = await db.Workflows.FirstOrDefaultAsync(x => x.Id == key || x.Name == key, ct);
                    if (w is null) return Error($"Pipeline '{key}' not found");
                    var run = await runs.CreateAsync(Json.De<WorkflowGraph>(w.Graph), args["inputs"] as JsonObject ?? new JsonObject(), w.Name, w.Id, ct: ct);
                    payload = await RunPayload(run.Id, args["wait_seconds"]?.GetValue<int>() ?? 600, runs, dbf, ct);
                    break;
                }
                case "get_run":
                    payload = await RunPayload(args.Str("run_id") ?? "", 0, runs, dbf, ct);
                    break;
                case "list_node_types":
                    payload = Json.ToNode(sp.GetRequiredService<NodeRegistry>().Specs)!;
                    break;
                case "run_node":
                {
                    var reg = sp.GetRequiredService<NodeRegistry>();
                    var type = args.Str("type") ?? "";
                    if (!reg.TryGet(type, out var node)) return Error($"Unknown node type {type}");
                    var graph = new WorkflowGraph();
                    graph.Nodes.Add(new NodeDef { Id = "node", Type = type, Params = args["params"] as JsonObject ?? new JsonObject() });
                    var reuse = new Dictionary<string, NodeState>();
                    var store = sp.GetRequiredService<Storage.AssetStore>();
                    foreach (var (port, value) in args["inputs"] as JsonObject ?? new JsonObject())
                    {
                        var v = value?.DeepClone();
                        if (v is JsonObject o && o.Str("$asset") is { } aid && o["path"] is null) v = (await store.GetAsync(aid, ct))?.ToJson();
                        graph.Nodes.Add(new NodeDef { Id = "in_" + port, Type = "output.result" });
                        graph.Edges.Add(new EdgeDef { Id = "e_" + port, Source = "in_" + port, SourcePort = "value", Target = "node", TargetPort = port });
                        reuse["in_" + port] = new NodeState { Status = "succeeded", Outputs = new JsonObject { ["value"] = v }, Cached = true };
                    }
                    var run = await runs.CreateAsync(graph, new JsonObject(), $"{node.Spec.Title} (MCP)", null, reuse, ct: ct);
                    payload = await RunPayload(run.Id, args["wait_seconds"]?.GetValue<int>() ?? 600, runs, dbf, ct);
                    break;
                }
                default:
                    return Error($"Unknown tool {name}");
            }
            return new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = payload.ToJsonString(Json.Pretty) }) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error(ex.Message);
        }
    }

    static async Task<JsonNode> RunPayload(string runId, int waitSeconds, RunService runs, IDbContextFactory<AppDb> dbf, CancellationToken ct)
    {
        if (waitSeconds > 0) await runs.WaitAsync(runId, TimeSpan.FromSeconds(Math.Min(waitSeconds, 3600)), ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var r = await db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == runId, ct) ?? throw new KeyNotFoundException("Run not found");
        var states = Json.De<Dictionary<string, NodeState>>(r.NodeStates);
        return new JsonObject
        {
            ["runId"] = r.Id, ["status"] = r.Status, ["error"] = r.Error,
            ["results"] = Json.Parse(r.Outputs),
            ["nodes"] = new JsonObject(states.Select(kv => KeyValuePair.Create(kv.Key,
                (JsonNode?)new JsonObject { ["status"] = kv.Value.Status, ["error"] = kv.Value.Error, ["message"] = kv.Value.Message }))),
        };
    }

    static JsonObject Error(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true,
    };
}
