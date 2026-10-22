using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using ForgeFlow.Core;
using ForgeFlow.Nodes.Builtin;
using ForgeFlow.Storage;

namespace ForgeFlow.Nodes;

/// <summary>Built-in node types plus custom HTTP nodes loaded from DataDir/nodes/*.json.</summary>
public class NodeRegistry
{
    readonly ConcurrentDictionary<string, INodeType> _types = new();
    readonly string _customDir;
    readonly ILogger<NodeRegistry> _log;

    public NodeRegistry(AssetStore store, ILogger<NodeRegistry> log)
    {
        _log = log;
        _customDir = Path.Combine(store.DataDir, "nodes");
        Directory.CreateDirectory(_customDir);
        INodeType[] builtins =
        [
            new TextInputNode(), new AssetInputNode(), new TextTemplateNode(), new TextSplitNode(), new ResultNode(), new PreviewNode(), new MockNode(),
            new LlmChatNode(),
            new ImageGenerateNode(), new VideoGenerateNode(), new Model3dGenerateNode(), new TripoTaskNode(), new VideoFramesNode(),
            new ImageCollageNode(), new ImageSliceNode(), new ImageRemoveBackgroundNode(),
            new HttpRequestNode(), new ComfyWorkflowNode(),
            new McpAgentNode(), new CliAgentNode(),
        ];
        foreach (var t in builtins) _types[t.Spec.Type] = t;
        ReloadCustom();
    }

    public IEnumerable<NodeSpec> Specs => _types.Values.Select(t => t.Spec).OrderBy(s => s.Category).ThenBy(s => s.Title);

    public bool TryGet(string type, [NotNullWhen(true)] out INodeType? node) => _types.TryGetValue(type, out node);

    public string CustomDir => _customDir;

    public void ReloadCustom()
    {
        foreach (var key in _types.Where(kv => kv.Value.Spec.Custom).Select(kv => kv.Key).ToList())
            _types.TryRemove(key, out _);
        foreach (var file in Directory.EnumerateFiles(_customDir, "*.json"))
        {
            try
            {
                var def = Json.ParseObject(File.ReadAllText(file));
                var node = CustomHttpNode.FromDefinition(def);
                _types[node.Spec.Type] = node;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping custom node {File}", file);
            }
        }
    }

    public JsonObject? ReadCustom(string type)
    {
        var path = CustomPath(type);
        return File.Exists(path) ? Json.ParseObject(File.ReadAllText(path)) : null;
    }

    public void SaveCustom(JsonObject definition)
    {
        var node = CustomHttpNode.FromDefinition(definition); // validates
        File.WriteAllText(CustomPath(node.Spec.Type), definition.ToJsonString(Json.Pretty));
        ReloadCustom();
    }

    public bool DeleteCustom(string type)
    {
        var path = CustomPath(type);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        ReloadCustom();
        return true;
    }

    string CustomPath(string type)
    {
        var safe = new string(type.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_').ToArray());
        return Path.Combine(_customDir, safe + ".json");
    }
}
