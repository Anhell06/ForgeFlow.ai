using Anthropic.Models.Beta.Messages;

namespace ForgeFlow.Tests;

public class AnthropicParamsTests
{
    [Fact]
    public void FallbacksSerializeAsDefault()
    {
        var p = new MessageCreateParams
        {
            Model = "claude-opus-5-5",
            MaxTokens = 16,
            Messages = [new BetaUserMessageParam { Content = "hi" }],
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
        };
        var text = p.ToString();
        Assert.Contains("\"fallbacks\"", text);
        Assert.Contains("\"default\"", text);
    }
}
