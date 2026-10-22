using System.Text.Json.Nodes;
using ForgeFlow.Core;
using ForgeFlow.Data;
using ForgeFlow.Nodes;
using ForgeFlow.Providers;
using ForgeFlow.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ForgeFlow.Engine;

/// <summary>
/// Executes one run: schedules nodes as soon as their upstream nodes finish, fans a node out when a
/// single-value input receives a list, retries failures and streams progress events.
/// </summary>
public class WorkflowEngine(
    IDbContextFactory<AppDb> dbf,
    NodeRegistry registry,
    RunEvents events,
    AssetStore assets,
    ProviderRegistry providers,
    IHttpClientFactory httpFactory,
    IServiceProvider services,
    NodeCacheService cache,
    IOptions<ForgeOptions> opt,
    ILogger<WorkflowEngine> log)
{
    const int MaxLogLines = 300;

    sealed class RunCtx
    {
        public required RunEntity Run;
        public required WorkflowGraph Graph;
        public required Dictionary<string, NodeState> States;
        public required JsonObject Inputs;
        /// <summary>Nodes that must execute even when a cached result exists.</summary>
        public required HashSet<string> Force;
        public readonly object Lock = new();
    }

    public async Task<string> ExecuteAsync(string runId, CancellationToken ct)
    {
        RunEntity run;
        await using (var db = await dbf.CreateDbContextAsync(ct))
            run = await db.Runs.FindAsync([runId], ct) ?? throw new KeyNotFoundException(runId);
        if (run.Status != "queued") return run.Status;

        var rc = new RunCtx
        {
            Run = run,
            Graph = Json.De<WorkflowGraph>(run.Graph),
            States = Json.De<Dictionary<string, NodeState>>(run.NodeStates),
            Inputs = Json.ParseObject(run.Inputs),
            Force = Json.ParseArray(run.ForceNodes).Select(x => Json.AsText(x)).ToHashSet(),
        };
        foreach (var n in rc.Graph.Nodes) rc.States.TryAdd(n.Id, new NodeState());
        // Defaults of input nodes are visible as {{workflow.name}} everywhere, not only when passed explicitly.
        foreach (var n in rc.Graph.Nodes.Where(n => n.Type == "input.text"))
            if (n.Params.Str("name") is { Length: > 0 } inputName && !rc.Inputs.ContainsKey(inputName))
                rc.Inputs[inputName] = n.Params["value"]?.DeepClone();

        run.Status = "running";
        run.StartedAt = DateTime.UtcNow;
        await SaveAsync(rc, CancellationToken.None);
        events.Publish(run.Id, new JsonObject { ["type"] = "run.status", ["status"] = "running" });

        try
        {
            await ScheduleAsync(rc, ct);
            var states = rc.States.Values;
            run.Status = ct.IsCancellationRequested ? "canceled"
                : states.Any(s => s.Status == "failed") ? "failed"
                : "succeeded";
            run.Error = rc.States.Where(kv => kv.Value.Status == "failed")
                .Select(kv => $"{Title(rc, kv.Key)}: {kv.Value.Error}").FirstOrDefault();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Status = "failed";
            run.Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            run.Status = "canceled";
        }

        if (run.Status == "canceled")
            foreach (var s in rc.States.Values.Where(s => s.Status is "pending" or "running"))
                s.Status = "canceled";

        run.Outputs = CollectOutputs(rc).ToJsonString();
        run.FinishedAt = DateTime.UtcNow;
        await SaveAsync(rc, CancellationToken.None);
        events.Publish(run.Id, new JsonObject { ["type"] = "run.status", ["status"] = run.Status, ["error"] = run.Error });
        log.LogInformation("Run {RunId} finished: {Status}", run.Id, run.Status);
        return run.Status;
    }

    async Task ScheduleAsync(RunCtx rc, CancellationToken ct)
    {
        rc.Graph.TopologicalOrder();
        foreach (var n in rc.Graph.Nodes)
            if (!registry.TryGet(n.Type, out _))
                throw new InvalidOperationException($"Unknown node type '{n.Type}' ({n.Title ?? n.Id})");

        var running = new Dictionary<string, Task>();
        using var gate = new SemaphoreSlim(Math.Max(1, opt.Value.MaxParallelNodes));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var node in rc.Graph.Nodes)
            {
                var st = rc.States[node.Id];
                if (st.Status != "pending" || running.ContainsKey(node.Id)) continue;
                var ups = rc.Graph.Upstream(node.Id).Select(u => rc.States[u].Status).ToList();
                if (ups.Any(s => s is "failed" or "skipped" or "canceled"))
                {
                    st.Status = "skipped";
                    st.Message = "Upstream node did not succeed";
                    Publish(rc, node.Id, st);
                    continue;
                }
                if (ups.All(s => s == "succeeded"))
                    running[node.Id] = RunNodeAsync(rc, node, gate, ct);
            }
            if (running.Count == 0) break;
            var finished = await Task.WhenAny(running.Values);
            var id = running.First(kv => kv.Value == finished).Key;
            running.Remove(id);
            await SaveAsync(rc, CancellationToken.None);
        }
    }

    async Task RunNodeAsync(RunCtx rc, NodeDef def, SemaphoreSlim gate, CancellationToken ct)
    {
        var st = rc.States[def.Id];
        if (def.Disabled)
        {
            // Disabled nodes pass their first input straight through so a pipeline can be bypassed in place.
            st.Status = "succeeded";
            st.Message = "Disabled (pass-through)";
            st.Outputs = new JsonObject();
            var first = rc.Graph.IncomingEdges(def.Id).FirstOrDefault();
            if (first is not null && registry.TryGet(def.Type, out var t) && t.Spec.Outputs.Length > 0)
                st.Outputs[t.Spec.Outputs[0].Name] = rc.States[first.Source].Outputs[first.SourcePort]?.DeepClone();
            Publish(rc, def.Id, st);
            return;
        }

        await gate.WaitAsync(ct);
        try
        {
            registry.TryGet(def.Type, out var type);
            st.Status = "running";
            st.StartedAt = DateTime.UtcNow;
            st.Error = null;
            st.Cached = false;
            Publish(rc, def.Id, st);

            var inputs = GatherInputs(rc, def, type!.Spec, out var listSourced);

            // Same settings and same input values as an earlier run of this pipeline → reuse that result.
            string? cacheKey = null;
            if (rc.Run.WorkflowId is { } workflowId && NodeCacheService.IsCacheable(def))
            {
                cacheKey = NodeCacheService.Fingerprint(def, inputs, rc.Inputs);
                if (!rc.Force.Contains(def.Id) && await cache.GetAsync(workflowId, def.Id, cacheKey, ct) is { } hit)
                {
                    st.Outputs = hit;
                    st.Status = "succeeded";
                    st.Cached = true;
                    st.Progress = 1;
                    st.Message = null;
                    AddLog(rc, def.Id, "Reused cached result (same settings and inputs)");
                    return;
                }
            }

            // Single-value ports fan out over a list. "any" ports only do so when the list really is a list output,
            // so a JSON array meant as one value is not split by accident; "json" ports never do.
            var mapPorts = type.Spec.Inputs
                .Where(p => !p.List && p.Kind != Kinds.Json && inputs.GetValueOrDefault(p.Name) is JsonArray
                            && (p.Kind != Kinds.Any || listSourced.Contains(p.Name)))
                .Select(p => p.Name).ToList();

            JsonObject outputs;
            if (mapPorts.Count == 0)
            {
                outputs = await ExecuteWithRetriesAsync(rc, def, type, inputs, null, ct);
            }
            else
            {
                outputs = await ExecuteMappedAsync(rc, def, type, inputs, mapPorts, ct);
            }
            st.Outputs = outputs;
            st.Status = "succeeded";
            st.Progress = 1;
            if (cacheKey is not null) await cache.StoreAsync(rc.Run.WorkflowId!, def.Id, cacheKey, outputs, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            st.Status = "canceled";
        }
        catch (Exception ex)
        {
            st.Status = "failed";
            st.Error = ex is NodeException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            AddLog(rc, def.Id, "ERROR " + st.Error);
            log.LogWarning(ex, "Node {Node} failed in run {Run}", def.Id, rc.Run.Id);
        }
        finally
        {
            st.FinishedAt = DateTime.UtcNow;
            gate.Release();
            Publish(rc, def.Id, st);
        }
    }

    async Task<JsonObject> ExecuteMappedAsync(RunCtx rc, NodeDef def, INodeType type, Dictionary<string, JsonNode?> inputs,
        List<string> mapPorts, CancellationToken ct)
    {
        var lead = (JsonArray)inputs[mapPorts[0]]!;
        var count = lead.Count;
        var st = rc.States[def.Id];
        st.Items = count;
        st.ItemsDone = 0;
        AddLog(rc, def.Id, $"Fan-out over '{mapPorts[0]}': {count} item(s)");

        var results = new JsonObject[count];
        using var itemGate = new SemaphoreSlim(Math.Max(1, def.MapConcurrency));
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await itemGate.WaitAsync(ct);
            try
            {
                var itemInputs = new Dictionary<string, JsonNode?>(inputs.Select(kv => KeyValuePair.Create(kv.Key, kv.Value?.DeepClone())));
                foreach (var port in mapPorts)
                {
                    var arr = (JsonArray)inputs[port]!;
                    // Zip lists of equal length; shorter lists repeat their last element.
                    itemInputs[port] = arr.Count == 0 ? null : arr[Math.Min(i, arr.Count - 1)]?.DeepClone();
                }
                results[i] = await ExecuteWithRetriesAsync(rc, def, type, itemInputs, i, ct);
                lock (rc.Lock) st.ItemsDone++;
                st.Progress = (double)st.ItemsDone / count;
                Publish(rc, def.Id, st);
            }
            finally { itemGate.Release(); }
        });
        await Task.WhenAll(tasks);

        var merged = new JsonObject();
        foreach (var port in type.Spec.Outputs)
            merged[port.Name] = new JsonArray(results.Select(r => r[port.Name]?.DeepClone()).ToArray());
        return merged;
    }

    async Task<JsonObject> ExecuteWithRetriesAsync(RunCtx rc, NodeDef def, INodeType type, Dictionary<string, JsonNode?> inputs, int? item,
        CancellationToken ct)
    {
        var attempts = Math.Max(0, def.Retries) + 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var ctx = new NodeContext
                {
                    RunId = rc.Run.Id,
                    Def = def,
                    Spec = type.Spec,
                    Inputs = inputs,
                    WorkflowInputs = rc.Inputs,
                    WorkDir = item is null ? assets.RunDir(rc.Run.Id, def.Id) : assets.RunDir(rc.Run.Id, def.Id, item.Value.ToString()),
                    Services = services,
                    Assets = assets,
                    Providers = providers,
                    Http = httpFactory.CreateClient("nodes"),
                    Ct = ct,
                    ItemIndex = item,
                    Log = msg => AddLog(rc, def.Id, item is null ? msg : $"[{item}] {msg}"),
                    Progress = (p, m) =>
                    {
                        var st = rc.States[def.Id];
                        if (item is null && p is not null) st.Progress = p;
                        if (m is not null) st.Message = m;
                        Publish(rc, def.Id, st, logs: false);
                    },
                };
                var result = await type.ExecuteAsync(ctx);
                var outputs = new JsonObject();
                foreach (var (k, v) in result) outputs[k] = v;
                return outputs;
            }
            catch (Exception ex) when (attempt < attempts && ex is not OperationCanceledException)
            {
                AddLog(rc, def.Id, $"Attempt {attempt} failed: {ex.Message}. Retrying…");
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), ct);
            }
        }
    }

    /// <summary>Collects port values from upstream outputs; list ports concatenate, other ports take the last edge.</summary>
    Dictionary<string, JsonNode?> GatherInputs(RunCtx rc, NodeDef def, NodeSpec spec, out HashSet<string> listSourced)
    {
        var result = new Dictionary<string, JsonNode?>();
        listSourced = [];
        foreach (var port in spec.Inputs)
        {
            var edges = rc.Graph.IncomingEdges(def.Id).Where(e => e.TargetPort == port.Name).ToList();
            if (edges.Count == 0) continue;
            if (edges.Any(e => rc.States[e.Source].Items is not null ||
                               registry.TryGet(rc.Graph.Node(e.Source)!.Type, out var src) && src.Spec.Outputs.Any(o => o.Name == e.SourcePort && o.List)))
                listSourced.Add(port.Name);
            var values = edges.Select(e => rc.States[e.Source].Outputs[e.SourcePort]?.DeepClone()).ToList();
            if (port.List)
            {
                var arr = new JsonArray();
                foreach (var v in values)
                {
                    if (v is JsonArray inner) foreach (var x in inner) arr.Add(x?.DeepClone());
                    else if (v is not null) arr.Add(v);
                }
                result[port.Name] = arr;
            }
            else
            {
                result[port.Name] = values.LastOrDefault(v => v is not null);
            }
        }
        return result;
    }

    JsonObject CollectOutputs(RunCtx rc)
    {
        var outputs = new JsonObject();
        var resultNodes = rc.Graph.Nodes.Where(n => n.Type is "output.result" or "output.preview").ToList();
        if (resultNodes.Count > 0)
        {
            foreach (var n in resultNodes)
            {
                var name = n.Params.Str("name") is { Length: > 0 } s ? s : n.Title ?? n.Id;
                outputs[name] = rc.States[n.Id].Outputs["value"]?.DeepClone();
            }
            return outputs;
        }
        // Without explicit result nodes, report the outputs of sink nodes.
        foreach (var n in rc.Graph.Nodes.Where(n => rc.Graph.Edges.All(e => e.Source != n.Id)))
            outputs[n.Title ?? n.Id] = rc.States[n.Id].Outputs.DeepClone();
        return outputs;
    }

    void AddLog(RunCtx rc, string nodeId, string line)
    {
        var st = rc.States[nodeId];
        var stamped = $"{DateTime.UtcNow:HH:mm:ss} {line}";
        lock (rc.Lock)
        {
            st.Logs.Add(stamped);
            if (st.Logs.Count > MaxLogLines) st.Logs.RemoveRange(0, st.Logs.Count - MaxLogLines);
        }
        events.Publish(rc.Run.Id, new JsonObject { ["type"] = "node.log", ["nodeId"] = nodeId, ["line"] = stamped });
    }

    void Publish(RunCtx rc, string nodeId, NodeState st, bool logs = false)
    {
        var evt = new JsonObject
        {
            ["type"] = "node.status",
            ["nodeId"] = nodeId,
            ["status"] = st.Status,
            ["progress"] = st.Progress,
            ["message"] = st.Message,
            ["error"] = st.Error,
            ["items"] = st.Items,
            ["itemsDone"] = st.ItemsDone,
        };
        if (st.Status is "succeeded" or "failed") evt["outputs"] = st.Outputs.DeepClone();
        if (logs) evt["logs"] = Json.ToNode(st.Logs);
        events.Publish(rc.Run.Id, evt);
    }

    static string Title(RunCtx rc, string nodeId) => rc.Graph.Node(nodeId)?.Title ?? nodeId;

    async Task SaveAsync(RunCtx rc, CancellationToken ct)
    {
        string statesJson;
        lock (rc.Lock) statesJson = Json.Ser(rc.States);
        await using var db = await dbf.CreateDbContextAsync(ct);
        var run = await db.Runs.FindAsync([rc.Run.Id], ct);
        if (run is null) return;
        run.Status = rc.Run.Status;
        run.StartedAt = rc.Run.StartedAt;
        run.FinishedAt = rc.Run.FinishedAt;
        run.Error = rc.Run.Error;
        run.Outputs = rc.Run.Outputs;
        run.NodeStates = statesJson;
        await db.SaveChangesAsync(ct);
    }
}
