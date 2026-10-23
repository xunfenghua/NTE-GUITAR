using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace MidiAutoPlayer.Utils;

public static class IconBuilder
{
    public static void InspectAndBuild(string inputPath, string outputIcoPath)
    {
        using var original = new Bitmap(inputPath);
        Console.WriteLine($"Image loaded: {original.Width}x{original.Height}, PixelFormat: {original.PixelFormat}");

        // Inspect corners
        Console.WriteLine($"Corner (0,0): {original.GetPixel(0, 0)}");
        Console.WriteLine($"Corner (w-1,0): {original.GetPixel(original.Width - 1, 0)}");
        Console.WriteLine($"Corner (0,h-1): {original.GetPixel(0, original.Height - 1)}");
        Console.WriteLine($"Corner (w-1,h-1): {original.GetPixel(original.Width - 1, original.Height - 1)}");

        // Create transparent cutout version using flood fill from border
        var transparentBmp = CreateTransparentCutout(original);

        // Also save a preview png to inspect
        string previewPng = Path.Combine(Path.GetDirectoryName(outputIcoPath)!, "icon_preview.png");
        transparentBmp.Save(previewPng, ImageFormat.Png);
        Console.WriteLine($"Saved preview to: {previewPng}");

        // Build multi-size .ico: 256, 128, 64, 48, 32, 16
        BuildIcoFile(transparentBmp, outputIcoPath);
        Console.WriteLine($"Successfully generated ICO: {outputIcoPath}");
    }

    public static Bitmap CreateTransparentCutout(Bitmap src)
    {
        int w = src.Width;
        int h = src.Height;
        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        // Copy source pixels to 32bpp Argb
        using (var g = Graphics.FromImage(result))
        {
            g.DrawImage(src, 0, 0, w, h);
        }

        // Flood fill from all outer edge pixels that are black/near-black (threshold)
        bool[,] visited = new bool[w, h];
        var queue = new Queue<Point>();

        bool IsBlack(Color c)
        {
            // JPEG black background usually has small compression artifacts (R<18, G<18, B<18)
            return c.R <= 20 && c.G <= 20 && c.B <= 20;
        }

        // Add border pixels
        for (int x = 0; x < w; x++)
        {
            if (IsBlack(result.GetPixel(x, 0))) { queue.Enqueue(new Point(x, 0)); visited[x, 0] = true; }
            if (IsBlack(result.GetPixel(x, h - 1))) { queue.Enqueue(new Point(x, h - 1)); visited[x, h - 1] = true; }
        }
        for (int y = 0; y < h; y++)
        {
            if (IsBlack(result.GetPixel(0, y))) { queue.Enqueue(new Point(0, y)); visited[0, y] = true; }
            if (IsBlack(result.GetPixel(w - 1, y))) { queue.Enqueue(new Point(w - 1, y)); visited[w - 1, y] = true; }
        }

        int[] dx = { 1, -1, 0, 0 };
        int[] dy = { 0, 0, 1, -1 };

        while (queue.Count > 0)
        {
            var p = queue.Dequeue();
            result.SetPixel(p.X, p.Y, Color.FromArgb(0, 0, 0, 0));

            for (int i = 0; i < 4; i++)
            {
                int nx = p.X + dx[i];
                int ny = p.Y + dy[i];

                if (nx >= 0 && nx < w && ny >= 0 && ny < h && !visited[nx, ny])
                {
                    visited[nx, ny] = true;
                    if (IsBlack(result.GetPixel(nx, ny)))
                    {
                        queue.Enqueue(new Point(nx, ny));
                    }
                }
            }
        }

        // Soften edge pixels (simple antialiasing for boundary pixels with R,G,B < 40)
        for (int y = 1; y < h - 1; y++)
        {
            for (int x = 1; x < w - 1; x++)
            {
                if (result.GetPixel(x, y).A != 0)
                {
                    // Check if adjacent to transparent
                    bool adjacentToTransparent =
                        result.GetPixel(x + 1, y).A == 0 ||
                        result.GetPixel(x - 1, y).A == 0 ||
                        result.GetPixel(x, y + 1).A == 0 ||
                        result.GetPixel(x, y - 1).A == 0;

                    if (adjacentToTransparent)
                    {
                        var c = result.GetPixel(x, y);
                        if (c.R <= 40 && c.G <= 40 && c.B <= 40)
                        {
                            // Blend alpha based on brightness
                            int maxVal = Math.Max(c.R, Math.Max(c.G, c.B));
                            int alpha = Math.Clamp(maxVal * 255 / 40, 30, 255);
                            result.SetPixel(x, y, Color.FromArgb(alpha, c.R, c.G, c.B));
                        }
                    }
                }
            }
        }

        return result;
    }

    public static void BuildIcoFile(Bitmap source, string outputPath)
    {
        int[] sizes = new[] { 256, 128, 64, 48, 32, 16 };
        var pngDataList = new List<byte[]>();

        foreach (int size in sizes)
        {
            using var resized = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(resized))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.DrawImage(source, new Rectangle(0, 0, size, size));
            }

            using var ms = new MemoryStream();
            resized.Save(ms, ImageFormat.Png);
            pngDataList.Add(ms.ToArray());
        }

        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // 1. ICONDIR (6 bytes)
        bw.Write((ushort)0); // Reserved
        bw.Write((ushort)1); // Type = 1 (Icon)
        bw.Write((ushort)sizes.Length); // Image count

        // Calculate offset: 6 (ICONDIR) + 16 * count (ICONDIRENTRY)
        uint offset = (uint)(6 + 16 * sizes.Length);

        // 2. ICONDIRENTRY (16 bytes each)
        for (int i = 0; i < sizes.Length; i++)
        {
            int size = sizes[i];
            byte[] data = pngDataList[i];

            bw.Write((byte)(size >= 256 ? 0 : size)); // Width (0 means 256)
            bw.Write((byte)(size >= 256 ? 0 : size)); // Height (0 means 256)
            bw.Write((byte)0); // Color count
            bw.Write((byte)0); // Reserved
            bw.Write((ushort)1); // Planes
            bw.Write((ushort)32); // Bit count
            bw.Write((uint)data.Length); // Bytes in res
            bw.Write(offset); // Image offset

            offset += (uint)data.Length;
        }

        // 3. Image data (PNG bytes)
        for (int i = 0; i < sizes.Length; i++)
        {
            bw.Write(pngDataList[i]);
        }
    }
}
