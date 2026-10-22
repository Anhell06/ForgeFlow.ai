using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ForgeFlow.Core;
using ForgeFlow.Data;
using Microsoft.EntityFrameworkCore;

namespace ForgeFlow.Engine;

/// <summary>
/// Content-addressed cache of node results per pipeline. A node's key is a fingerprint of its type, settings,
/// input values and the pipeline inputs its templates reference — so a changed upstream result changes the
/// inputs downstream and those nodes miss the cache automatically.
/// </summary>
public partial class NodeCacheService(IDbContextFactory<AppDb> dbf)
{
    const int KeepPerNode = 10;

    /// <summary>Inputs just copy pipeline inputs and outputs only pass values through: caching them buys nothing.</summary>
    public static bool IsCacheable(NodeDef def) =>
        def.Cache != false && !def.Type.StartsWith("input.") && !def.Type.StartsWith("output.");

    [GeneratedRegex(@"workflow\.([A-Za-z0-9_]+)")]
    private static partial Regex WorkflowRef();

    public static string Fingerprint(NodeDef def, IReadOnlyDictionary<string, JsonNode?> inputs, JsonObject workflowInputs)
    {
        var parms = def.Params.ToJsonString();
        var referenced = new JsonObject();
        foreach (var name in WorkflowRef().Matches(parms).Select(m => m.Groups[1].Value).Distinct().Order())
            referenced[name] = workflowInputs[name]?.DeepClone();
        var ins = new JsonObject();
        foreach (var (k, v) in inputs.OrderBy(kv => kv.Key, StringComparer.Ordinal)) ins[k] = v?.DeepClone();
        var doc = new JsonObject
        {
            ["type"] = def.Type,
            ["params"] = Canonical(def.Params),
            ["inputs"] = Canonical(ins),
            ["workflow"] = referenced,
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(doc.ToJsonString())));
    }

    /// <summary>Same JSON regardless of property order.</summary>
    static JsonNode? Canonical(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => KeyValuePair.Create(kv.Key, Canonical(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(Canonical).ToArray()),
        _ => node?.DeepClone(),
    };

    /// <summary>Cached outputs, or null when missing or when a file they point to was deleted.</summary>
    public async Task<JsonObject?> GetAsync(string workflowId, string nodeId, string key, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var row = await db.NodeCache.AsNoTracking()
            .Where(c => c.WorkflowId == workflowId && c.NodeId == nodeId && c.Key == key)
            .OrderByDescending(c => c.CreatedAt).FirstOrDefaultAsync(ct);
        if (row is null) return null;
        var outputs = Json.ParseObject(row.Outputs);
        var files = AssetRef.All(new JsonArray(outputs.Select(kv => kv.Value?.DeepClone()).ToArray()));
        return files.All(a => File.Exists(a.Path)) ? outputs : null;
    }

    public async Task StoreAsync(string workflowId, string nodeId, string key, JsonObject outputs, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        await db.NodeCache.Where(c => c.WorkflowId == workflowId && c.NodeId == nodeId && c.Key == key).ExecuteDeleteAsync(ct);
        db.NodeCache.Add(new NodeCacheEntity { WorkflowId = workflowId, NodeId = nodeId, Key = key, Outputs = outputs.ToJsonString() });
        await db.SaveChangesAsync(ct);
        // Keep a few recent variants per node so switching a setting back and forth stays free.
        var stale = await db.NodeCache.Where(c => c.WorkflowId == workflowId && c.NodeId == nodeId)
            .OrderByDescending(c => c.CreatedAt).Skip(KeepPerNode).Select(c => c.Id).ToListAsync(ct);
        if (stale.Count > 0) await db.NodeCache.Where(c => stale.Contains(c.Id)).ExecuteDeleteAsync(ct);
    }

    /// <summary>Deletes cached results of the given nodes (null = the whole pipeline). Returns the number of rows removed.</summary>
    public async Task<int> ClearAsync(string workflowId, IReadOnlyCollection<string>? nodeIds, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        var q = db.NodeCache.Where(c => c.WorkflowId == workflowId);
        if (nodeIds is not null) q = q.Where(c => nodeIds.Contains(c.NodeId));
        return await q.ExecuteDeleteAsync(ct);
    }
}
