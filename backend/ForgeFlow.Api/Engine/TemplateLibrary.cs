using System.Text.Json.Nodes;
using ForgeFlow.Core;
using ForgeFlow.Storage;

namespace ForgeFlow.Engine;

public record PipelineTemplate(string Id, string Name, string Description, string[] Tags, WorkflowGraph Graph);

/// <summary>Pipeline templates: built-in ones shipped next to the binary, plus user ones in DataDir/templates.</summary>
public class TemplateLibrary
{
    readonly string[] _dirs;
    readonly ILogger<TemplateLibrary> _log;

    public TemplateLibrary(AssetStore store, IWebHostEnvironment env, ILogger<TemplateLibrary> log)
    {
        _log = log;
        var userDir = Path.Combine(store.DataDir, "templates");
        Directory.CreateDirectory(userDir);
        _dirs = [Path.Combine(AppContext.BaseDirectory, "templates"), Path.Combine(env.ContentRootPath, "templates"), userDir];
    }

    public IEnumerable<PipelineTemplate> All
    {
        get
        {
            var seen = new HashSet<string>();
            foreach (var dir in _dirs.Where(Directory.Exists).Distinct())
                foreach (var file in Directory.EnumerateFiles(dir, "*.json").Order())
                {
                    PipelineTemplate? t = null;
                    try
                    {
                        var o = Json.ParseObject(File.ReadAllText(file));
                        t = new PipelineTemplate(
                            o.Str("id") ?? Path.GetFileNameWithoutExtension(file),
                            o.Str("name") ?? Path.GetFileNameWithoutExtension(file),
                            o.Str("description") ?? "",
                            (o["tags"] as JsonArray)?.Select(x => Json.AsText(x)).ToArray() ?? [],
                            Json.De<WorkflowGraph>(o["graph"]?.ToJsonString() ?? "{}"));
                    }
                    catch (Exception ex) { _log.LogWarning(ex, "Bad template {File}", file); }
                    if (t is not null && seen.Add(t.Id)) yield return t;
                }
        }
    }

    public PipelineTemplate? Get(string id) => All.FirstOrDefault(t => t.Id == id);
}
