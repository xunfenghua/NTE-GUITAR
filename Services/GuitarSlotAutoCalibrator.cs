using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MidiAutoPlayer.Services;

public sealed record SlotDetectionCandidate(
    int Slot,
    double X,
    double Y,
    double Width,
    double Height,
    double MatchScore);

public sealed record SlotDetectionResult(
    bool Success,
    string? Reason,
    IReadOnlyList<SlotDetectionCandidate> Candidates);

/// <summary>
/// Finds high-contrast closed block edges near the user's existing calibrated slot centers.
/// MatchScore is an internal ranking value, not a probability.
/// </summary>
public sealed class GuitarSlotAutoCalibrator
{
    private const double MinimumEdgeContrast = 0.045;
    private const double SearchRadiusByClientHeight = 0.035;
    private const double MinimumHalfSizeByClientHeight = 0.018;
    private const double MaximumHalfSizeByClientHeight = 0.065;

    public SlotDetectionResult Detect(Bitmap source, IReadOnlyList<(double X, double Y)> seeds)
    {
        if (source.Width < 320 || source.Height < 240)
            return Failed("游戏客户区太小，无法可靠识别吉他槽位。");
        if (seeds.Count != 6 || seeds.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X is < 0 or > 1 || p.Y is < 0 or > 1))
            return Failed("吉他槽位初始坐标无效，请先检查手动校准。");

