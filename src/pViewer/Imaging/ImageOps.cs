using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WColor = System.Windows.Media.Color;

namespace pViewer.Imaging;

/// <summary>Text to print on the image; coordinates and sizes in image pixels.</summary>
public sealed record TextPlacement(
    string Text, double X, double Y, string FontFamily, double FontSize, bool Bold, bool Italic, WColor Color);

/// <summary>
/// Editing operations. They take a frozen BitmapSource and return a new one:
/// the original stays untouched (undo needs it). The ones that draw with WPF must
/// run on an STA thread (see <see cref="StaTask"/>).
/// </summary>
public static class ImageOps
{
    public static BitmapSource Rotate(BitmapSource src, int degrees) =>
        ImageBridge.Materialize(new TransformedBitmap(src, new RotateTransform(degrees)));

    public static BitmapSource Flip(BitmapSource src, bool horizontal) =>
        ImageBridge.Materialize(new TransformedBitmap(src, horizontal ? new ScaleTransform(-1, 1) : new ScaleTransform(1, -1)));

    public static Int32Rect ClampRect(BitmapSource src, Int32Rect r)
    {
        int x = Math.Clamp(r.X, 0, src.PixelWidth);
        int y = Math.Clamp(r.Y, 0, src.PixelHeight);
        int right = Math.Clamp(r.X + r.Width, 0, src.PixelWidth);
        int bottom = Math.Clamp(r.Y + r.Height, 0, src.PixelHeight);
        return new Int32Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    public static BitmapSource Crop(BitmapSource src, Int32Rect rect)
    {
        rect = ClampRect(src, rect);
        if (rect.Width < 1 || rect.Height < 1) return src;
        return ImageBridge.Materialize(new CroppedBitmap(src, rect));
    }

    public static BitmapSource Resize(BitmapSource src, int width, int height) =>
        ImageBridge.Process(src, p => Resampler.Resize(p, width, height));

    /// <summary>Scales down proportionally to fit within maxW×maxH (never enlarges).</summary>
    public static BitmapSource FitWithin(BitmapSource src, int maxW, int maxH)
    {
        double ratio = Math.Min((double)maxW / src.PixelWidth, (double)maxH / src.PixelHeight);
        if (ratio >= 1) return src;
        return Resize(src, (int)Math.Round(src.PixelWidth * ratio), (int)Math.Round(src.PixelHeight * ratio));
    }

    // ---- Color filters: each new color is a weighted sum of the old red, green and blue ----
    // (the matrices of the CSS/SVG filters of the same names; alpha is never touched)

    /// <param name="matrices">
    /// Applied one after the other. Each: three rows (new red, green, blue) of four numbers:
    /// × red, × green, × blue, + offset (where 1 is full brightness).
    /// </param>
    private static BitmapSource Recolor(BitmapSource src, params float[][] matrices) => ImageBridge.Process(src, p =>
    {
        byte[] d = p.Data;
        foreach (var m in matrices)
        {
            Parallel.For(0, p.Height, y =>
            {
                int end = (y + 1) * p.Stride;
                for (int i = y * p.Stride; i < end; i += 4)
                {
                    float b = d[i], g = d[i + 1], r = d[i + 2];
                    d[i + 2] = ToByte(m[0] * r + m[1] * g + m[2] * b + m[3] * 255);
                    d[i + 1] = ToByte(m[4] * r + m[5] * g + m[6] * b + m[7] * 255);
                    d[i] = ToByte(m[8] * r + m[9] * g + m[10] * b + m[11] * 255);
                }
            });
        }
        return p;
    });

    private static byte ToByte(float value) => value <= 0 ? (byte)0 : value >= 255 ? (byte)255 : (byte)(value + 0.5f);

    public static BitmapSource Invert(BitmapSource src) => Recolor(src, [-1, 0, 0, 1, 0, -1, 0, 1, 0, 0, -1, 1]);

    public static BitmapSource Grayscale(BitmapSource src) =>
        Recolor(src, [.2126f, .7152f, .0722f, 0, .2126f, .7152f, .0722f, 0, .2126f, .7152f, .0722f, 0]);

    public static BitmapSource Sepia(BitmapSource src) =>
        Recolor(src, [.393f, .769f, .189f, 0, .349f, .686f, .168f, 0, .272f, .534f, .131f, 0]);

    /// <summary>Pure black and white (formerly "1 bit").</summary>
    public static BitmapSource BlackWhite(BitmapSource src) => ImageBridge.Process(src, p =>
    {
        // By luminance, keeping alpha (a threshold on the whole pixel would turn the
        // transparent areas of a PNG solid black).
        byte[] d = p.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            byte v = 0.299 * d[i + 2] + 0.587 * d[i + 1] + 0.114 * d[i] >= 128 ? (byte)255 : (byte)0;
            d[i] = d[i + 1] = d[i + 2] = v;
        }
        return p;
    });

