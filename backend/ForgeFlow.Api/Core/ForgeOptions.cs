namespace ForgeFlow.Core;

public class ForgeOptions
{
    /// <summary>Root for the database, assets, run work dirs and custom node definitions.</summary>
    public string DataDir { get; set; } = "storage";
    /// <summary>Absolute base URL used when building asset links.</summary>
    public string PublicUrl { get; set; } = "http://localhost:5180";
    /// <summary>When set, every /api and /mcp request must carry it (X-Api-Key, Bearer token or ?key=).</summary>
    public string? ApiKey { get; set; }
    public int MaxParallelRuns { get; set; } = 2;
    public int MaxParallelNodes { get; set; } = 4;
}
