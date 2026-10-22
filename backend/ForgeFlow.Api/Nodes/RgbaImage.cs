using SkiaSharp;

namespace ForgeFlow.Nodes;

public readonly record struct Rgba(byte R, byte G, byte B, byte A);

public readonly record struct Rect(int X, int Y, int Width, int Height);

/// <summary>Straight-alpha RGBA8 pixel buffer decoded/encoded with SkiaSharp.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    readonly byte[] _px;

    public RgbaImage(int width, int height, Rgba fill = default)
    {
        Width = width;
        Height = height;
        _px = new byte[width * height * 4];
        if (fill != default)
            for (var i = 0; i < _px.Length; i += 4) { _px[i] = fill.R; _px[i + 1] = fill.G; _px[i + 2] = fill.B; _px[i + 3] = fill.A; }
    }

    RgbaImage(int width, int height, byte[] px) { Width = width; Height = height; _px = px; }

    public Rgba this[int x, int y]
    {
        get { var i = (y * Width + x) * 4; return new Rgba(_px[i], _px[i + 1], _px[i + 2], _px[i + 3]); }
        set { var i = (y * Width + x) * 4; _px[i] = value.R; _px[i + 1] = value.G; _px[i + 2] = value.B; _px[i + 3] = value.A; }
    }

    public static RgbaImage Load(string path)
    {
        using var decoded = SKBitmap.Decode(path) ?? throw new NodeException($"Cannot decode image {Path.GetFileName(path)}");
        return FromBitmap(decoded);
    }

    static RgbaImage FromBitmap(SKBitmap src)
    {
        var info = new SKImageInfo(src.Width, src.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        if (!src.CopyTo(bmp, SKColorType.Rgba8888))
        {
            using var canvas = new SKCanvas(bmp);
            canvas.DrawBitmap(src, 0, 0);
        }
        var bytes = new byte[info.BytesSize];
        System.Runtime.InteropServices.Marshal.Copy(bmp.GetPixels(), bytes, 0, bytes.Length);
        return new RgbaImage(src.Width, src.Height, bytes);
    }

    public SKBitmap ToBitmap()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bmp = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(_px, 0, bmp.GetPixels(), _px.Length);
        return bmp;
    }

    public byte[] ToPng()
    {
        using var bmp = ToBitmap();
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public RgbaImage Crop(Rect r)
    {
        var px = new byte[r.Width * r.Height * 4];
        for (var y = 0; y < r.Height; y++)
            Buffer.BlockCopy(_px, ((r.Y + y) * Width + r.X) * 4, px, y * r.Width * 4, r.Width * 4);
        return new RgbaImage(r.Width, r.Height, px);
    }

    /// <summary>Draws images onto a grid; each is scaled to fit its cell and centered.</summary>
    public static byte[] Collage(IReadOnlyList<string> paths, int cols, int cell, int gap, SKColor background)
    {
        var rows = (int)Math.Ceiling(paths.Count / (double)cols);
        var info = new SKImageInfo(cols * cell + (cols + 1) * gap, rows * cell + (rows + 1) * gap, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(background);
        using var paint = new SKPaint { IsAntialias = true };
        var sampling = new SKSamplingOptions(SKCubicResampler.Mitchell);
        for (var i = 0; i < paths.Count; i++)
        {
            using var img = SKImage.FromEncodedData(paths[i]) ?? throw new NodeException($"Cannot decode {Path.GetFileName(paths[i])}");
            var scale = Math.Min((float)cell / img.Width, (float)cell / img.Height);
            float w = img.Width * scale, h = img.Height * scale;
            int cx = i % cols, cy = i / cols;
            var x = gap + cx * (cell + gap) + (cell - w) / 2;
            var y = gap + cy * (cell + gap) + (cell - h) / 2;
            canvas.DrawImage(img, SKRect.Create(x, y, w, h), sampling, paint);
        }
        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