    private static float[] BrightnessMatrix(float amount) => [amount, 0, 0, 0, 0, amount, 0, 0, 0, 0, amount, 0];

    private static float[] ContrastMatrix(float amount)
    {
        float offset = 0.5f - 0.5f * amount; // around middle gray
        return [amount, 0, 0, offset, 0, amount, 0, offset, 0, 0, amount, offset];
    }

    private static float[] SaturationMatrix(float amount) =>
    [
        .213f + .787f * amount, .715f - .715f * amount, .072f - .072f * amount, 0,
        .213f - .213f * amount, .715f + .285f * amount, .072f - .072f * amount, 0,
        .213f - .213f * amount, .715f - .715f * amount, .072f + .928f * amount, 0,
    ];

    private static float[] HueMatrix(float degrees)
    {
        float cos = MathF.Cos(degrees * MathF.PI / 180), sin = MathF.Sin(degrees * MathF.PI / 180);
        return
        [
            .213f + cos * .787f - sin * .213f, .715f - cos * .715f - sin * .715f, .072f - cos * .072f + sin * .928f, 0,
            .213f - cos * .213f + sin * .143f, .715f + cos * .285f + sin * .140f, .072f - cos * .072f - sin * .283f, 0,
            .213f - cos * .213f - sin * .787f, .715f - cos * .715f + sin * .715f, .072f + cos * .928f + sin * .072f, 0,
        ];
    }

    /// <param name="brightness">-100..100</param>
    /// <param name="contrast">-100..100</param>
    public static BitmapSource BrightnessContrast(BitmapSource src, double brightness, double contrast)
    {
        if (brightness == 0 && contrast == 0) return src;
        var steps = new List<float[]>();
        if (brightness != 0) steps.Add(BrightnessMatrix((float)(1 + brightness / 100)));
        if (contrast != 0) steps.Add(ContrastMatrix((float)(1 + contrast / 100)));
        return Recolor(src, [.. steps]);
    }

    public static BitmapSource Sharpen(BitmapSource src, double sigma) => sigma <= 0 ? src
        : ImageBridge.Process(src, p => SkiaImaging.Sharpen(p, (float)sigma));

    public static BitmapSource Blur(BitmapSource src, double sigma) => sigma <= 0 ? src
        : ImageBridge.Process(src, p => SkiaImaging.Blur(p, (float)sigma));

    /// <param name="hue">-180..180 degrees</param>
    /// <param name="saturation">-100..100</param>
    public static BitmapSource HueSaturation(BitmapSource src, double hue, double saturation)
    {
        if (hue == 0 && saturation == 0) return src;
        var steps = new List<float[]>();
        if (hue != 0) steps.Add(HueMatrix((float)hue));
        if (saturation != 0) steps.Add(SaturationMatrix((float)(1 + saturation / 100)));
        return Recolor(src, [.. steps]);
    }

    public static BitmapSource AddBorder(BitmapSource src, int thickness, WColor color) => ImageBridge.Process(src, p =>
    {
        var framed = new PixelBuffer(p.Width + thickness * 2, p.Height + thickness * 2);
        byte[] d = framed.Data;
        for (int i = 0; i < d.Length; i += 4)
        {
            d[i] = color.B; d[i + 1] = color.G; d[i + 2] = color.R; d[i + 3] = color.A;
        }
        for (int y = 0; y < p.Height; y++)
            Buffer.BlockCopy(p.Data, y * p.Stride, d, ((y + thickness) * framed.Width + thickness) * 4, p.Stride);
        return framed;
    });

    /// <summary>Reduces red where it clearly dominates green and blue (red eyes).</summary>
    public static BitmapSource RedEye(BitmapSource src, Int32Rect area)
    {
        area = ClampRect(src, area);
        if (area.Width < 1 || area.Height < 1) return src;
        return ImageBridge.Process(src, p =>
        {
            byte[] d = p.Data;
            for (int y = area.Y; y < area.Y + area.Height; y++)
            {
                for (int x = area.X, i = (y * p.Width + x) * 4; x < area.X + area.Width; x++, i += 4)
                {
                    int red = d[i + 2], avg = (d[i + 1] + d[i]) / 2;
                    if (red > 60 && red > avg * 1.5) d[i + 2] = (byte)avg;
                }
            }
            return p;
        });
    }

    // ---- Operations that draw with WPF (STA thread) ----

