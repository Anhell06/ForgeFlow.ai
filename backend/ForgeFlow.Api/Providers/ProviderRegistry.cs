using System.Text.Json.Nodes;
using ForgeFlow.Core;
using ForgeFlow.Data;
using Microsoft.EntityFrameworkCore;

namespace ForgeFlow.Providers;

public sealed record ProviderInfo(string Id, string Name, string Type, string? BaseUrl, string? ApiKey, string? DefaultModel, JsonObject Extra)
{
    public string RequireKey() => string.IsNullOrEmpty(ApiKey)
        ? throw new InvalidOperationException($"Provider '{Name}' has no API key (set it in Settings or via env:VAR)")
        : ApiKey;
}

public static class ProviderTypes
{
    public record TypeInfo(string Type, string Title, string? DefaultBaseUrl, string KeyHelp, string Description);

    public static readonly TypeInfo[] All =
    [
        new("openai", "OpenAI-compatible", "https://api.openai.com/v1", "env:OPENAI_API_KEY",
            "OpenAI, plus any OpenAI-compatible server: Ollama (http://localhost:11434/v1), LM Studio (http://localhost:1234/v1), vLLM, OpenRouter. Chat, tools, images."),
        new("anthropic", "Anthropic (Claude)", null, "env:ANTHROPIC_API_KEY", "Claude models through the official Anthropic SDK. Chat, vision, tool use."),
        new("tripo", "Tripo 3D", "https://api.tripo3d.ai/v2/openapi", "env:TRIPO_API_KEY", "Image/text to 3D, rigging, retargeting, conversion."),
        new("meshy", "Meshy", "https://api.meshy.ai", "env:MESHY_API_KEY", "Image/text to 3D."),
        new("fal", "fal.ai", "https://queue.fal.run", "env:FAL_KEY", "Any fal model: video (Kling, Wan, Seedance), images, 3D (TRELLIS, Hunyuan3D)."),
        new("replicate", "Replicate", "https://api.replicate.com/v1", "env:REPLICATE_API_TOKEN", "Any Replicate model."),
        new("comfyui", "ComfyUI", "http://127.0.0.1:8188", "(usually none)", "Local ComfyUI server running API-format workflows."),
        new("http", "Custom HTTP", null, "optional", "Your own neural network server. Use with the HTTP Request node or custom node definitions."),
    ];
}

/// <summary>Resolves provider records (with env: secrets) for nodes.</summary>
public class ProviderRegistry(IDbContextFactory<AppDb> dbf)
{
    public async Task<ProviderInfo> ResolveAsync(string? idOrName, string[] allowedTypes, CancellationToken ct)
    {
        await using var db = await dbf.CreateDbContextAsync(ct);
        ProviderEntity? e = null;
        if (!string.IsNullOrWhiteSpace(idOrName))
        {
            e = await db.Providers.FirstOrDefaultAsync(p => p.Id == idOrName || p.Name == idOrName, ct)
                ?? throw new InvalidOperationException($"Provider '{idOrName}' not found");
        }
        else
        {
            var candidates = await db.Providers.Where(p => allowedTypes.Contains(p.Type)).ToListAsync(ct);
            // Prefer the first provider that actually has credentials available.
            e = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(ResolveSecret(p.ApiKey)) || p.Type is "comfyui" or "http")
                ?? candidates.FirstOrDefault()
                ?? throw new InvalidOperationException($"No provider configured for: {string.Join(", ", allowedTypes)}");
        }
        if (allowedTypes.Length > 0 && !allowedTypes.Contains(e.Type))
            throw new InvalidOperationException($"Provider '{e.Name}' is '{e.Type}', this node supports: {string.Join(", ", allowedTypes)}");
        return ToInfo(e);
    }

    public static ProviderInfo ToInfo(ProviderEntity e)
    {
        var baseUrl = string.IsNullOrWhiteSpace(e.BaseUrl)
            ? ProviderTypes.All.FirstOrDefault(t => t.Type == e.Type)?.DefaultBaseUrl
            : e.BaseUrl.TrimEnd('/');
        return new ProviderInfo(e.Id, e.Name, e.Type, baseUrl, ResolveSecret(e.ApiKey), e.DefaultModel, Json.ParseObject(e.Extra));
    }

    public static string? ResolveSecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.StartsWith("env:") ? Environment.GetEnvironmentVariable(value[4..].Trim()) : value;
    }

    public static async Task SeedAsync(AppDb db)
    {
        if (await db.Providers.AnyAsync()) return;
        db.Providers.AddRange(
            new ProviderEntity { Name = "OpenAI", Type = "openai", ApiKey = "env:OPENAI_API_KEY", DefaultModel = "gpt-5" },
            new ProviderEntity { Name = "Local LLM (Ollama)", Type = "openai", BaseUrl = "http://localhost:11434/v1", ApiKey = "ollama", DefaultModel = "gpt-oss:20b" },
            new ProviderEntity { Name = "LM Studio", Type = "openai", BaseUrl = "http://localhost:1234/v1", ApiKey = "lm-studio", DefaultModel = "local-model" },
            new ProviderEntity { Name = "Anthropic", Type = "anthropic", ApiKey = "env:ANTHROPIC_API_KEY", DefaultModel = "claude-opus-5-5" },
            new ProviderEntity { Name = "Tripo", Type = "tripo", ApiKey = "env:TRIPO_API_KEY" },
            new ProviderEntity { Name = "Meshy", Type = "meshy", ApiKey = "env:MESHY_API_KEY" },
            new ProviderEntity { Name = "fal.ai", Type = "fal", ApiKey = "env:FAL_KEY" },
            new ProviderEntity { Name = "Replicate", Type = "replicate", ApiKey = "env:REPLICATE_API_TOKEN" },
            new ProviderEntity { Name = "ComfyUI (local)", Type = "comfyui", BaseUrl = "http://127.0.0.1:8188" });
        if (!await db.McpServers.AnyAsync())
            db.McpServers.AddRange(
                new McpServerEntity { Name = "Blender MCP", Transport = "stdio", Command = "uvx", Args = "[\"blender-mcp\"]" },
                new McpServerEntity { Name = "Unity MCP", Transport = "http", Url = "http://localhost:8080/mcp", Enabled = false });
        await db.SaveChangesAsync();
    }
}
