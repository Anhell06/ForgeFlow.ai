using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ForgeFlow.Agents;
using ForgeFlow.Core;
using ForgeFlow.Data;
using ForgeFlow.Providers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileSystemGlobbing;

namespace ForgeFlow.Nodes.Builtin;

public static class AgentShared
{
    public static readonly PortSpec[] Inputs =
    [
        new("prompt", Kinds.Text), new("model", Kinds.Model), new("image", Kinds.Image), new("video", Kinds.Video),
        new("files", Kinds.Any, List: true), new("context", Kinds.Any),
    ];

    public static readonly PortSpec[] Outputs =
    [
        new("text", Kinds.Text), new("json", Kinds.Json), new("files", Kinds.File, List: true),
        new("model", Kinds.Model), new("image", Kinds.Image), new("video", Kinds.Video),
    ];

    /// <summary>A description of the connected inputs with local paths, appended to the agent's task.</summary>
    public static string DescribeInputs(NodeContext ctx, string outDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine().AppendLine("---").AppendLine("Pipeline context (from ForgeFlow):");
        foreach (var port in new[] { "model", "image", "video" })
            if (ctx.InputAsset(port) is { } a) sb.AppendLine($"- input {port}: {a.Path}");
        var files = ctx.InputAssets("files");
        if (files.Count > 0)
        {
            sb.AppendLine("- input files:");
            foreach (var f in files) sb.AppendLine($"  - {f.Path} ({f.Kind})");
        }
        if (ctx.Input("context") is { } c) sb.AppendLine("- context: " + Json.AsText(c));
        sb.AppendLine($"- save every result file into: {outDir}");
        return sb.ToString();
    }

    public static async Task<Dictionary<string, JsonNode?>> CollectOutputsAsync(NodeContext ctx, string text, JsonNode? json,
        IEnumerable<string> extraFiles, string outDir, DateTime startedAt)
    {
        var files = new List<AssetRef>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = Directory.Exists(outDir) ? Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories) : [];
        foreach (var path in paths.Concat(extraFiles))
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full) || !seen.Add(full)) continue;
            if (File.GetLastWriteTimeUtc(full) < startedAt.AddSeconds(-2) && !full.StartsWith(outDir, StringComparison.OrdinalIgnoreCase)) continue;
            files.Add(await ctx.SaveFileAsync(full));
        }
        ctx.Log($"collected {files.Count} file(s)");
        return new Dictionary<string, JsonNode?>
        {
            ["text"] = JsonValue.Create(text),
            ["json"] = json,
            ["files"] = new JsonArray(files.Select(f => (JsonNode)f.ToJson()).ToArray()),
            ["model"] = files.FirstOrDefault(f => f.Kind == "model" && f.Name.EndsWith(".glb"))?.ToJson() ?? files.FirstOrDefault(f => f.Kind == "model")?.ToJson(),
            ["image"] = files.FirstOrDefault(f => f.Kind == "image")?.ToJson(),
            ["video"] = files.FirstOrDefault(f => f.Kind == "video")?.ToJson(),
        };
    }

    public static IEnumerable<string> Glob(string root, IEnumerable<string> patterns)
    {
        var list = patterns.ToList();
        if (list.Count == 0 || !Directory.Exists(root)) return [];
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(list);
        return matcher.GetResultsInFullPath(root);
    }
}

