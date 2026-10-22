using ForgeFlow.Api;
using ForgeFlow.Core;
using ForgeFlow.Data;
using ForgeFlow.Engine;
using ForgeFlow.Nodes;
using ForgeFlow.Providers;
using ForgeFlow.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ForgeOptions>(builder.Configuration.GetSection("ForgeFlow"));
var dataDir = Path.GetFullPath(builder.Configuration["ForgeFlow:DataDir"] ?? "storage");
Directory.CreateDirectory(dataDir);

builder.Services.AddDbContextFactory<AppDb>(o => o.UseSqlite($"Data Source={Path.Combine(dataDir, "forgeflow.db")}"));
builder.Services.AddHttpClient("nodes", c => c.Timeout = TimeSpan.FromMinutes(15));
builder.Services.AddHttpClient("downloads", c => c.Timeout = TimeSpan.FromMinutes(30));
builder.Services.AddSingleton<AssetStore>();
builder.Services.AddSingleton<ProviderRegistry>();
builder.Services.AddSingleton<NodeRegistry>();
builder.Services.AddSingleton<TemplateLibrary>();
builder.Services.AddSingleton<RunEvents>();
builder.Services.AddSingleton<WorkflowEngine>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RunService>());
builder.Services.AddOpenApi();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = Json.Opts.PropertyNamingPolicy;
    o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never;
});
builder.Services.Configure<Microsoft.AspNetCore.Http.Features.FormOptions>(o => o.MultipartBodyLengthLimit = 1L << 30);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 1L << 30);

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDb>>().CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await ProviderRegistry.SeedAsync(db);
}

app.UseCors();

// Optional API key: protects /api and /mcp. Asset files stay readable so previews and engines can load them.
var apiKey = app.Services.GetRequiredService<IOptions<ForgeOptions>>().Value.ApiKey;
if (!string.IsNullOrEmpty(apiKey))
{
    app.Use(async (ctx, next) =>
    {
        var path = ctx.Request.Path;
        if ((path.StartsWithSegments("/api") || path.StartsWithSegments("/mcp")) && !path.StartsWithSegments("/api/health"))
        {
            var given = ctx.Request.Headers["X-Api-Key"].FirstOrDefault()
                        ?? ctx.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "")
                        ?? ctx.Request.Query["key"].FirstOrDefault();
            if (given != apiKey)
            {
                ctx.Response.StatusCode = 401;
                await ctx.Response.WriteAsJsonAsync(new { error = "Missing or wrong API key" });
                return;
            }
        }
        await next();
    });
}

var store = app.Services.GetRequiredService<AssetStore>();
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(store.AssetsDir),
    RequestPath = "/files",
    ServeUnknownFileTypes = true,
});
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapOpenApi();
app.MapForgeApi();
app.MapForgeMcp();
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
