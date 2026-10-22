using System.Text.Json.Nodes;

namespace ForgeFlow.Core;

/// <summary>A file produced or consumed by nodes. Travels between nodes as a JSON object tagged with "$asset".</summary>
public sealed record AssetRef(string Id, string Kind, string Name, string Mime, string Url, string Path, long Size)
{
    public JsonObject ToJson() => new()
    {
        ["$asset"] = Id,
        ["kind"] = Kind,
        ["name"] = Name,
        ["mime"] = Mime,
        ["url"] = Url,
        ["path"] = Path,
        ["size"] = Size,
    };

    public static AssetRef? From(JsonNode? node)
    {
        if (node is not JsonObject o || o.Str("$asset") is not { } id) return null;
        return new AssetRef(id, o.Str("kind") ?? "file", o.Str("name") ?? id, o.Str("mime") ?? "application/octet-stream",
            o.Str("url") ?? "", o.Str("path") ?? "", o["size"]?.GetValue<long>() ?? 0);
    }

    public static IEnumerable<AssetRef> All(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o when From(o) is { } a:
                yield return a;
                break;
            case JsonArray arr:
                foreach (var item in arr)
                    foreach (var a in All(item)) yield return a;
                break;
        }
    }
}

public static class MediaKinds
{
    static readonly Dictionary<string, (string Kind, string Mime)> ByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = ("image", "image/png"),
        [".jpg"] = ("image", "image/jpeg"),
        [".jpeg"] = ("image", "image/jpeg"),
        [".webp"] = ("image", "image/webp"),
        [".gif"] = ("image", "image/gif"),
        [".bmp"] = ("image", "image/bmp"),
        [".tga"] = ("image", "image/x-tga"),
        [".mp4"] = ("video", "video/mp4"),
        [".webm"] = ("video", "video/webm"),
        [".mov"] = ("video", "video/quicktime"),
        [".glb"] = ("model", "model/gltf-binary"),
        [".gltf"] = ("model", "model/gltf+json"),
        [".fbx"] = ("model", "application/octet-stream"),
        [".obj"] = ("model", "model/obj"),
        [".stl"] = ("model", "model/stl"),
        [".ply"] = ("model", "application/octet-stream"),
        [".usdz"] = ("model", "model/vnd.usdz+zip"),
        [".blend"] = ("model", "application/x-blender"),
        [".vox"] = ("model", "application/octet-stream"),
        [".wav"] = ("audio", "audio/wav"),
        [".mp3"] = ("audio", "audio/mpeg"),
        [".ogg"] = ("audio", "audio/ogg"),
        [".flac"] = ("audio", "audio/flac"),
        [".txt"] = ("text", "text/plain"),
        [".md"] = ("text", "text/markdown"),
        [".json"] = ("json", "application/json"),
        [".zip"] = ("file", "application/zip"),
        [".unitypackage"] = ("file", "application/octet-stream"),
    };

    static readonly Dictionary<string, string> ExtByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png", ["image/jpeg"] = ".jpg", ["image/webp"] = ".webp", ["image/gif"] = ".gif",
        ["video/mp4"] = ".mp4", ["video/webm"] = ".webm", ["model/gltf-binary"] = ".glb", ["model/gltf+json"] = ".gltf",
        ["audio/wav"] = ".wav", ["audio/mpeg"] = ".mp3", ["application/json"] = ".json", ["text/plain"] = ".txt",
    };

    public static (string Kind, string Mime) FromExtension(string? ext) =>
        ext is not null && ByExt.TryGetValue(ext, out var r) ? r : ("file", "application/octet-stream");

    public static string? ExtensionFromMime(string? mime)
    {
        if (mime is null) return null;
        var clean = mime.Split(';')[0].Trim();
        return ExtByMime.TryGetValue(clean, out var e) ? e : null;
    }

    public static bool IsKnownFileExtension(string ext) => ByExt.ContainsKey(ext);
}