/// <summary>
/// Tool-using agent: an LLM (GPT, Claude, or a local model via Ollama/LM Studio) drives MCP servers such as Blender MCP.
/// </summary>
public partial class McpAgentNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("agent.mcp", "Agent + MCP tools", "AI · Agents",
        "An LLM that uses MCP servers as tools — e.g. a local GPT driving Blender through Blender MCP to import, rig and animate a model. " +
        "Input file paths are given to the agent; files it saves to the output folder (or registers) become outputs.",
        AgentShared.Inputs, AgentShared.Outputs,
        [
            P.Provider("openai", "anthropic"),
            P.Model(help: "A model with tool calling: gpt-5, claude-opus-5-5, gpt-oss:20b, qwen3…"),
            new("mcpServers", "mcpServers", "MCP servers", new JsonArray(), Help: "Names or ids from Settings → MCP servers"),
            new("system", "text", "System prompt", JsonValue.Create(
                "You are a technical 3D artist operating tools. Work step by step, verify results (e.g. take a viewport screenshot), " +
                "and save final files into the output folder you are given. Finish with a short summary.")),
            P.Prompt("{{inputs.prompt}}", "Task"),
            new("maxSteps", "number", "Max tool steps", JsonValue.Create(40)),
            new("timeoutMin", "number", "Timeout (min)", JsonValue.Create(30)),
        ], "#a855f7");

    const string RegisterTool = "forgeflow_register_output";

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var started = DateTime.UtcNow;
        var provider = await ctx.Providers.ResolveAsync(ctx.Str("provider"), ["openai", "anthropic"], ctx.Ct);
        var model = ctx.Str("model") is { Length: > 0 } m ? m : provider.DefaultModel ?? throw new NodeException("Set a model");
        var outDir = Directory.CreateDirectory(Path.Combine(ctx.WorkDir, "out")).FullName;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, ctx.Num("timeoutMin", 30))));
        var ct = timeout.Token;

        var serverRefs = ctx.StringList("mcpServers");
        var clients = new List<McpClient>();
        var toolMap = new Dictionary<string, (McpClient Client, string Tool)>();
        var tools = new List<LlmTool>();
        try
        {
            if (serverRefs.Count > 0)
            {
                var dbf = ctx.Services.GetRequiredService<IDbContextFactory<AppDb>>();
                await using var db = await dbf.CreateDbContextAsync(ct);
                var servers = await db.McpServers.Where(s => serverRefs.Contains(s.Id) || serverRefs.Contains(s.Name)).ToListAsync(ct);
                foreach (var missing in serverRefs.Where(r => servers.All(s => s.Id != r && s.Name != r)))
                    throw new NodeException($"MCP server '{missing}' is not configured");
                foreach (var s in servers)
                {
                    ctx.Log($"connecting MCP '{s.Name}'…");
                    var client = await McpClient.ConnectAsync(s, ctx.Log, ct, ctx.Http);
                    clients.Add(client);
                    var prefix = servers.Count > 1 ? Slug(s.Name) + "__" : "";
                    foreach (var t in await client.ListToolsAsync(ct))
                    {
                        var name = Sanitize(prefix + t.Name);
                        toolMap[name] = (client, t.Name);
                        tools.Add(new LlmTool(name, t.Description, t.InputSchema));
                    }
                    ctx.Log($"MCP '{s.Name}': {toolMap.Count(kv => kv.Value.Client == client)} tools");
                }
            }
            tools.Add(new LlmTool(RegisterTool, "Register a file you created as a result of this pipeline step.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["path"] = new JsonObject { ["type"] = "string", ["description"] = "Absolute path of the file" },
                    },
                    ["required"] = new JsonArray("path"),
                }));

            var session = LlmFactory.Create(provider, new LlmOptions(model, ctx.Str("system"), tools), ctx.Http);
            var task = (ctx.Str("prompt") ?? "") + AgentShared.DescribeInputs(ctx, outDir);
            var images = new List<LlmImage>();
            if (ctx.InputAsset("image") is { Kind: "image" } img)
                images.Add(new LlmImage(img.Mime, Convert.ToBase64String(await File.ReadAllBytesAsync(img.Path, ct))));
            session.AddUser(task, images);

            var registered = new List<string>();
            var transcript = new JsonArray();
            var finalText = "";
            var maxSteps = Math.Max(1, ctx.Int("maxSteps", 40));
            for (var step = 1; step <= maxSteps; step++)
            {
                ctx.Progress(Math.Min(0.95, step / (double)maxSteps), $"step {step}");
                var turn = await session.NextAsync(ct);
                if (!string.IsNullOrWhiteSpace(turn.Text))
                {
                    finalText = turn.Text;
                    ctx.Log("agent: " + Short(turn.Text, 400));
                }
                transcript.Add(new JsonObject { ["step"] = step, ["text"] = turn.Text, ["tools"] = new JsonArray(turn.ToolCalls.Select(c => (JsonNode)c.Name).ToArray()) });
                if (turn.ToolCalls.Count == 0) break;

                var results = new List<LlmToolResult>();
                foreach (var call in turn.ToolCalls)
                    results.Add(await ExecuteToolAsync(ctx, call, toolMap, registered, outDir, ct));
                session.AddToolResults(results);
                if (step == maxSteps) ctx.Log($"stopped after {maxSteps} steps");
            }

            var outputs = await AgentShared.CollectOutputsAsync(ctx, finalText, transcript, registered, outDir, started);
            return outputs;
        }
        finally
        {
            foreach (var c in clients) await c.DisposeAsync();
        }
    }

    static async Task<LlmToolResult> ExecuteToolAsync(NodeContext ctx, LlmToolCall call, Dictionary<string, (McpClient Client, string Tool)> toolMap,
        List<string> registered, string outDir, CancellationToken ct)
    {
        ctx.Log($"→ {call.Name} {Short(call.Arguments.ToJsonString(), 300)}");
        try
        {
            if (call.Name == RegisterTool)
            {
                var path = call.Arguments.Str("path") ?? "";
                if (!File.Exists(path)) return new LlmToolResult(call.Id, $"File not found: {path}", true);
                registered.Add(path);
                return new LlmToolResult(call.Id, "Registered " + path);
            }
            if (!toolMap.TryGetValue(call.Name, out var target))
                return new LlmToolResult(call.Id, $"Unknown tool {call.Name}", true);

            var result = await target.Client.CallToolAsync(target.Tool, call.Arguments, ct);
            var sb = new StringBuilder();
            foreach (var c in result.Content)
            {
                if (c.Type == "text") sb.AppendLine(c.Text);
                else if (c.Data is not null)
                {
                    // Images from tools (viewport screenshots, renders) are saved so they show up as outputs.
                    var ext = MediaKinds.ExtensionFromMime(c.MimeType) ?? ".bin";
                    var path = Path.Combine(outDir, $"{call.Name}-{DateTime.UtcNow:HHmmssfff}{ext}");
                    await File.WriteAllBytesAsync(path, Convert.FromBase64String(c.Data), ct);
                    sb.AppendLine($"[{c.Type} saved to {path}]");
                }
            }
            var text = sb.ToString().Trim();
            ctx.Log($"← {Short(text, 300)}");
            return new LlmToolResult(call.Id, text.Length > 20000 ? text[..20000] + "…(truncated)" : text, result.IsError);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ctx.Log($"tool error: {ex.Message}");
            return new LlmToolResult(call.Id, ex.Message, true);
        }
    }

    static string Short(string s, int n) => s.Length > n ? s[..n] + "…" : s;

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex InvalidToolChars();

    static string Sanitize(string name)
    {
        var clean = InvalidToolChars().Replace(name, "_");
        return clean.Length > 64 ? clean[..64] : clean;
    }

    static string Slug(string name) => Sanitize(name.ToLowerInvariant().Replace(' ', '_'));
}

