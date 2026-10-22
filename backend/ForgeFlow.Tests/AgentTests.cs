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
        public string LastToolResult = "";

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
