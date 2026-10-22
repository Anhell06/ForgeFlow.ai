using System.Net.Http.Json;
using System.Text.Json.Nodes;
using ForgeFlow.Agents;
using ForgeFlow.Data;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ForgeFlow.Tests;

public sealed class ForgeFactory : WebApplicationFactory<Program>
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "forgeflow-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public ForgeFactory()
    {
        // Program reads DataDir before the host is built, so pass it through the environment.
        Environment.SetEnvironmentVariable("ForgeFlow__DataDir", DataDir);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, true); } catch { /* sqlite may still hold the file briefly */ }
    }
}

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<ForgeFactory>;

[Collection("api")]
public class ApiTests(ForgeFactory factory)
{
    readonly HttpClient _http = factory.CreateClient();

    [Fact]
    public async Task ListsNodesTemplatesAndSeededProviders()
    {
        var nodes = await _http.GetFromJsonAsync<JsonArray>("/api/nodes");
        Assert.Contains(nodes!, n => n!["type"]!.GetValue<string>() == "agent.mcp");
        var templates = await _http.GetFromJsonAsync<JsonArray>("/api/templates");
        Assert.True(templates!.Count >= 5);
        var providers = await _http.GetFromJsonAsync<JsonArray>("/api/providers");
        Assert.Contains(providers!, p => p!["type"]!.GetValue<string>() == "tripo");
    }

    [Fact]
    public async Task RunsOfflineDemoEndToEnd()
    {
        var wf = await (await _http.PostAsync("/api/templates/offline-demo/instantiate", null)).Content.ReadFromJsonAsync<JsonObject>();
        var id = wf!["id"]!.GetValue<string>();
        var resp = await _http.PostAsJsonAsync($"/api/workflows/{id}/run", new { inputs = new { prompt = "a\nb\nc" }, wait = true, waitSeconds = 60 });
        var run = await resp.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("succeeded", run!["status"]!.GetValue<string>());

        // Fan-out: three items → three mock images → collage → slicer finds three elements again.
        var nodes = run["nodes"]!.AsObject();
        Assert.Equal(3, nodes["gen"]!["items"]!.GetValue<int>());
        var slices = run["outputs"]!["slices"]!.AsArray();
        Assert.Equal(3, slices.Count);
        var model = run["outputs"]!["model"]!;
        Assert.EndsWith(".glb", model["name"]!.GetValue<string>());
        Assert.True(File.Exists(model["path"]!.GetValue<string>()));

        // Files are served for previews.
        var file = await _http.GetAsync(new Uri(model["url"]!.GetValue<string>()).AbsolutePath);
        Assert.True(file.IsSuccessStatusCode);

        // Rerun from the collage node reuses the upstream nodes.
        var rerun = await (await _http.PostAsJsonAsync($"/api/runs/{run["id"]}/rerun", new { fromNodeId = "board" })).Content.ReadFromJsonAsync<JsonObject>();
        var rerunId = rerun!["id"]!.GetValue<string>();
        JsonObject? done = null;
        for (var i = 0; i < 100 && done?["status"]?.GetValue<string>() is not ("succeeded" or "failed"); i++)
        {
            await Task.Delay(200);
            done = await _http.GetFromJsonAsync<JsonObject>($"/api/runs/{rerunId}");
        }
        Assert.Equal("succeeded", done!["status"]!.GetValue<string>());
        Assert.True(done["nodes"]!["gen"]!["cached"]!.GetValue<bool>());
        Assert.False(done["nodes"]!["board"]!["cached"]!.GetValue<bool>());
    }

    [Fact]
    public async Task FailedNodeSkipsDownstream()
    {
        var graph = new
        {
            nodes = new object[]
            {
                new { id = "a", type = "debug.mock", @params = new { fail = true, delayMs = 10 } },
                new { id = "b", type = "debug.mock", @params = new { delayMs = 10 } },
            },
            edges = new[] { new { id = "e", source = "a", sourcePort = "out", target = "b", targetPort = "in" } },
        };
        var run = await (await _http.PostAsJsonAsync("/api/runs", new { graph, name = "fail test" })).Content.ReadFromJsonAsync<JsonObject>();
        var id = run!["id"]!.GetValue<string>();
        JsonObject? done = null;
        for (var i = 0; i < 100 && done?["status"]?.GetValue<string>() is not ("succeeded" or "failed"); i++)
        {
            await Task.Delay(100);
            done = await _http.GetFromJsonAsync<JsonObject>($"/api/runs/{id}");
        }
        Assert.Equal("failed", done!["status"]!.GetValue<string>());
        Assert.Equal("skipped", done["nodes"]!["b"]!["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task SingleNodeToolModeAndMcpServer()
    {
        var run = await (await _http.PostAsJsonAsync("/api/nodes/debug.mock/run", new { inputs = new { @in = "hi" }, @params = new { kind = "text", delayMs = 10 }, wait = true }))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("succeeded", run!["status"]!.GetValue<string>());
        Assert.Equal("mock: hi", run["nodes"]!["node"]!["outputs"]!["out"]!.GetValue<string>());

        // Preview collects into a list port and unwraps a single value.
        var preview = await (await _http.PostAsJsonAsync("/api/nodes/output.preview/run", new { inputs = new { value = "final text" }, wait = true }))
            .Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("succeeded", preview!["status"]!.GetValue<string>());
        Assert.Equal("final text", preview["nodes"]!["node"]!["outputs"]!["value"]!.GetValue<string>());

        // Our own MCP client against our own MCP endpoint.
        var cfg = new McpServerEntity { Name = "self", Transport = "http", Url = new Uri(_http.BaseAddress!, "/mcp").ToString() };
        await using var mcp = await McpClient.ConnectAsync(cfg, _ => { }, CancellationToken.None, _http);
        var tools = await mcp.ListToolsAsync(CancellationToken.None);
        Assert.Contains(tools, t => t.Name == "run_pipeline");
        var result = await mcp.CallToolAsync("run_node",
            new JsonObject { ["type"] = "debug.mock", ["inputs"] = new JsonObject { ["in"] = "x" }, ["params"] = new JsonObject { ["kind"] = "text", ["delayMs"] = 10 } },
            CancellationToken.None);
        Assert.False(result.IsError);
        Assert.Contains("succeeded", result.Content[0].Text);
    }
}
