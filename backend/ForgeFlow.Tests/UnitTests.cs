using System.Text.Json.Nodes;
using ForgeFlow.Core;
using ForgeFlow.Engine;
using ForgeFlow.Nodes;
using ForgeFlow.Nodes.Builtin;

namespace ForgeFlow.Tests;

public class TemplateEngineTests
{
    static readonly JsonObject Scope = new()
    {
        ["inputs"] = new JsonObject
        {
            ["prompt"] = "a \"quoted\" knight",
            ["image"] = new AssetRef("ast_1", "image", "a.png", "image/png", "http://x/files/a.png", "C:/data/a.png", 10).ToJson(),
            ["list"] = new JsonArray("a", "b", "c"),
            ["part"] = new JsonObject { ["name"] = "hair", ["description"] = "long red hair" },
        },
        ["workflow"] = new JsonObject { ["animation"] = "walk" },
    };

    [Theory]
    [InlineData("Hello {{inputs.prompt}}", "Hello a \"quoted\" knight")]
    [InlineData("{\"p\":\"{{inputs.prompt | escape}}\"}", "{\"p\":\"a \\\"quoted\\\" knight\"}")]
    [InlineData("{{inputs.image}}", "C:/data/a.png")]
    [InlineData("{{inputs.image | url}}", "http://x/files/a.png")]
    [InlineData("{{inputs.list | join:\", \"}}", "a, b, c")]
    [InlineData("{{inputs.list | first}}", "a")]
    [InlineData("{{inputs.list.length}}", "3")]
    [InlineData("{{inputs.part.description}} / {{workflow.animation}}", "long red hair / walk")]
    [InlineData("{{inputs.missing | default:\"none\"}}", "none")]
    [InlineData("{{inputs.list | json}}", "[\"a\",\"b\",\"c\"]")]
    public void Renders(string template, string expected) => Assert.Equal(expected, TemplateEngine.Render(template, Scope));

    [Fact]
    public void RenderValueKeepsJsonType()
    {
        var v = TemplateEngine.RenderValue("{{inputs.list}}", Scope);
        Assert.IsType<JsonArray>(v);
    }
}

public class GraphTests
{
    static WorkflowGraph Chain(params (string From, string To)[] edges)
    {
        var g = new WorkflowGraph();
        foreach (var id in edges.SelectMany(e => new[] { e.From, e.To }).Distinct()) g.Nodes.Add(new NodeDef { Id = id, Type = "debug.mock" });
        foreach (var (f, t) in edges) g.Edges.Add(new EdgeDef { Id = f + t, Source = f, SourcePort = "out", Target = t, TargetPort = "in" });
        return g;
    }

    [Fact]
    public void OrdersTopologically()
    {
        var order = Chain(("a", "b"), ("b", "c"), ("a", "c")).TopologicalOrder();
        Assert.Equal(["a", "b", "c"], order);
    }

    [Fact]
    public void RejectsCycles() => Assert.Throws<InvalidOperationException>(() => Chain(("a", "b"), ("b", "a")).TopologicalOrder());

    [Fact]
    public void DownstreamIncludesSelf()
    {
        var g = Chain(("a", "b"), ("b", "c"), ("x", "c"));
        Assert.Equal(new HashSet<string> { "b", "c" }, g.DownstreamOf("b"));
    }
}

public class ImageMathTests
{
    /// <summary>A white sheet with two "buttons": rounded-ish dark borders and a flat fill, which is what 9-slice expects.</summary>
    static RgbaImage Sheet()
    {
        var img = new RgbaImage(200, 100, new Rgba(255, 255, 255, 255));
        void Button(int x0, int y0, int w, int h)
        {
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                {
                    var edge = x < 6 || y < 6 || x >= w - 6 || y >= h - 6;
                    img[x0 + x, y0 + y] = edge ? new Rgba(40, 20, 10, 255) : new Rgba(200, 120, 40, 255);
                }
        }
        Button(10, 10, 80, 40);
        Button(110, 30, 60, 50);
        return img;
    }

    [Fact]
    public void FindsSeparateElements()
    {
        var rects = ImageMath.FindElements(Sheet(), 20, 10, 0);
        Assert.Equal(2, rects.Count);
        Assert.Equal(new Rect(10, 10, 80, 40), rects[0]);
        Assert.Equal(new Rect(110, 30, 60, 50), rects[1]);
    }

    [Fact]
    public void EstimatesNineSliceBorders()
    {
        var piece = Sheet().Crop(new Rect(10, 10, 80, 40));
        var b = ImageMath.NineSliceBorder(piece);
        Assert.Equal(new ImageMath.Border(6, 6, 6, 6), b);
    }

    [Fact]
    public void KeysOutFlatBackground()
    {
        var img = Sheet();
        ImageMath.KeyOutBackground(img, 10);
        Assert.Equal(0, img[0, 0].A);
        Assert.Equal(255, img[50, 30].A); // inside a button
    }

    [Fact]
    public void PngRoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ff-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, Sheet().ToPng());
        var back = RgbaImage.Load(path);
        Assert.Equal(200, back.Width);
        Assert.Equal(new Rgba(200, 120, 40, 255), back[50, 30]);
        File.Delete(path);
    }
}

public class LlmParsingTests
{
    [Theory]
    [InlineData("[\"a\",\"b\"]", 2)]
    [InlineData("Here you go:\n```json\n[{\"name\":\"hair\"},{\"name\":\"boots\"}]\n```", 2)]
    [InlineData("{\"parts\": [1,2,3]}", 3)]
    [InlineData("1. red potion\n2. blue potion\n- gold coin", 3)]
    public void ExtractsItems(string text, int count)
    {
        var json = LlmChatNode.ExtractJson(text);
        Assert.Equal(count, LlmChatNode.ToItems(json, text).Count);
    }

    [Fact]
    public void GlbIsValidHeader()
    {
        var glb = GlbWriter.Cube();
        Assert.Equal("glTF", System.Text.Encoding.ASCII.GetString(glb, 0, 4));
        Assert.Equal(glb.Length, BitConverter.ToInt32(glb, 8));
    }
}