    /// <param name="aliased">Hard edges: shapes on integer pixel edges stay crisp instead of
    /// getting half-transparent anti-aliased borders.</param>
    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw, bool aliased = false)
    {
        var visual = new DrawingVisual();
        if (aliased) RenderOptions.SetEdgeMode(visual, EdgeMode.Aliased);
        using (var dc = visual.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static Rect FullRect(BitmapSource src) => new(0, 0, src.PixelWidth, src.PixelHeight);

    /// <summary>The rendered image is 96 dpi: give it back the resolution of the original (a 300 dpi scan stays 300).</summary>
    private static BitmapSource KeepDpi(BitmapSource original, BitmapSource rendered) =>
        original.DpiX == rendered.DpiX && original.DpiY == rendered.DpiY ? rendered
            : ImageBridge.WithDpi(rendered, original.DpiX, original.DpiY);

    public static BitmapSource DrawRectangle(BitmapSource src, Int32Rect rect, WColor color, double thickness, bool fill)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return KeepDpi(src, Render(src.PixelWidth, src.PixelHeight, dc =>
        {
            dc.DrawImage(src, FullRect(src));
            var r = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            if (fill) dc.DrawRectangle(brush, null, r);
            else
            {
                // Four filled bands on whole pixels, centred on the edges like a pen would be: a pen
                // of odd width centred on an integer edge would fall on half pixels (soft edges).
                int t = Math.Max(1, (int)Math.Round(thickness));
                int before = t / 2;
                int left = rect.X - before, top = rect.Y - before;
                int outerW = rect.Width + t, outerH = rect.Height + t;
                dc.DrawRectangle(brush, null, new Rect(left, top, outerW, t));                    // top
                dc.DrawRectangle(brush, null, new Rect(left, top + outerH - t, outerW, t));       // bottom
                // A rectangle lower than the pen: top and bottom already cover it all.
                int sideH = outerH - 2 * t;
                if (sideH > 0)
                {
                    dc.DrawRectangle(brush, null, new Rect(left, top + t, t, sideH));               // left
                    dc.DrawRectangle(brush, null, new Rect(left + outerW - t, top + t, t, sideH));  // right
                }
            }
        }, aliased: true));
    }

    public static FormattedText CreateFormattedText(TextPlacement t)
    {
        var brush = new SolidColorBrush(t.Color);
        brush.Freeze();
        var typeface = new Typeface(new FontFamily(t.FontFamily),
            t.Italic ? FontStyles.Italic : FontStyles.Normal,
            t.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStretches.Normal);
        return new FormattedText(t.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, Math.Max(1, t.FontSize), brush, 1.0);
    }

    public static BitmapSource DrawText(BitmapSource src, TextPlacement text) =>
        KeepDpi(src, Render(src.PixelWidth, src.PixelHeight, dc =>
        {
            dc.DrawImage(src, FullRect(src));
            dc.DrawText(CreateFormattedText(text), new Point(text.X, text.Y));
        }));

    /// <summary>Joins side-by-side pages (manga/comic mode) into a single image.</summary>
    public static BitmapSource Compose(IReadOnlyList<BitmapSource> pages, WColor background)
    {
        if (pages.Count == 1) return pages[0];
        int width = pages.Sum(p => p.PixelWidth);
        int height = pages.Max(p => p.PixelHeight);
        var bg = new SolidColorBrush(background);
        bg.Freeze();
        return Render(width, height, dc =>
        {
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            double x = 0;
            foreach (var p in pages)
            {
                // Whole pixels: a half-pixel offset (odd height difference) would resample the page.
                dc.DrawImage(p, new Rect(x, Math.Floor((height - p.PixelHeight) / 2.0), p.PixelWidth, p.PixelHeight));
                x += p.PixelWidth;
            }
        });
    }

    /// <summary>Flattens transparency onto a background color (for JPEG/BMP).</summary>
    public static BitmapSource FlattenAlpha(BitmapSource src, WColor background)
    {
        if (!ImageBridge.MayHaveAlpha(src.Format)) return src;
        var flat = ImageBridge.Process(src, p =>
        {
            byte[] d = p.Data;
            for (int i = 0; i < d.Length; i += 4)
            {
                int a = d[i + 3];
                if (a == 255) continue;
                d[i] = (byte)((d[i] * a + background.B * (255 - a) + 127) / 255);
                d[i + 1] = (byte)((d[i + 1] * a + background.G * (255 - a) + 127) / 255);
                d[i + 2] = (byte)((d[i + 2] * a + background.R * (255 - a) + 127) / 255);
                d[i + 3] = 255;
            }
            return p;
        });
        var bgr = new FormatConvertedBitmap(flat, PixelFormats.Bgr24, null, 0);
        bgr.Freeze();
        return bgr;
    }
}
