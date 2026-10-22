using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;

namespace ForgeFlow.Providers;

/// <summary>Claude through the official Anthropic C# SDK (beta Messages path, so refusal fallbacks can be enabled).</summary>
public sealed class AnthropicChatSession : ILlmSession
{
    // Server-side fallback ("default" routing): a declined request is re-served by a fallback model in the same call.
    const string FallbackBeta = "server-side-fallback-2026-07-01";
    const string FallbackModel = "claude-opus-4-8";

    readonly AnthropicClient _client;
    readonly LlmOptions _o;
    readonly bool _fallbacks;
    readonly List<BetaMessageParam> _messages = [];
    readonly List<BetaToolUnion> _tools;

    public AnthropicChatSession(ProviderInfo p, LlmOptions o)
    {
        _o = o;
        _client = string.IsNullOrEmpty(p.BaseUrl)
            ? new AnthropicClient { ApiKey = p.RequireKey() }
            : new AnthropicClient { ApiKey = p.RequireKey(), BaseUrl = p.BaseUrl };
        _fallbacks = p.Extra["fallbacks"]?.GetValue<bool>() ?? true;
        _tools = o.Tools.Select(ToTool).ToList();
    }

    static BetaToolUnion ToTool(LlmTool t)
    {
        var props = new Dictionary<string, JsonElement>();
        if (t.InputSchema["properties"] is JsonObject po)
            foreach (var (k, v) in po) props[k] = JsonSerializer.SerializeToElement(v);
        var required = t.InputSchema["required"] is JsonArray ra ? ra.Select(x => x!.GetValue<string>()).ToList() : [];
        return new BetaTool
        {
            Name = t.Name,
            Description = t.Description,
            InputSchema = new() { Properties = props, Required = required },
        };
    }

    public void AddUser(string text, IReadOnlyList<LlmImage>? images = null)
    {
        List<BetaContentBlockParam> content = [];
        foreach (var img in images ?? [])
            content.Add(new BetaImageBlockParam
            {
                Source = new BetaBase64ImageSource { Data = img.Base64, MediaType = img.MediaType },
            });
        content.Add(new BetaTextBlockParam { Text = text });
        _messages.Add(new BetaUserMessageParam { Content = content });
    }

    public void AddToolResults(IReadOnlyList<LlmToolResult> results)
    {
        List<BetaContentBlockParam> content = [];
        foreach (var r in results)
            content.Add(new BetaToolResultBlockParam { ToolUseID = r.Id, Content = r.Content, IsError = r.IsError });
        _messages.Add(new BetaUserMessageParam { Content = content });
    }

    public async Task<LlmTurn> NextAsync(CancellationToken ct)
    {
        var parameters = new MessageCreateParams
        {
            Model = _o.Model,
            MaxTokens = _o.MaxTokens,
            Messages = [.. _messages],
        };
        if (!string.IsNullOrWhiteSpace(_o.System)) parameters = parameters with { System = _o.System };
        if (_tools.Count > 0) parameters = parameters with { Tools = _tools };
        if (_fallbacks) parameters = parameters with { Betas = [FallbackBeta], Fallbacks = new Default() };

        BetaMessage resp = await _client.Beta.Messages.Create(parameters, cancellationToken: ct);

        if (resp.StopReason == "refusal")
            throw new InvalidOperationException($"Claude declined the request ({resp.StopDetails?.Category}): {resp.StopDetails?.Explanation}");

        // Echo the assistant turn back unchanged (thinking signatures must be preserved).
        List<BetaContentBlockParam> echo = [];
        var text = new System.Text.StringBuilder();
        var calls = new List<LlmToolCall>();
        foreach (var block in resp.Content)
        {
            if (block.TryPickText(out var t))
            {
                echo.Add(new BetaTextBlockParam { Text = t.Text });
                text.Append(t.Text);
            }
            else if (block.TryPickThinking(out var th))
            {
                echo.Add(new BetaThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
            }
            else if (block.TryPickRedactedThinking(out var rt))
            {
                echo.Add(new BetaRedactedThinkingBlockParam { Data = rt.Data });
            }
            else if (block.TryPickToolUse(out var tu))
            {
                echo.Add(new BetaToolUseBlockParam { ID = tu.ID, Name = tu.Name, Input = tu.Input });
                var args = new JsonObject();
                foreach (var (k, v) in tu.Input) args[k] = JsonNode.Parse(v.GetRawText());
                calls.Add(new LlmToolCall(tu.ID, tu.Name, args));
            }
        }
        _messages.Add(new BetaAssistantMessageParam { Content = echo });
        return new LlmTurn(text.ToString(), calls, resp.StopReason?.ToString() ?? "end_turn");
    }
}
