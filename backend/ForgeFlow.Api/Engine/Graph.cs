using System.Text.Json.Nodes;

namespace ForgeFlow.Engine;

/// <summary>A pipeline: nodes connected by edges from output ports to input ports. Shared with the UI as JSON.</summary>
public class WorkflowGraph
{
    public List<NodeDef> Nodes { get; set; } = [];
    public List<EdgeDef> Edges { get; set; } = [];

    public NodeDef? Node(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    public IEnumerable<EdgeDef> IncomingEdges(string nodeId) => Edges.Where(e => e.Target == nodeId);

    public IEnumerable<string> Upstream(string nodeId) => IncomingEdges(nodeId).Select(e => e.Source).Distinct();

    /// <summary>The node itself plus everything reachable from it.</summary>
    public HashSet<string> DownstreamOf(string nodeId)
    {
        var seen = new HashSet<string> { nodeId };
        var stack = new Stack<string>([nodeId]);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            foreach (var e in Edges.Where(e => e.Source == cur))
                if (seen.Add(e.Target)) stack.Push(e.Target);
        }
        return seen;
    }

    /// <summary>Kahn topological order; throws on cycles or dangling edges.</summary>
    public List<string> TopologicalOrder()
    {
        var ids = Nodes.Select(n => n.Id).ToHashSet();
        foreach (var e in Edges)
            if (!ids.Contains(e.Source) || !ids.Contains(e.Target))
                throw new InvalidOperationException($"Edge {e.Id} references a missing node");
        var indeg = Nodes.ToDictionary(n => n.Id, n => Upstream(n.Id).Count());
        var queue = new Queue<string>(indeg.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var order = new List<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var next in Edges.Where(e => e.Source == id).Select(e => e.Target).Distinct())
                if (--indeg[next] == 0) queue.Enqueue(next);
        }
        if (order.Count != Nodes.Count) throw new InvalidOperationException("The pipeline contains a cycle");
        return order;
    }
}

public class NodeDef
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string? Title { get; set; }
    public JsonObject Params { get; set; } = new();
    public NodePosition? Position { get; set; }
    /// <summary>Extra attempts after a failure.</summary>
    public int Retries { get; set; }
    /// <summary>How many list items run at once when the node fans out over a list.</summary>
    public int MapConcurrency { get; set; } = 2;
    public bool Disabled { get; set; }
}

public class NodePosition
{
    public double X { get; set; }
    public double Y { get; set; }
}

public class EdgeDef
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourcePort { get; set; } = "";
    public string Target { get; set; } = "";
    public string TargetPort { get; set; } = "";
}

public class NodeState
{
    /// <summary>pending | running | succeeded | failed | skipped | canceled</summary>
    public string Status { get; set; } = "pending";
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
    public JsonObject Outputs { get; set; } = new();
    public string? Error { get; set; }
    public double? Progress { get; set; }
    public string? Message { get; set; }
    public List<string> Logs { get; set; } = [];
    /// <summary>Set when the node fanned out: total items and finished items.</summary>
    public int? Items { get; set; }
    public int? ItemsDone { get; set; }
    /// <summary>True when outputs were reused from an earlier run.</summary>
    public bool Cached { get; set; }
}