        using var normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(normalized))
            graphics.DrawImageUnscaled(source, 0, 0);

        var colors = ReadRgb(normalized);
        var candidates = new List<SlotDetectionCandidate>(6);
        int minDimension = Math.Min(source.Width, source.Height);
        int radius = Math.Max(8, (int)Math.Round(source.Height * SearchRadiusByClientHeight));
        int step = Math.Clamp(source.Height / 540, 1, 4);
        int minHalfSize = Math.Max(6, (int)Math.Round(source.Height * MinimumHalfSizeByClientHeight));
        int maxHalfSize = Math.Max(minHalfSize + 2, (int)Math.Round(source.Height * MaximumHalfSizeByClientHeight));
        var halfSizes = new[] { minHalfSize, minHalfSize * 1.4, minHalfSize * 1.8, minHalfSize * 2.2, maxHalfSize }
            .Select(v => Math.Clamp((int)Math.Round(v), minHalfSize, maxHalfSize))
            .Distinct()
            .ToArray();

        for (int i = 0; i < seeds.Count; i++)
        {
            int seedX = (int)Math.Round(seeds[i].X * (source.Width - 1));
            int seedY = (int)Math.Round(seeds[i].Y * (source.Height - 1));
            CandidateScore? best = null;

            for (int cy = Math.Max(maxHalfSize + 2, seedY - radius); cy <= Math.Min(source.Height - maxHalfSize - 3, seedY + radius); cy += step)
            for (int cx = Math.Max(maxHalfSize + 2, seedX - radius); cx <= Math.Min(source.Width - maxHalfSize - 3, seedX + radius); cx += step)
            foreach (int halfSize in halfSizes)
            {
                double contrast = ScoreClosedEdge(colors, source.Width, source.Height, cx, cy, halfSize);
                double dx = (cx - seedX) / (double)radius;
                double dy = (cy - seedY) / (double)radius;
                double prior = Math.Exp(-1.25 * (dx * dx + dy * dy));
                double score = contrast * (0.72 + 0.28 * prior);
                if (best is null || score > best.Value.Score)
                    best = new CandidateScore(cx, cy, halfSize, contrast, score);
            }

            if (best is null || best.Value.Contrast < MinimumEdgeContrast)
                return Failed($"Slot {i + 1} 附近没有找到足够清晰的槽位边缘。请确保六块吉他界面可见，或使用手动校准。");

            var hit = best.Value;
            candidates.Add(new SlotDetectionCandidate(
                i + 1,
                hit.X / (double)(source.Width - 1),
                hit.Y / (double)(source.Height - 1),
                (hit.HalfSize * 2.0) / source.Width,
                (hit.HalfSize * 2.0) / source.Height,
                Math.Clamp(hit.Score, 0, 1)));
        }

        var detectedLayout = candidates.Select(c => (c.X, c.Y)).ToArray();
        double seedArea = SignedArea(seeds);
        double detectedArea = SignedArea(detectedLayout);
        if (Math.Sign(seedArea) != Math.Sign(detectedArea) || Math.Abs(detectedArea) < Math.Abs(seedArea) * 0.65)
            return Failed("六个候选没有保持原有环形顺序或布局，未应用自动校准。");

        double duplicateDistance = minDimension * 0.04;
        for (int i = 0; i < candidates.Count; i++)
        for (int j = i + 1; j < candidates.Count; j++)
        {
            double dx = (candidates[i].X - candidates[j].X) * source.Width;
            double dy = (candidates[i].Y - candidates[j].Y) * source.Height;
            if (Math.Sqrt(dx * dx + dy * dy) < duplicateDistance)
                return Failed("多个槽位匹配到了同一处画面区域，未应用自动校准。");
        }

        return new SlotDetectionResult(true, null, candidates);
    }

    private static double ScoreClosedEdge(byte[] rgb, int width, int height, int cx, int cy, int halfSize)
    {
        double[] side = new double[4];
        int samples = 10;
        for (int i = 0; i < samples; i++)
        {
            double t = -0.72 + 1.44 * i / (samples - 1.0);
            side[0] += ContrastAt(rgb, width, height, cx + (int)(t * halfSize), cy - halfSize, cx + (int)(t * halfSize), cy - (int)(halfSize * 1.24));
            side[1] += ContrastAt(rgb, width, height, cx + (int)(t * halfSize), cy + halfSize, cx + (int)(t * halfSize), cy + (int)(halfSize * 1.24));
            side[2] += ContrastAt(rgb, width, height, cx - halfSize, cy + (int)(t * halfSize), cx - (int)(halfSize * 1.24), cy + (int)(t * halfSize));
            side[3] += ContrastAt(rgb, width, height, cx + halfSize, cy + (int)(t * halfSize), cx + (int)(halfSize * 1.24), cy + (int)(t * halfSize));
        }

        for (int i = 0; i < side.Length; i++) side[i] /= samples;
        Array.Sort(side);
        return side[0] * 0.55 + side[1] * 0.20 + side[2] * 0.15 + side[3] * 0.10;
    }

    private static double ContrastAt(byte[] rgb, int width, int height, int x1, int y1, int x2, int y2)
    {
        int a = (Math.Clamp(y1, 0, height - 1) * width + Math.Clamp(x1, 0, width - 1)) * 3;
        int b = (Math.Clamp(y2, 0, height - 1) * width + Math.Clamp(x2, 0, width - 1)) * 3;
        int difference = Math.Max(Math.Abs(rgb[a] - rgb[b]), Math.Max(Math.Abs(rgb[a + 1] - rgb[b + 1]), Math.Abs(rgb[a + 2] - rgb[b + 2])));
        return difference / 255.0;
    }

    private static double SignedArea(IReadOnlyList<(double X, double Y)> points)
    {
        double area = 0;
        for (int i = 0; i < points.Count; i++)
        {
            var current = points[i];
            var next = points[(i + 1) % points.Count];
            area += current.X * next.Y - next.X * current.Y;
        }
        return area / 2.0;
    }

    private static byte[] ReadRgb(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        BitmapData data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] row = new byte[Math.Abs(data.Stride)];
            byte[] rgb = new byte[bitmap.Width * bitmap.Height * 3];
            for (int y = 0; y < bitmap.Height; y++)
            {
                IntPtr rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                Marshal.Copy(rowPtr, row, 0, row.Length);
                int pixelOffset = 0;
                for (int x = 0; x < bitmap.Width; x++, pixelOffset += 4)
                {
                    int colorOffset = (y * bitmap.Width + x) * 3;
                    rgb[colorOffset] = row[pixelOffset + 2];
                    rgb[colorOffset + 1] = row[pixelOffset + 1];
                    rgb[colorOffset + 2] = row[pixelOffset];
                }
            }
            return rgb;
        }
        finally
        {
            bitmap.UnlockBits(data);
        }
    }

    private static SlotDetectionResult Failed(string reason) => new(false, reason, Array.Empty<SlotDetectionCandidate>());

    private readonly record struct CandidateScore(int X, int Y, int HalfSize, double Contrast, double Score);
}
