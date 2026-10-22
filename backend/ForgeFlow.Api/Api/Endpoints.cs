using System.Text.Json.Nodes;
using ForgeFlow.Agents;
using ForgeFlow.Core;
using ForgeFlow.Data;
using ForgeFlow.Engine;
using ForgeFlow.Nodes;
using ForgeFlow.Providers;
using ForgeFlow.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ForgeFlow.Api;

public record RunRequest(JsonObject? Inputs, bool Wait = false, int WaitSeconds = 300);
public record AdhocRunRequest(WorkflowGraph Graph, JsonObject? Inputs, string? Name, string? WorkflowId);
public record NodeRunRequest(JsonObject? Inputs, JsonObject? Params, bool Wait = false, int WaitSeconds = 300);
public record RerunRequest(string? FromNodeId, JsonObject? Inputs, WorkflowGraph? Graph);
public record WorkflowDto(string? Name, string? Description, WorkflowGraph? Graph);

public static class Endpoints
{
    public static void MapForgeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/health", () => Results.Ok(new { ok = true, version = "0.1.0" }));

        // ---- nodes ----
        api.MapGet("/nodes", (NodeRegistry reg) => reg.Specs);
        api.MapPost("/nodes/{type}/run", async (string type, NodeRunRequest req, NodeRegistry reg, RunService runs, IDbContextFactory<AppDb> dbf,
            AssetStore store, CancellationToken ct) =>
        {
            if (!reg.TryGet(type, out var node)) return Results.NotFound(new { error = $"Unknown node type {type}" });
            // Tool mode: wrap one node in a tiny pipeline; each input becomes a "constant" upstream value.
            var graph = new WorkflowGraph();
            var target = new NodeDef { Id = "node", Type = type, Title = node.Spec.Title, Params = req.Params ?? new JsonObject() };
            graph.Nodes.Add(target);
            var reuse = new Dictionary<string, NodeState>();
            foreach (var (name, value) in req.Inputs ?? new JsonObject())
            {
                var id = "in_" + name;
                graph.Nodes.Add(new NodeDef { Id = id, Type = "output.result", Title = name });
                graph.Edges.Add(new EdgeDef { Id = "e_" + name, Source = id, SourcePort = "value", Target = "node", TargetPort = name });
                reuse[id] = new NodeState { Status = "succeeded", Outputs = new JsonObject { ["value"] = await ResolveAssetRefs(value, store, ct) }, Cached = true };
            }
            var run = await runs.CreateAsync(graph, new JsonObject(), $"{node.Spec.Title} (API)", null, reuse, ct: ct);
            return await RunResult(run.Id, req.Wait, req.WaitSeconds, runs, dbf, ct);
        });

        api.MapGet("/custom-nodes", (NodeRegistry reg) =>
            Directory.EnumerateFiles(reg.CustomDir, "*.json").Select(f => Json.ParseObject(File.ReadAllText(f))));
        api.MapPut("/custom-nodes", (JsonObject def, NodeRegistry reg) =>
        {
            try { reg.SaveCustom(def); return Results.Ok(reg.Specs.Where(s => s.Custom)); }
            catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        api.MapDelete("/custom-nodes/{type}", (string type, NodeRegistry reg) => reg.DeleteCustom(type) ? Results.NoContent() : Results.NotFound());

        // ---- providers ----
        api.MapGet("/provider-types", () => ProviderTypes.All);
        api.MapGet("/providers", async (IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return (await db.Providers.OrderBy(p => p.CreatedAt).ToListAsync(ct)).Select(ProviderView);
        });
        api.MapPost("/providers", async (ProviderEntity p, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            p.Id = Ids.New("prv");
            await using var db = await dbf.CreateDbContextAsync(ct);
            db.Providers.Add(p);
            await db.SaveChangesAsync(ct);
            return ProviderView(p);
        });
        api.MapPut("/providers/{id}", async (string id, ProviderEntity p, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var e = await db.Providers.FindAsync([id], ct);
            if (e is null) return Results.NotFound();
            e.Name = p.Name; e.Type = p.Type; e.BaseUrl = p.BaseUrl; e.DefaultModel = p.DefaultModel; e.Extra = p.Extra;
            // The UI sends back a masked key; only overwrite when a new value was typed.
            if (p.ApiKey is not null && !p.ApiKey.StartsWith("••")) e.ApiKey = p.ApiKey;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ProviderView(e));
        });
        api.MapDelete("/providers/{id}", async (string id, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var n = await db.Providers.Where(p => p.Id == id).ExecuteDeleteAsync(ct);
            return n > 0 ? Results.NoContent() : Results.NotFound();
        });

        // ---- MCP servers ----
        api.MapGet("/mcp-servers", async (IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return (await db.McpServers.ToListAsync(ct)).Select(McpView);
        });
        api.MapPost("/mcp-servers", async (JsonObject body, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            var e = new McpServerEntity();
            ApplyMcp(e, body);
            await using var db = await dbf.CreateDbContextAsync(ct);
            db.McpServers.Add(e);
            await db.SaveChangesAsync(ct);
            return McpView(e);
        });
        api.MapPut("/mcp-servers/{id}", async (string id, JsonObject body, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var e = await db.McpServers.FindAsync([id], ct);
            if (e is null) return Results.NotFound();
            ApplyMcp(e, body);
            await db.SaveChangesAsync(ct);
            return Results.Ok(McpView(e));
        });
        api.MapDelete("/mcp-servers/{id}", async (string id, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return await db.McpServers.Where(s => s.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
        });
        api.MapPost("/mcp-servers/{id}/test", async (string id, IDbContextFactory<AppDb> dbf, IHttpClientFactory hf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var e = await db.McpServers.FindAsync([id], ct);
            if (e is null) return Results.NotFound();
            var logs = new List<string>();
            try
            {
                await using var client = await McpClient.ConnectAsync(e, l => { lock (logs) logs.Add(l); }, ct, hf.CreateClient("nodes"));
                var tools = await client.ListToolsAsync(ct);
                return Results.Ok(new { ok = true, tools = tools.Select(t => new { t.Name, t.Description }), logs });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, error = ex.Message, logs });
            }
        });

