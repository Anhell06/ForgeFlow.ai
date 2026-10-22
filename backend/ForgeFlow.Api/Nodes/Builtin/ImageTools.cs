using System.Text.Json.Nodes;
using ForgeFlow.Core;
using SkiaSharp;

namespace ForgeFlow.Nodes.Builtin;

public class ImageCollageNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("image.collage", "Collage / moodboard", "Image",
        "Lays images out on a grid — a moodboard from several generations, or a sprite sheet from frames.",
        [new("images", Kinds.Image, List: true)],
        [new("image", Kinds.Image)],
        [
            new("columns", "number", "Columns (0 = auto)", JsonValue.Create(0)),
            new("cell", "number", "Cell size (px)", JsonValue.Create(512)),
            new("gap", "number", "Gap (px)", JsonValue.Create(16)),
            new("background", "string", "Background", JsonValue.Create("#111111"), Help: "Hex color or 'transparent'"),
        ], "#0ea5e9");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var inputs = ctx.InputAssets("images").Where(a => a.Kind == "image").ToList();
        if (inputs.Count == 0) throw new NodeException("Connect at least one image");
        var cols = ctx.Int("columns", 0) is > 0 and var c ? c : (int)Math.Ceiling(Math.Sqrt(inputs.Count));
        var rows = (int)Math.Ceiling(inputs.Count / (double)cols);
        var cell = Math.Clamp(ctx.Int("cell", 512), 16, 4096);
        var gap = Math.Clamp(ctx.Int("gap", 16), 0, 512);
        var bgText = ctx.StrOr("background", "#111111");
        var bg = bgText == "transparent" ? SKColors.Transparent
            : SKColor.TryParse(bgText, out var parsed) ? parsed : throw new NodeException($"Bad color '{bgText}'");
        var png = RgbaImage.Collage(inputs.Select(i => i.Path).ToList(), cols, cell, gap, bg);
        return Out(("image", (await ctx.SaveBytesAsync(png, "collage.png")).ToJson()));
    }
}

public class ImageRemoveBackgroundNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("image.removeBackground", "Remove flat background", "Image",
        "Makes a flat/solid background transparent (flood fill from the borders). For photos use an AI model via fal/Replicate/ComfyUI.",
        [new("image", Kinds.Image)],
        [new("image", Kinds.Image)],
        [new("tolerance", "number", "Tolerance (0-255)", JsonValue.Create(24))], "#0ea5e9");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var src = ctx.RequireAsset("image");
        var img = RgbaImage.Load(src.Path);
        ImageMath.KeyOutBackground(img, ctx.Int("tolerance", 24));
        return Out(("image", (await ctx.SaveBytesAsync(img.ToPng(), Path.GetFileNameWithoutExtension(src.Name) + "-nobg.png")).ToJson()));
    }
}

public class ImageSliceNode : NodeBase
{
    public override NodeSpec Spec { get; } = new("image.slice", "Slice into elements (+9-slice)", "Image",
        "Cuts an image into pieces: a fixed grid, or auto-detected elements separated by background (UI sheets, moodboards, icon sets). " +
        "For each piece it estimates 9-slice borders (left/top/right/bottom) from where the middle stops changing.",
        [new("image", Kinds.Image)],
        [new("images", Kinds.Image, List: true), new("slices", Kinds.Json)],
        [
            new("mode", "select", "Mode", JsonValue.Create("auto"), Options: ["auto", "grid"]),
            new("rows", "number", "Grid rows", JsonValue.Create(2)),
            new("columns", "number", "Grid columns", JsonValue.Create(2)),
            new("tolerance", "number", "Background tolerance", JsonValue.Create(24)),
            new("minSize", "number", "Min element size (px)", JsonValue.Create(24)),
            new("padding", "number", "Padding around elements (px)", JsonValue.Create(2)),
            new("transparent", "bool", "Make background transparent", JsonValue.Create(true)),
        ], "#0ea5e9");

    public override async Task<Dictionary<string, JsonNode?>> ExecuteAsync(NodeContext ctx)
    {
        var src = ctx.RequireAsset("image");
        var img = RgbaImage.Load(src.Path);
        var tolerance = ctx.Int("tolerance", 24);
        var rects = ctx.Str("mode") == "grid"
            ? ImageMath.Grid(img.Width, img.Height, Math.Max(1, ctx.Int("rows", 2)), Math.Max(1, ctx.Int("columns", 2)))
            : ImageMath.FindElements(img, tolerance, Math.Max(4, ctx.Int("minSize", 24)), Math.Max(0, ctx.Int("padding", 2)));
        if (rects.Count == 0) throw new NodeException("No elements found — try grid mode or a higher tolerance");
        if (ctx.Bool("transparent", true)) ImageMath.KeyOutBackground(img, tolerance);

        var images = new List<AssetRef>();
        var slices = new JsonArray();
        var baseName = Path.GetFileNameWithoutExtension(src.Name);
        for (var i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            var piece = img.Crop(r);
            var border = ImageMath.NineSliceBorder(piece);
            var asset = await ctx.SaveBytesAsync(piece.ToPng(), $"{baseName}-{i:00}.png");
            images.Add(asset);
            slices.Add(new JsonObject
            {
                ["index"] = i, ["file"] = asset.Path, ["name"] = asset.Name,
                ["x"] = r.X, ["y"] = r.Y, ["width"] = r.Width, ["height"] = r.Height,
                ["border"] = new JsonObject { ["left"] = border.Left, ["top"] = border.Top, ["right"] = border.Right, ["bottom"] = border.Bottom },
                ["stretchable"] = border.Left + border.Right < r.Width && border.Top + border.Bottom < r.Height,
            });
        }
        ctx.Log($"{images.Count} element(s)");
        return Out(("images", ToArray(images)), ("slices", slices));
    }
}

