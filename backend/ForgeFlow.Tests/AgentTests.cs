using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace ForgeFlow.Tests;

/// <summary>
/// Full agent loop: agent.mcp → fake OpenAI-compatible LLM (tool call, then final answer) → real stdio MCP server process.
/// </summary>
[Collection("api")]
public class AgentTests(ForgeFactory factory)
{
    readonly HttpClient _http = factory.CreateClient();

    [Fact]
    public async Task McpAgentCallsToolsAndCollectsFiles()
    {
        await using var llm = await FakeLlm.StartAsync();

        var provider = await (await _http.PostAsJsonAsync("/api/providers", new { name = "fake-llm", type = "openai", baseUrl = llm.BaseUrl, apiKey = "x", defaultModel = "fake" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var script = Path.Combine(AppContext.BaseDirectory, "fake-mcp-server.ps1");
        await _http.PostAsJsonAsync("/api/mcp-servers", new
        {
            name = "fake-tools", transport = "stdio", command = "powershell",
            args = new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script },
        });

        var test = await (await _http.PostAsync($"/api/mcp-servers/{(await FindMcp("fake-tools"))}/test", null)).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(test!["ok"]!.GetValue<bool>(), test.ToJsonString());

        var run = await (await _http.PostAsJsonAsync("/api/nodes/agent.mcp/run", new
        {
            inputs = new { prompt = "make the file" },
            @params = new { provider = provider!["id"]!.GetValue<string>(), mcpServers = new[] { "fake-tools" }, maxSteps = 5 },
            wait = true, waitSeconds = 90,
        })).Content.ReadFromJsonAsync<JsonObject>();

        var node = run!["nodes"]!["node"]!;
        Assert.True(run["status"]!.GetValue<string>() == "succeeded", node.ToJsonString());
        Assert.Equal("Done: the file is ready.", node["outputs"]!["text"]!.GetValue<string>());
        var files = node["outputs"]!["files"]!.AsArray();
        Assert.Single(files);
        Assert.Equal("hello from the agent", File.ReadAllText(files[0]!["path"]!.GetValue<string>()));
        Assert.Equal(2, llm.Calls);
        Assert.Contains("wrote 20 chars", llm.LastToolResult);
    }

    [Fact]
    public async Task RerunWithEditedGraphReusesUnchangedGenerations()
    {
        await using var llm = await FakeLlm.StartAsync();
        var provider = await (await _http.PostAsJsonAsync("/api/providers", new { name = "fake-images", type = "openai", baseUrl = llm.BaseUrl, apiKey = "x" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var pid = provider!["id"]!.GetValue<string>();

        object Graph(string previewPort) => new
        {
            nodes = new object[]
            {
                new { id = "gen", type = "image.generate", @params = new { provider = pid, prompt = "logo", count = 2 } },
                new { id = "view", type = "output.preview", @params = new { } },
            },
            edges = new[] { new { id = "e", source = "gen", sourcePort = previewPort, target = "view", targetPort = "value" } },
        };
        var wf = await (await _http.PostAsJsonAsync("/api/workflows", new { name = "rerun graph test", graph = Graph("image") })).Content.ReadFromJsonAsync<JsonObject>();
        var first = await (await _http.PostAsJsonAsync($"/api/workflows/{wf!["id"]}/run", new { wait = true })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("succeeded", first!["status"]!.GetValue<string>());
        Assert.IsType<JsonObject>(first["nodes"]!["view"]!["outputs"]!["value"]); // "image" = the first one only
        var calls = llm.ImageCalls;

        // Rewire the preview to "images" and rerun from it with the edited graph: nothing is regenerated.
        var rerun = await (await _http.PostAsJsonAsync($"/api/runs/{first["id"]}/rerun", new { fromNodeId = "view", graph = Graph("images") }))
            .Content.ReadFromJsonAsync<JsonObject>();
        JsonObject? done = null;
        for (var i = 0; i < 100 && done?["status"]?.GetValue<string>() is not ("succeeded" or "failed"); i++)
        {
            await Task.Delay(100);
            done = await _http.GetFromJsonAsync<JsonObject>($"/api/runs/{rerun!["id"]}");
        }
        Assert.Equal("succeeded", done!["status"]!.GetValue<string>());
        Assert.True(done["nodes"]!["gen"]!["cached"]!.GetValue<bool>());
        Assert.Equal(2, done["nodes"]!["view"]!["outputs"]!["value"]!.AsArray().Count);
        Assert.Equal(calls, llm.ImageCalls);

        // A changed generation setting is not reused.
        var changed = new
        {
            nodes = new object[]
            {
                new { id = "gen", type = "image.generate", @params = new { provider = pid, prompt = "other logo", count = 2 } },
                new { id = "view", type = "output.preview", @params = new { } },
            },
            edges = new[] { new { id = "e", source = "gen", sourcePort = "images", target = "view", targetPort = "value" } },
        };
        var rerun2 = await (await _http.PostAsJsonAsync($"/api/runs/{first["id"]}/rerun", new { fromNodeId = "view", graph = changed }))
            .Content.ReadFromJsonAsync<JsonObject>();
        done = null;
        for (var i = 0; i < 100 && done?["status"]?.GetValue<string>() is not ("succeeded" or "failed"); i++)
        {
            await Task.Delay(100);
            done = await _http.GetFromJsonAsync<JsonObject>($"/api/runs/{rerun2!["id"]}");
        }
        Assert.Equal("succeeded", done!["status"]!.GetValue<string>());
        Assert.False(done["nodes"]!["gen"]!["cached"]!.GetValue<bool>());
        Assert.Equal(calls + 1, llm.ImageCalls);
    }

    [Fact]
    public async Task NodeCacheSkipsUnchangedGenerations()
    {
        await using var llm = await FakeLlm.StartAsync();
        var provider = await (await _http.PostAsJsonAsync("/api/providers", new { name = "fake-cache", type = "openai", baseUrl = llm.BaseUrl, apiKey = "x" }))
            .Content.ReadFromJsonAsync<JsonObject>();
        var pid = provider!["id"]!.GetValue<string>();
        object Graph(string prompt, bool? cache = null) => new
        {
            nodes = new object[]
            {
                new { id = "txt", type = "input.text", @params = new { name = "prompt", value = prompt } },
                new { id = "gen", type = "image.generate", cache, @params = new { provider = pid, count = 1 } },
                new { id = "mock", type = "debug.mock", @params = new { kind = "text", delayMs = 10 } },
                new { id = "view", type = "output.preview", @params = new { } },
            },
            edges = new[]
            {
                new { id = "e1", source = "txt", sourcePort = "text", target = "gen", targetPort = "prompt" },
                new { id = "e2", source = "gen", sourcePort = "image", target = "mock", targetPort = "in" },
                new { id = "e3", source = "mock", sourcePort = "out", target = "view", targetPort = "value" },
            },
        };
        var wf = await (await _http.PostAsJsonAsync("/api/workflows", new { name = "cache test", graph = Graph("cat") })).Content.ReadFromJsonAsync<JsonObject>();
        var wid = wf!["id"]!.GetValue<string>();
        async Task<JsonObject> Run() => (await (await _http.PostAsJsonAsync($"/api/workflows/{wid}/run", new { wait = true })).Content.ReadFromJsonAsync<JsonObject>())!;
        static bool Cached(JsonObject run, string node) => run["nodes"]![node]!["cached"]!.GetValue<bool>();
        async Task Save(object graph) => await _http.PutAsJsonAsync($"/api/workflows/{wid}", new { graph });

        var r1 = await Run();
        Assert.Equal("succeeded", r1["status"]!.GetValue<string>());
        Assert.Equal(1, llm.ImageCalls);

        // Plain run again: nothing changed, nothing is generated.
        var r2 = await Run();
        Assert.True(Cached(r2, "gen") && Cached(r2, "mock"));
        Assert.Equal(1, llm.ImageCalls);

        // A new prompt changes the generator's inputs; downstream follows because its input changed too.
        await Save(Graph("dog"));
        var r3 = await Run();
        Assert.False(Cached(r3, "gen"));
        Assert.False(Cached(r3, "mock"));
        Assert.Equal(2, llm.ImageCalls);

        // Cache switched off for the generator: it runs every time.
        await Save(Graph("dog", cache: false));
        var r4 = await Run();
        Assert.False(Cached(r4, "gen"));
        Assert.Equal(3, llm.ImageCalls);

        // Back on, then "Clear node" on the generator also clears what depends on it.
        await Save(Graph("dog"));
        Assert.True(Cached(await Run(), "gen")); // the "dog" result from r3 is still cached
        var cleared = await (await _http.PostAsJsonAsync($"/api/workflows/{wid}/cache/clear", new { nodeIds = new[] { "gen" } })).Content.ReadFromJsonAsync<JsonObject>();
        var clearedIds = cleared!["nodes"]!.AsArray().Select(x => x!.GetValue<string>()).ToHashSet();
        Assert.Contains("gen", clearedIds);
        Assert.Contains("mock", clearedIds);
        Assert.DoesNotContain("txt", clearedIds);
        var r6 = await Run();
        Assert.False(Cached(r6, "gen"));
        Assert.Equal(4, llm.ImageCalls);

        // "Rerun from node" executes that node even though its cache matches.
        var rerun = await (await _http.PostAsJsonAsync($"/api/runs/{r6["id"]}/rerun", new { fromNodeId = "gen" })).Content.ReadFromJsonAsync<JsonObject>();
        JsonObject? done = null;
        for (var i = 0; i < 100 && done?["status"]?.GetValue<string>() is not ("succeeded" or "failed"); i++)
        {
            await Task.Delay(100);
            done = await _http.GetFromJsonAsync<JsonObject>($"/api/runs/{rerun!["id"]}");
        }
        Assert.Equal("succeeded", done!["status"]!.GetValue<string>());
        Assert.Equal(5, llm.ImageCalls);
    }

    async Task<string> FindMcp(string name)
    {
        var list = await _http.GetFromJsonAsync<JsonArray>("/api/mcp-servers");
        return list!.First(s => s!["name"]!.GetValue<string>() == name)!["id"]!.GetValue<string>();
    }

    /// <summary>OpenAI-compatible chat endpoint scripted for two turns.</summary>
    sealed class FakeLlm : IAsyncDisposable
    {
        WebApplication _app = null!;
        public string BaseUrl { get; private set; } = "";
        public int Calls;
        public int ImageCalls;
        public string LastToolResult = "";

        // 1×1 PNG.
        const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

        public static async Task<FakeLlm> StartAsync()
        {
            var fake = new FakeLlm();
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.MapPost("/v1/chat/completions", async (HttpContext ctx) =>
            {
                var body = (await JsonNode.ParseAsync(ctx.Request.Body))!.AsObject();
                Interlocked.Increment(ref fake.Calls);
                var messages = body["messages"]!.AsArray();
                var last = messages[^1]!;
                if (last["role"]!.GetValue<string>() == "user")
                {
                    // The task text carries the output folder; parse it like a model would.
                    var task = last["content"]!.GetValue<string>();
                    var outDir = task.Split('\n').First(l => l.Contains("save every result file into:")).Split("into:")[1].Trim();
                    var args = new JsonObject { ["path"] = Path.Combine(outDir, "result.txt"), ["text"] = "hello from the agent" }.ToJsonString();
                    return Results.Json(Completion(new JsonObject
                    {
                        ["role"] = "assistant", ["content"] = null,
                        ["tool_calls"] = new JsonArray(new JsonObject
                        {
                            ["id"] = "call_1", ["type"] = "function",
                            ["function"] = new JsonObject { ["name"] = "make_file", ["arguments"] = args },
                        }),
                    }, "tool_calls"));
                }
                fake.LastToolResult = last["content"]!.GetValue<string>();
                return Results.Json(Completion(new JsonObject { ["role"] = "assistant", ["content"] = "Done: the file is ready." }, "stop"));
            });
            app.MapPost("/v1/images/generations", async (HttpContext ctx) =>
            {
                var body = (await JsonNode.ParseAsync(ctx.Request.Body))!;
                Interlocked.Increment(ref fake.ImageCalls);
                var n = body["n"]?.GetValue<int>() ?? 1;
                return Results.Json(new JsonObject
                {
                    ["data"] = new JsonArray(Enumerable.Range(0, n).Select(_ => (JsonNode)new JsonObject { ["b64_json"] = Png }).ToArray()),
                });
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
            fake._app = app;
            fake.BaseUrl = address + "/v1";
            return fake;
        }

        static JsonObject Completion(JsonObject message, string finish) => new()
        {
            ["id"] = "x", ["object"] = "chat.completion",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish }),
        };

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