        // ---- workflows & templates ----
        api.MapGet("/workflows", async (IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return (await db.Workflows.OrderByDescending(w => w.UpdatedAt).ToListAsync(ct)).Select(w => WorkflowView(w, false));
        });
        api.MapGet("/workflows/{id}", async (string id, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var w = await db.Workflows.FindAsync([id], ct);
            return w is null ? Results.NotFound() : Results.Ok(WorkflowView(w, true));
        });
        api.MapPost("/workflows", async (WorkflowDto dto, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            var w = new WorkflowEntity { Name = dto.Name ?? "Untitled pipeline", Description = dto.Description ?? "", Graph = Json.Ser(dto.Graph ?? new WorkflowGraph()) };
            await using var db = await dbf.CreateDbContextAsync(ct);
            db.Workflows.Add(w);
            await db.SaveChangesAsync(ct);
            return WorkflowView(w, true);
        });
        api.MapPut("/workflows/{id}", async (string id, WorkflowDto dto, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var w = await db.Workflows.FindAsync([id], ct);
            if (w is null) return Results.NotFound();
            if (dto.Name is not null) w.Name = dto.Name;
            if (dto.Description is not null) w.Description = dto.Description;
            if (dto.Graph is not null)
            {
                try { dto.Graph.TopologicalOrder(); }
                catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
                w.Graph = Json.Ser(dto.Graph);
            }
            w.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(WorkflowView(w, true));
        });
        api.MapDelete("/workflows/{id}", async (string id, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return await db.Workflows.Where(w => w.Id == id).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound();
        });
        api.MapPost("/workflows/{id}/duplicate", async (string id, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var w = await db.Workflows.FindAsync([id], ct);
            if (w is null) return Results.NotFound();
            var copy = new WorkflowEntity { Name = w.Name + " (copy)", Description = w.Description, Graph = w.Graph, TemplateId = w.TemplateId };
            db.Workflows.Add(copy);
            await db.SaveChangesAsync(ct);
            return Results.Ok(WorkflowView(copy, true));
        });
        api.MapGet("/workflows/{id}/inputs", async (string id, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var w = await db.Workflows.FindAsync([id], ct);
            return w is null ? Results.NotFound() : Results.Ok(WorkflowInputs(Json.De<WorkflowGraph>(w.Graph)));
        });
        api.MapPost("/workflows/{id}/run", async (string id, RunRequest? req, RunService runs, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            WorkflowEntity? w;
            await using (var db = await dbf.CreateDbContextAsync(ct)) w = await db.Workflows.FindAsync([id], ct);
            if (w is null) return Results.NotFound();
            var run = await runs.CreateAsync(Json.De<WorkflowGraph>(w.Graph), req?.Inputs ?? new JsonObject(), w.Name, w.Id, ct: ct);
            return await RunResult(run.Id, req?.Wait ?? false, req?.WaitSeconds ?? 300, runs, dbf, ct);
        });

