using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ForgeFlow.Data;

public class AppDb(DbContextOptions<AppDb> options) : DbContext(options)
{
    public DbSet<AssetEntity> Assets => Set<AssetEntity>();
    public DbSet<ProviderEntity> Providers => Set<ProviderEntity>();
    public DbSet<McpServerEntity> McpServers => Set<McpServerEntity>();
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();
    public DbSet<RunEntity> Runs => Set<RunEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AssetEntity>().HasIndex(a => a.CreatedAt);
        b.Entity<AssetEntity>().HasIndex(a => a.RunId);
        b.Entity<RunEntity>().HasIndex(r => r.CreatedAt);
        b.Entity<RunEntity>().HasIndex(r => r.WorkflowId);

        // SQLite stores no DateTimeKind; mark values as UTC so the API serializes them with a "Z".
        var utc = new ValueConverter<DateTime, DateTime>(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        var utcNullable = new ValueConverter<DateTime?, DateTime?>(v => v, v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
        foreach (var entity in b.Model.GetEntityTypes())
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTime)) prop.SetValueConverter(utc);
                else if (prop.ClrType == typeof(DateTime?)) prop.SetValueConverter(utcNullable);
            }
    }
}

public static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];
}

public class AssetEntity
{
    [Key] public string Id { get; set; } = Ids.New("ast");
    public string Kind { get; set; } = "file";
    public string Name { get; set; } = "";
    public string Mime { get; set; } = "application/octet-stream";
    public string RelPath { get; set; } = "";
    public long Size { get; set; }
    public string? RunId { get; set; }
    public string? NodeId { get; set; }
    public string? Source { get; set; }
    public string Tags { get; set; } = "[]";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A connection to a model backend: cloud API, local server, ComfyUI instance.</summary>
public class ProviderEntity
{
    [Key] public string Id { get; set; } = Ids.New("prv");
    public string Name { get; set; } = "";
    /// <summary>openai | anthropic | tripo | meshy | fal | replicate | comfyui | http</summary>
    public string Type { get; set; } = "openai";
    public string? BaseUrl { get; set; }
    /// <summary>Literal key or "env:VAR_NAME".</summary>
    public string? ApiKey { get; set; }
    public string? DefaultModel { get; set; }
    public string Extra { get; set; } = "{}";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>An MCP server agents can use as a toolbox (Blender MCP, Unity MCP, filesystem, ...).</summary>
public class McpServerEntity
{
    [Key] public string Id { get; set; } = Ids.New("mcp");
    public string Name { get; set; } = "";
    /// <summary>stdio | http</summary>
    public string Transport { get; set; } = "stdio";
    public string? Command { get; set; }
    public string Args { get; set; } = "[]";
    public string Env { get; set; } = "{}";
    public string? Cwd { get; set; }
    public string? Url { get; set; }
    public string Headers { get; set; } = "{}";
    public bool Enabled { get; set; } = true;
}

public class WorkflowEntity
{
    [Key] public string Id { get; set; } = Ids.New("wf");
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Graph { get; set; } = "{}";
    public string? TemplateId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public class RunEntity
{
    [Key] public string Id { get; set; } = Ids.New("run");
    public string? WorkflowId { get; set; }
    public string Name { get; set; } = "";
    /// <summary>queued | running | succeeded | failed | canceled</summary>
    public string Status { get; set; } = "queued";
    public string Inputs { get; set; } = "{}";
    public string Graph { get; set; } = "{}";
    public string NodeStates { get; set; } = "{}";
    public string Outputs { get; set; } = "{}";
    public string? Error { get; set; }
    public string? ParentRunId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAt { get; set; }
    public DateTime? FinishedAt { get; set; }
}
