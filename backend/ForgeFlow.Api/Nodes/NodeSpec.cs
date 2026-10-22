using System.Text.Json.Nodes;

namespace ForgeFlow.Nodes;

/// <summary>Value kinds flowing through ports: text, image, video, model, audio, file, json, any.</summary>
public static class Kinds
{
    public const string Text = "text", Image = "image", Video = "video", Model = "model", Audio = "audio",
        File = "file", Json = "json", Any = "any";
}

public record PortSpec(string Name, string Kind, bool List = false, bool Optional = true, string? Label = null, string? Help = null);

/// <summary>
/// Param kinds: string, text (multi-line, templated), number, bool, select, provider, mcpServers, json, code, asset.
/// </summary>
public record ParamSpec(
    string Name,
    string Kind,
    string? Label = null,
    JsonNode? Default = null,
    string[]? Options = null,
    string[]? ProviderTypes = null,
    string? Help = null);

public record NodeSpec(
    string Type,
    string Title,
    string Category,
    string Description,
    PortSpec[] Inputs,
    PortSpec[] Outputs,
    ParamSpec[] Params,
    string? Color = null,
    bool Custom = false);

public interface INodeType
{
    NodeSpec Spec { get; }
    Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx);
}

public class NodeException(string message) : Exception(message);