        api.MapGet("/templates", (TemplateLibrary lib) => lib.All.Select(t => new { t.Id, t.Name, t.Description, t.Tags, nodeCount = t.Graph.Nodes.Count }));
        api.MapGet("/templates/{id}", (string id, TemplateLibrary lib) => lib.Get(id) is { } t ? Results.Ok(t) : Results.NotFound());
        api.MapPost("/templates/{id}/instantiate", async (string id, WorkflowDto? dto, TemplateLibrary lib, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            if (lib.Get(id) is not { } t) return Results.NotFound();
            // The UI may pass a localized name/description for the new pipeline.
            var w = new WorkflowEntity
            {
                Name = string.IsNullOrWhiteSpace(dto?.Name) ? t.Name : dto.Name,
                Description = string.IsNullOrWhiteSpace(dto?.Description) ? t.Description : dto.Description,
                Graph = Json.Ser(t.Graph), TemplateId = t.Id,
            };
            await using var db = await dbf.CreateDbContextAsync(ct);
            db.Workflows.Add(w);
            await db.SaveChangesAsync(ct);
            return Results.Ok(WorkflowView(w, true));
        });

        // ---- runs ----
        api.MapPost("/runs", async (AdhocRunRequest req, RunService runs, CancellationToken ct) =>
        {
            try
            {
                var run = await runs.CreateAsync(req.Graph, req.Inputs ?? new JsonObject(), req.Name ?? "Ad-hoc run", req.WorkflowId, ct: ct);
                return Results.Ok(RunView(run, true));
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        api.MapGet("/runs", async (string? workflowId, int? limit, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var q = db.Runs.AsNoTracking().AsQueryable();
            if (!string.IsNullOrEmpty(workflowId)) q = q.Where(r => r.WorkflowId == workflowId);
            var list = await q.OrderByDescending(r => r.CreatedAt).Take(Math.Clamp(limit ?? 50, 1, 500)).ToListAsync(ct);
            return list.Select(r => RunView(r, false));
        });
        api.MapGet("/runs/{id}", async (string id, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var r = await db.Runs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
            return r is null ? Results.NotFound() : Results.Ok(RunView(r, true));
        });
        api.MapPost("/runs/{id}/cancel", async (string id, RunService runs) =>
            await runs.CancelAsync(id) ? Results.Ok(new { canceled = true }) : Results.Conflict(new { error = "Run is not active" }));
        api.MapPost("/runs/{id}/rerun", async (string id, RerunRequest? req, RunService runs, CancellationToken ct) =>
        {
            try { return Results.Ok(RunView(await runs.RerunAsync(id, req?.FromNodeId, req?.Inputs, ct, req?.Graph), true)); }
            catch (KeyNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });
        api.MapDelete("/runs/{id}", async (string id, IDbContextFactory<AppDb> dbf, CancellationToken ct) =>
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            return await db.Runs.Where(r => r.Id == id && r.Status != "running" && r.Status != "queued").ExecuteDeleteAsync(ct) > 0
                ? Results.NoContent() : Results.Conflict(new { error = "Run is active or missing" });
        });
        api.MapGet("/runs/{id}/events", async (string id, HttpContext http, RunEvents events, CancellationToken ct) =>
            await StreamEvents(http, events, id, ct));
        api.MapGet("/events", async (HttpContext http, RunEvents events, CancellationToken ct) => await StreamEvents(http, events, "*", ct));

        // ---- assets ----
        api.MapGet("/assets", async (string? kind, string? runId, string? q, int? limit, AssetStore store, CancellationToken ct) =>
            (await store.ListAsync(kind, runId, q, limit ?? 200, ct)).Select(a => a.ToJson()));
        api.MapGet("/assets/{id}", async (string id, AssetStore store, CancellationToken ct) =>
            await store.GetAsync(id, ct) is { } a ? Results.Ok(a.ToJson()) : Results.NotFound());
        api.MapPost("/assets", async (IFormFileCollection files, AssetStore store, CancellationToken ct) =>
        {
            var saved = new List<JsonObject>();
            foreach (var f in files)
            {
                await using var s = f.OpenReadStream();
                saved.Add((await store.SaveStreamAsync(s, f.FileName, f.ContentType, new AssetMeta(Source: "upload"), ct)).ToJson());
            }
            return saved;
        }).DisableAntiforgery();
        api.MapPost("/assets/import", async ([FromBody] JsonObject body, AssetStore store, CancellationToken ct) =>
        {
            var url = body.Str("url") ?? "";
            if (url.StartsWith("http") || url.StartsWith("data:"))
                return Results.Ok((await store.DownloadAsync(url, body.Str("name"), new AssetMeta(Source: "import"), ct)).ToJson());
            if (File.Exists(url))
                return Results.Ok((await store.SaveFileAsync(url, new AssetMeta(Source: "import"), ct)).ToJson());
            return Results.BadRequest(new { error = "Give an http(s) URL, a data: URI or an existing local path" });
        });
        api.MapDelete("/assets/{id}", async (string id, AssetStore store, CancellationToken ct) =>
            await store.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
    }

    // ---- helpers ----

    /// <summary>API callers may pass {"$asset":"ast_..."} with only the id, or a list of those; fill in path/url.</summary>
    static async Task<JsonNode?> ResolveAssetRefs(JsonNode? value, AssetStore store, CancellationToken ct)
    {
        switch (value)
        {
            case JsonObject o when o.Str("$asset") is { } id && o["path"] is null:
                return (await store.GetAsync(id, ct))?.ToJson() ?? throw new KeyNotFoundException($"Asset {id} not found");
            case JsonArray a:
                var list = new JsonArray();
                foreach (var item in a) list.Add(await ResolveAssetRefs(item, store, ct));
                return list;
            default:
                return value?.DeepClone();
        }
    }

    static async Task<IResult> RunResult(string runId, bool wait, int waitSeconds, RunService runs, IDbContextFactory<AppDb> dbf, CancellationToken ct)
    {
        if (wait) await runs.WaitAsync(runId, TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 3600)), ct);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var r = await db.Runs.AsNoTracking().FirstAsync(x => x.Id == runId, ct);
        return Results.Ok(RunView(r, true));
    }

    static async Task StreamEvents(HttpContext http, RunEvents events, string key, CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        using var sub = events.Subscribe(key);
        await http.Response.WriteAsync(": connected\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(ct);
                heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                try
                {
                    var evt = await sub.Reader.ReadAsync(heartbeat.Token);
                    await http.Response.WriteAsync($"data: {evt.ToJsonString()}\n\n", ct);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                }
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { /* client went away */ }
    }

    public static object ProviderView(ProviderEntity p) => new
    {
        p.Id, p.Name, p.Type, p.BaseUrl, p.DefaultModel,
        apiKey = string.IsNullOrEmpty(p.ApiKey) ? null : p.ApiKey.StartsWith("env:") ? p.ApiKey : "••••" + p.ApiKey[^Math.Min(4, p.ApiKey.Length)..],
        keyAvailable = !string.IsNullOrEmpty(ProviderRegistry.ResolveSecret(p.ApiKey)),
        extra = Json.ParseObject(p.Extra),
    };

    static object McpView(McpServerEntity s) => new
    {
        s.Id, s.Name, s.Transport, s.Command, args = Json.ParseArray(s.Args), env = Json.ParseObject(s.Env), s.Cwd, s.Url,
        headers = Json.ParseObject(s.Headers), s.Enabled,
    };

    static void ApplyMcp(McpServerEntity e, JsonObject body)
    {
        e.Name = body.Str("name") ?? e.Name;
        e.Transport = body.Str("transport") ?? e.Transport;
        e.Command = body.Str("command") ?? e.Command;
        e.Cwd = body.Str("cwd") ?? e.Cwd;
        e.Url = body.Str("url") ?? e.Url;
        if (body["args"] is JsonArray a) e.Args = a.ToJsonString();
        if (body["env"] is JsonObject env) e.Env = env.ToJsonString();
        if (body["headers"] is JsonObject h) e.Headers = h.ToJsonString();
        if (body["enabled"] is JsonValue en && en.TryGetValue(out bool b)) e.Enabled = b;
    }

    public static object WorkflowView(WorkflowEntity w, bool withGraph)
    {
        var graph = Json.De<WorkflowGraph>(w.Graph);
        return new
        {
            w.Id, w.Name, w.Description, w.TemplateId, w.CreatedAt, w.UpdatedAt,
            nodeCount = graph.Nodes.Count,
            inputs = WorkflowInputs(graph),
            graph = withGraph ? graph : null,
        };
    }

    public static List<object> WorkflowInputs(WorkflowGraph g) => g.Nodes
        .Where(n => n.Type is "input.text" or "input.asset")
        .Select(n => (object)new
        {
            name = n.Params.Str("name") ?? (n.Type == "input.text" ? "prompt" : "image"),
            type = n.Type == "input.text" ? "text" : "file",
            title = n.Title,
            @default = n.Type == "input.text" ? n.Params["value"]?.DeepClone() : n.Params["asset"]?.DeepClone(),
        }).ToList();

    public static object RunView(RunEntity r, bool full) => new
    {
        r.Id, r.WorkflowId, r.Name, r.Status, r.Error, r.ParentRunId, r.CreatedAt, r.StartedAt, r.FinishedAt,
        inputs = Json.ParseObject(r.Inputs),
        outputs = Json.ParseObject(r.Outputs),
        nodes = full ? Json.Parse(r.NodeStates) : null,
        graph = full ? Json.Parse(r.Graph) : null,
    };
}