public static class ImageMath
{
    public record struct Border(int Left, int Top, int Right, int Bottom);

    static int Diff(Rgba a, Rgba b) =>
        Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A)));

    /// <summary>Background = the most common border color (or transparency).</summary>
    public static Rgba BackgroundColor(RgbaImage img)
    {
        var counts = new Dictionary<uint, int>();
        void Add(Rgba p)
        {
            var key = p.A < 16 ? 0u : (uint)((p.R >> 3) << 10 | (p.G >> 3) << 5 | p.B >> 3) + 1;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        for (var x = 0; x < img.Width; x++) { Add(img[x, 0]); Add(img[x, img.Height - 1]); }
        for (var y = 0; y < img.Height; y++) { Add(img[0, y]); Add(img[img.Width - 1, y]); }
        var best = counts.MaxBy(kv => kv.Value).Key;
        if (best == 0) return new Rgba(0, 0, 0, 0);
        // Return an actual border pixel matching the winning bucket.
        for (var x = 0; x < img.Width; x++)
        {
            var p = img[x, 0];
            if (p.A >= 16 && (uint)((p.R >> 3) << 10 | (p.G >> 3) << 5 | p.B >> 3) + 1 == best) return p;
        }
        return img[0, 0];
    }

    static bool IsBackground(Rgba p, Rgba bg, int tol) => bg.A < 16 ? p.A < 16 + tol / 2 : Diff(p, bg) <= tol;

    public static void KeyOutBackground(RgbaImage img, int tolerance)
    {
        var bg = BackgroundColor(img);
        int w = img.Width, h = img.Height;
        var seen = new bool[w * h];
        var queue = new Queue<(int, int)>();
        for (var x = 0; x < w; x++) { queue.Enqueue((x, 0)); queue.Enqueue((x, h - 1)); }
        for (var y = 0; y < h; y++) { queue.Enqueue((0, y)); queue.Enqueue((w - 1, y)); }
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            if (x < 0 || y < 0 || x >= w || y >= h || seen[y * w + x]) continue;
            seen[y * w + x] = true;
            var p = img[x, y];
            if (!IsBackground(p, bg, tolerance)) continue;
            img[x, y] = new Rgba(p.R, p.G, p.B, 0);
            queue.Enqueue((x + 1, y)); queue.Enqueue((x - 1, y)); queue.Enqueue((x, y + 1)); queue.Enqueue((x, y - 1));
        }
    }

    public static List<Rect> Grid(int w, int h, int rows, int cols)
    {
        var list = new List<Rect>();
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                int x0 = c * w / cols, x1 = (c + 1) * w / cols, y0 = r * h / rows, y1 = (r + 1) * h / rows;
                list.Add(new Rect(x0, y0, x1 - x0, y1 - y0));
            }
        return list;
    }

    /// <summary>Connected components of non-background pixels, returned top-to-bottom, left-to-right.</summary>
    public static List<Rect> FindElements(RgbaImage img, int tolerance, int minSize, int padding)
    {
        var bg = BackgroundColor(img);
        int w = img.Width, h = img.Height;
        var fg = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
                fg[y * w + x] = !IsBackground(img[x, y], bg, tolerance);

        var visited = new bool[w * h];
        var rects = new List<Rect>();
        var stack = new Stack<int>();
        for (var start = 0; start < fg.Length; start++)
        {
            if (!fg[start] || visited[start]) continue;
            int minX = w, minY = h, maxX = 0, maxY = 0;
            stack.Push(start);
            visited[start] = true;
            while (stack.Count > 0)
            {
                var i = stack.Pop();
                int x = i % w, y = i / w;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                // Radius-2 neighbourhood so thin anti-aliased gaps inside one element do not split it.
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        var j = ny * w + nx;
                        if (fg[j] && !visited[j]) { visited[j] = true; stack.Push(j); }
                    }
            }
            if (maxX - minX + 1 < minSize || maxY - minY + 1 < minSize) continue;
            var rx = Math.Max(0, minX - padding);
            var ry = Math.Max(0, minY - padding);
            rects.Add(new Rect(rx, ry, Math.Min(w, maxX + padding + 1) - rx, Math.Min(h, maxY + padding + 1) - ry));
        }
        var rowHeight = Math.Max(1, minSize);
        return rects.OrderBy(r => r.Y / rowHeight).ThenBy(r => r.X).ToList();
    }

    /// <summary>
    /// Estimates 9-slice borders: the stretchable band is where columns (rows) equal the center column (row).
    /// Corners and edges are what differs from the middle.
    /// </summary>
    public static Border NineSliceBorder(RgbaImage img, int tolerance = 10)
    {
        int w = img.Width, h = img.Height;
        if (w < 3 || h < 3) return new Border(0, 0, 0, 0);
        bool ColumnsMatch(int a, int b)
        {
            for (var y = 0; y < h; y++) if (Diff(img[a, y], img[b, y]) > tolerance) return false;
            return true;
        }
        bool RowsMatch(int a, int b)
        {
            for (var x = 0; x < w; x++) if (Diff(img[x, a], img[x, b]) > tolerance) return false;
            return true;
        }
        int cx = w / 2, cy = h / 2;
        var left = cx; while (left > 0 && ColumnsMatch(left - 1, cx)) left--;
        var right = cx; while (right < w - 1 && ColumnsMatch(right + 1, cx)) right++;
        var top = cy; while (top > 0 && RowsMatch(top - 1, cy)) top--;
        var bottom = cy; while (bottom < h - 1 && RowsMatch(bottom + 1, cy)) bottom++;
        return new Border(left, top, w - 1 - right, h - 1 - bottom);
    }
}
