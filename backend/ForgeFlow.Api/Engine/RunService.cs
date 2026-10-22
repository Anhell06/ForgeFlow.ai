using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using ForgeFlow.Core;
using ForgeFlow.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ForgeFlow.Engine;

/// <summary>Creates runs, queues them and executes them on background workers.</summary>
public class RunService(IDbContextFactory<AppDb> dbf, RunEvents events, IServiceProvider sp, IOptions<ForgeOptions> opt, ILogger<RunService> log)
    : BackgroundService
{
    readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();
    readonly ConcurrentDictionary<string, TaskCompletionSource<string>> _waiters = new();

    public async Task<RunEntity> CreateAsync(WorkflowGraph graph, JsonObject inputs, string name, string? workflowId,
        Dictionary<string, NodeState>? reuse = null, string? parentRunId = null, CancellationToken ct = default)
    {
        graph.TopologicalOrder(); // validate early: cycles and dangling edges
        var states = graph.Nodes.ToDictionary(n => n.Id, n => reuse is not null && reuse.TryGetValue(n.Id, out var s) ? s : new NodeState());
        var run = new RunEntity
        {
            Name = name,
            WorkflowId = workflowId,
            Inputs = inputs.ToJsonString(),
            Graph = Json.Ser(graph),
            NodeStates = Json.Ser(states),
            ParentRunId = parentRunId,
        };
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            db.Runs.Add(run);
            await db.SaveChangesAsync(ct);
        }
        events.Publish(run.Id, new JsonObject { ["type"] = "run.status", ["status"] = run.Status, ["name"] = run.Name });
        await _queue.Writer.WriteAsync(run.Id, ct);
        return run;
    }

    /// <summary>Re-runs a finished run. With fromNodeId, everything upstream of that node is reused from the old run.</summary>
    public async Task<RunEntity> RerunAsync(string runId, string? fromNodeId, JsonObject? inputsOverride, CancellationToken ct)
    {
        RunEntity old;
        await using (var db = await dbf.CreateDbContextAsync(ct))
            old = await db.Runs.FindAsync([runId], ct) ?? throw new KeyNotFoundException("Run not found");
        var graph = Json.De<WorkflowGraph>(old.Graph);
        Dictionary<string, NodeState>? reuse = null;
        if (!string.IsNullOrEmpty(fromNodeId))
        {
            if (graph.Node(fromNodeId) is null) throw new KeyNotFoundException($"Node {fromNodeId} not in run");
            var dirty = graph.DownstreamOf(fromNodeId);
            var oldStates = Json.De<Dictionary<string, NodeState>>(old.NodeStates);
            reuse = oldStates
                .Where(kv => !dirty.Contains(kv.Key) && kv.Value.Status == "succeeded")
                .ToDictionary(kv => kv.Key, kv => new NodeState
                {
                    Status = "succeeded", Outputs = kv.Value.Outputs, Cached = true, StartedAt = kv.Value.StartedAt,
                    FinishedAt = kv.Value.FinishedAt, Logs = ["reused from " + runId],
                });
        }
        var inputs = inputsOverride ?? Json.ParseObject(old.Inputs);
        return await CreateAsync(graph, inputs, old.Name, old.WorkflowId, reuse, runId, ct);
    }

    public async Task<bool> CancelAsync(string runId)
    {
        if (_active.TryGetValue(runId, out var cts))
        {
            await cts.CancelAsync();
            return true;
        }
        await using var db = await dbf.CreateDbContextAsync();
        var run = await db.Runs.FindAsync(runId);
        if (run is null || run.Status != "queued") return false;
        run.Status = "canceled";
        run.FinishedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        events.Publish(runId, new JsonObject { ["type"] = "run.status", ["status"] = "canceled" });
        Complete(runId, "canceled");
        return true;
    }

    /// <summary>Waits for a run to reach a terminal state (used by the agent API with ?wait=).</summary>
    public async Task<string> WaitAsync(string runId, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = _waiters.GetOrAdd(runId, _ => new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously));
        await using (var db = await dbf.CreateDbContextAsync(ct))
        {
            var run = await db.Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
            if (run is null) throw new KeyNotFoundException("Run not found");
            if (run.Status is "succeeded" or "failed" or "canceled") return run.Status;
        }
        try { return await tcs.Task.WaitAsync(timeout, ct); }
        catch (TimeoutException) { return "running"; }
    }

    void Complete(string runId, string status)
    {
        if (_waiters.TryRemove(runId, out var tcs)) tcs.TrySetResult(status);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        var workers = Enumerable.Range(0, Math.Max(1, opt.Value.MaxParallelRuns)).Select(_ => Worker(stoppingToken));
        await Task.WhenAll(workers);
    }

    async Task RecoverAsync(CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        foreach (var r in await db.Runs.Where(r => r.Status == "running").ToListAsync(ct))
        {
            r.Status = "failed";
            r.Error = "Interrupted by server restart";
            r.FinishedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        foreach (var id in await db.Runs.Where(r => r.Status == "queued").OrderBy(r => r.CreatedAt).Select(r => r.Id).ToListAsync(ct))
            await _queue.Writer.WriteAsync(id, ct);
    }

    async Task Worker(CancellationToken stoppingToken)
    {
        await foreach (var runId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            _active[runId] = cts;
            string status = "failed";
            try
            {
                var engine = sp.GetRequiredService<WorkflowEngine>();
                status = await engine.ExecuteAsync(runId, cts.Token);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Run {RunId} crashed", runId);
            }
            finally
            {
                _active.TryRemove(runId, out _);
                Complete(runId, status);
            }
        }
    }
}