/// <summary>
/// Runs a local coding agent CLI — Claude Code, Codex or any command — inside a project folder (e.g. a Unity project).
/// </summary>
public class CliAgentNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("agent.cli", "Agent CLI (Claude Code, Codex…)", "AI · Agents",
        "Runs a local agent CLI in a working folder: Claude Code in your Unity project, Codex in a repo, or any script. " +
        "The task is sent on stdin with input file paths appended. Files matching 'Collect' (or saved to the output folder) become outputs.",
        AgentShared.Inputs, AgentShared.Outputs,
        [
            new("preset", "select", "Preset", JsonValue.Create("claude-code"), Options: ["claude-code", "codex", "custom"]),
            new("command", "string", "Command", Help: "Empty = preset default (claude / codex)"),
            new("args", "text", "Arguments (one per line)", Help:
                "Empty = preset default. Claude Code: -p --output-format json --permission-mode acceptEdits. Templates allowed."),
            new("workdir", "string", "Working folder", Help: "e.g. D:/Unity/MyGame — the agent runs here"),
            P.Prompt("{{inputs.prompt}}", "Task"),
            new("collect", "text", "Collect files (globs, relative to working folder)", JsonValue.Create(""),
                Help: "e.g. Assets/UI/Generated/**/*.png — only files changed during this run are collected"),
            P.Json("env", "Environment JSON", "Extra environment variables; values may be env:VAR"),
            new("timeoutMin", "number", "Timeout (min)", JsonValue.Create(30)),
        ], "#a855f7");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var started = DateTime.UtcNow;
        var preset = ctx.Str("preset") ?? "claude-code";
        var outDir = Directory.CreateDirectory(Path.Combine(ctx.WorkDir, "out")).FullName;
        var workdir = ctx.Str("workdir") is { Length: > 0 } wd ? wd : outDir;
        if (!Directory.Exists(workdir)) throw new NodeException($"Working folder not found: {workdir}");

        var command = ctx.Str("command") is { Length: > 0 } c ? c : preset switch { "codex" => "codex", "claude-code" => "claude", _ => "" };
        if (string.IsNullOrEmpty(command)) throw new NodeException("Set a command");
        var args = ctx.StringList("args");
        if (args.Count == 0)
            args = preset switch
            {
                "claude-code" => ["-p", "--output-format", "json", "--permission-mode", "acceptEdits", "--add-dir", outDir],
                "codex" => ["exec", "--full-auto", "-"],
                _ => [],
            };
        if (CommandResolver.Resolve(command) is null)
            throw new NodeException($"'{command}' not found on PATH. Install it or set the full path in Command.");

        var task = (ctx.Str("prompt") ?? "") + AgentShared.DescribeInputs(ctx, outDir);
        var psi = CommandResolver.Build(command, args);
        psi.WorkingDirectory = workdir;
        psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["FORGEFLOW_OUTPUT_DIR"] = outDir;
        psi.Environment["FORGEFLOW_RUN_ID"] = ctx.RunId;
        foreach (var (k, v) in ctx.JsonObjectParam("env")) psi.Environment[k] = ProviderRegistry.ResolveSecret(Json.AsText(v));

        ctx.Log($"$ {command} {string.Join(' ', args)}  (in {workdir})");
        using var proc = Process.Start(psi) ?? throw new NodeException($"Could not start {command}");
        var stdout = new StringBuilder();
        var readOut = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync()) is not null)
            {
                stdout.AppendLine(line);
                if (preset != "claude-code") ctx.Log(line);
            }
        });
        var readErr = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync()) is not null)
                if (!string.IsNullOrWhiteSpace(line)) ctx.Log("stderr: " + line);
        });
        await proc.StandardInput.WriteAsync(task);
        proc.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ctx.Ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, ctx.Num("timeoutMin", 30))));
        try
        {
            await proc.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            proc.Kill(entireProcessTree: true);
            throw ctx.Ct.IsCancellationRequested ? new OperationCanceledException() : new NodeException("Agent timed out");
        }
        await Task.WhenAll(readOut, readErr);

        var output = stdout.ToString().Trim();
        var json = Json.Parse(output) ?? LlmChatNode.ExtractJson(output);
        var text = json?["result"] is JsonValue r && r.TryGetValue(out string? res) ? res! : output;
        if (json?["is_error"]?.GetValue<bool>() == true || proc.ExitCode != 0)
            throw new NodeException($"{command} exited with code {proc.ExitCode}: {(text.Length > 1500 ? text[..1500] + "…" : text)}");
        ctx.Log("result: " + (text.Length > 600 ? text[..600] + "…" : text));

        var collected = AgentShared.Glob(workdir, ctx.StringList("collect"));
        return await AgentShared.CollectOutputsAsync(ctx, text, json, collected, outDir, started);
    }
}
