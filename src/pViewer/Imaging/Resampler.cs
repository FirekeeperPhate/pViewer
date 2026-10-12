namespace pViewer.Imaging;

/// <summary>
/// Resizing with a proper filter: Lanczos 3 when the image gets smaller (every source pixel that
/// falls under a new one counts, so fine patterns do not turn into moiré), bicubic when it gets
/// larger. Two passes, columns then rows; colors are weighted by their alpha, or the invisible
/// color of transparent pixels would bleed into the edges.
/// </summary>
internal static class Resampler
{
    public static PixelBuffer Resize(PixelBuffer src, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        if (width == src.Width && height == src.Height) return src;
        bool down = (long)width * height < (long)src.Width * src.Height;
        Func<double, double> kernel = down ? Lanczos3 : Bicubic;
        double radius = down ? 3 : 2;

        var columns = Weights(src.Width, width, kernel, radius);
        var rows = Weights(src.Height, height, kernel, radius);
        int sw = src.Width, sh = src.Height;
        byte[] source = src.Data;

        // Pass 1: every source row to the new width (premultiplied, kept as floats so that the
        // negative lobes of the filter are not lost between the passes).
        var wide = new float[ImageBridge.BufferSize(width, sh)];
        Parallel.For(0, sh, y =>
        {
            int rowStart = y * sw * 4, outStart = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                var (first, weights) = columns[x];
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    int p = rowStart + (first + k) * 4;
                    float alpha = source[p + 3] * weights[k];
                    b += source[p] * alpha;
                    g += source[p + 1] * alpha;
                    r += source[p + 2] * alpha;
                    a += alpha;
                }
                int o = outStart + x * 4;
                wide[o] = b; wide[o + 1] = g; wide[o + 2] = r; wide[o + 3] = a;
            }
        });

        // Pass 2: the columns of that to the new height, back to plain 8-bit colors.
        var result = new PixelBuffer(width, height);
        byte[] target = result.Data;
        Parallel.For(0, height, y =>
        {
            var (first, weights) = rows[y];
            for (int x = 0; x < width; x++)
            {
                float b = 0, g = 0, r = 0, a = 0;
                for (int k = 0; k < weights.Length; k++)
                {
                    int p = ((first + k) * width + x) * 4;
                    float w = weights[k];
                    b += wide[p] * w; g += wide[p + 1] * w; r += wide[p + 2] * w; a += wide[p + 3] * w;
                }
                int o = (y * width + x) * 4;
                if (a <= 0.5f)
                {
                    target[o] = target[o + 1] = target[o + 2] = target[o + 3] = 0;
                    continue;
                }
                // The colors were multiplied by an alpha of 0-255: divide it out again.
                target[o] = ToByte(b / a);
                target[o + 1] = ToByte(g / a);
                target[o + 2] = ToByte(r / a);
                target[o + 3] = ToByte(a);
            }
        });
        return result;
    }

    private static byte ToByte(float value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + 0.5f);

    /// <summary>For each new pixel: the first old pixel under the filter and the weight of each one from there.</summary>
    private static (int First, float[] Weights)[] Weights(int oldSize, int newSize, Func<double, double> kernel, double radius)
    {
        double ratio = (double)oldSize / newSize;
        double scale = Math.Max(1, ratio); // a smaller image: the filter widens to cover the pixels merged into one
        double support = radius * scale;
        var all = new (int, float[])[newSize];
        for (int i = 0; i < newSize; i++)
        {
            double center = (i + 0.5) * ratio - 0.5;
            int first = Math.Max(0, (int)Math.Ceiling(center - support));
            int last = Math.Min(oldSize - 1, (int)Math.Floor(center + support));
            if (last < first) first = last = Math.Clamp((int)Math.Round(center), 0, oldSize - 1);
            var weights = new float[last - first + 1];
            double sum = 0;
            for (int k = 0; k < weights.Length; k++)
            {
                double w = kernel((first + k - center) / scale);
                weights[k] = (float)w;
                sum += w;
            }
            if (Math.Abs(sum) < 1e-9)
            {
                // Only the outer zeros of the filter fell on pixels: take the nearest one.
                Array.Clear(weights);
                weights[Math.Clamp((int)Math.Round(center) - first, 0, weights.Length - 1)] = 1;
            }
            else
            {
                for (int k = 0; k < weights.Length; k++) weights[k] = (float)(weights[k] / sum);
            }
            all[i] = (first, weights);
        }
        return all;
    }

    private static double Lanczos3(double x)
    {
        x = Math.Abs(x);
        if (x < 1e-9) return 1;
        if (x >= 3) return 0;
        double px = Math.PI * x;
        return 3 * Math.Sin(px) * Math.Sin(px / 3) / (px * px);
    }

    /// <summary>Catmull-Rom (the usual "bicubic", a = -0.5).</summary>
    private static double Bicubic(double x)
    {
        x = Math.Abs(x);
        if (x <= 1) return (1.5 * x - 2.5) * x * x + 1;
        if (x < 2) return ((-0.5 * x + 2.5) * x - 4) * x + 2;
        return 0;
    }
}
